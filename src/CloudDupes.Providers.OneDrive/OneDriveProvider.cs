using System.Runtime.CompilerServices;
using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Kiota.Abstractions.Authentication;

namespace CloudDupes.Providers.OneDrive;

public sealed class OneDriveOptions
{
    /// <summary>Application (client) ID of your Entra ID app registration.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>"common" allows both personal and work/school accounts.</summary>
    public string Tenant { get; set; } = "common";

    public string CacheDirectory { get; set; } = "";
}

/// <summary>OneDrive (personal or business) via Microsoft Graph.</summary>
public sealed class OneDriveProvider : ICloudProvider, IDisposable
{
    static readonly string[] Scopes = ["Files.ReadWrite.All"];

    static readonly string[] DeltaSelect =
        ["id", "name", "size", "file", "folder", "root", "package", "parentReference", "lastModifiedDateTime", "eTag", "deleted", "webUrl"];

    readonly MsalAuth _auth;
    readonly GraphServiceClient _graph;
    readonly HttpClient _thumbnailHttp = new();
    string? _driveId;

    public OneDriveProvider(OneDriveOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new InvalidOperationException("OneDrive:ClientId is not configured. See README for the app registration steps.");
        _auth = new MsalAuth(options, Scopes);
        _graph = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(new TokenProvider(_auth)));
    }

    public CloudProviderKind Kind => CloudProviderKind.OneDrive;

    public async Task<string> SignInAsync(CancellationToken ct)
    {
        var result = await _auth.AcquireAsync(ct);
        return result.Account.Username;
    }

    async Task<string> DriveIdAsync(CancellationToken ct)
    {
        if (_driveId is not null) return _driveId;
        var drive = await _graph.Me.Drive.GetAsync(cancellationToken: ct);
        return _driveId = drive?.Id ?? throw new InvalidOperationException("Could not resolve the signed-in user's drive.");
    }

    public async IAsyncEnumerable<ScanBatch> ScanAsync(string? deltaToken, [EnumeratorCancellation] CancellationToken ct)
    {
        var driveId = await DriveIdAsync(ct);
        var reset = deltaToken is null;
        var page = await FetchDeltaAsync(driveId, deltaToken, ct);
        if (page is null)
        {
            // The saved delta link expired (HTTP 410): start over with a full enumeration.
            reset = true;
            page = await FetchDeltaAsync(driveId, null, ct)
                ?? throw new InvalidOperationException("OneDrive refused a full delta enumeration.");
        }

        while (true)
        {
            yield return ToBatch(page.Value ?? []) with { Reset = reset, NextToken = page.OdataDeltaLink };
            reset = false;

            if (page.OdataNextLink is null) yield break;
            page = await FetchDeltaAsync(driveId, page.OdataNextLink, ct)
                ?? throw new InvalidOperationException("OneDrive asked for a resync mid-scan; run 'scan --full'.");
        }
    }

    /// <summary>Null means the delta link is no longer valid and a full resync is required.</summary>
    async Task<DeltaGetResponse?> FetchDeltaAsync(string driveId, string? url, CancellationToken ct)
    {
        var delta = _graph.Drives[driveId].Items["root"].Delta;
        try
        {
            return url is null
                ? await delta.GetAsDeltaGetResponseAsync(rc => rc.QueryParameters.Select = DeltaSelect, ct)
                : await delta.WithUrl(url).GetAsDeltaGetResponseAsync(cancellationToken: ct);
        }
        catch (ODataError e) when (e.ResponseStatusCode == 410)
        {
            return null;
        }
    }

    // Delta responses don't include parentReference.path, so paths are rebuilt from folder ids by the store.
    static ScanBatch ToBatch(List<DriveItem> items)
    {
        var files = new List<CloudFile>();
        var folders = new List<FolderEntry>();
        var deleted = new List<string>();

        foreach (var item in items)
        {
            if (item.Id is null) continue;
            if (item.Deleted is not null)
            {
                deleted.Add(item.Id);
            }
            else if (item.Root is not null)
            {
                folders.Add(new FolderEntry(item.Id, null, ""));
            }
            else if (item.Folder is not null || item.Package is not null)
            {
                // Packages (e.g. OneNote notebooks) are treated as folders so nothing inside them gets deleted on its own.
                folders.Add(new FolderEntry(item.Id, item.ParentReference?.Id, item.Name ?? ""));
            }
            else if (item.File is not null)
            {
                var hashes = item.File.Hashes;
                files.Add(new CloudFile
                {
                    Provider = CloudProviderKind.OneDrive,
                    Id = item.Id,
                    Name = item.Name ?? "",
                    ParentId = item.ParentReference?.Id,
                    Size = item.Size ?? 0,
                    Modified = item.LastModifiedDateTime ?? default,
                    Version = item.ETag,
                    MimeType = item.File.MimeType,
                    WebUrl = item.WebUrl,
                    QuickXor = hashes?.QuickXorHash,
                    Sha1 = hashes?.Sha1Hash?.ToLowerInvariant(),
                    Sha256 = hashes?.Sha256Hash?.ToLowerInvariant(),
                });
            }
        }
        return new ScanBatch(files, folders, deleted);
    }

    public async Task<Stream> OpenContentAsync(string id, CancellationToken ct)
    {
        var driveId = await DriveIdAsync(ct);
        return await _graph.Drives[driveId].Items[id].Content.GetAsync(cancellationToken: ct)
            ?? throw new InvalidOperationException($"No content returned for {id}.");
    }

    public async Task<Stream?> OpenThumbnailAsync(string id, CancellationToken ct)
    {
        var driveId = await DriveIdAsync(ct);
        ThumbnailSetCollectionResponse? sets;
        try
        {
            sets = await _graph.Drives[driveId].Items[id].Thumbnails.GetAsync(cancellationToken: ct);
        }
        catch (ODataError e) when (e.ResponseStatusCode is 404 or 400)
        {
            return null;
        }

        var url = sets?.Value?.FirstOrDefault()?.Medium?.Url;
        if (url is null) return null;

        // Thumbnail URLs are pre-authenticated; no bearer token needed.
        var response = await _thumbnailHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<string?> GetCurrentVersionAsync(string id, CancellationToken ct)
    {
        var driveId = await DriveIdAsync(ct);
        try
        {
            var item = await _graph.Drives[driveId].Items[id].GetAsync(rc => rc.QueryParameters.Select = ["id", "eTag", "deleted"], ct);
            return item?.Deleted is null ? item?.ETag : null;
        }
        catch (ODataError e) when (e.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>DELETE on a drive item moves it to the OneDrive recycle bin.</summary>
    public async Task TrashAsync(string id, string? expectedVersion, CancellationToken ct)
    {
        var driveId = await DriveIdAsync(ct);
        await _graph.Drives[driveId].Items[id].DeleteAsync(rc =>
        {
            if (expectedVersion is not null) rc.Headers.Add("If-Match", expectedVersion);
        }, ct);
    }

    public void Dispose()
    {
        _graph.Dispose();
        _thumbnailHttp.Dispose();
    }

    sealed class TokenProvider(MsalAuth auth) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

        public async Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default) =>
            (await auth.AcquireAsync(cancellationToken)).AccessToken;
    }
}

/// <summary>MSAL public client with a persistent (DPAPI-protected on Windows) token cache.</summary>
sealed class MsalAuth
{
    readonly IPublicClientApplication _app;
    readonly string[] _scopes;
    readonly Lazy<Task> _cacheRegistration;
    readonly SemaphoreSlim _gate = new(1, 1);

    public MsalAuth(OneDriveOptions options, string[] scopes)
    {
        _scopes = scopes;
        _app = PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.Tenant)
            .WithRedirectUri("http://localhost")
            .Build();

        _cacheRegistration = new Lazy<Task>(async () =>
        {
            var properties = new StorageCreationPropertiesBuilder("onedrive.msal.cache", options.CacheDirectory).Build();
            var helper = await MsalCacheHelper.CreateAsync(properties);
            helper.RegisterCache(_app.UserTokenCache);
        });
    }

    public async Task<AuthenticationResult> AcquireAsync(CancellationToken ct)
    {
        await _cacheRegistration.Value;
        await _gate.WaitAsync(ct);
        try
        {
            var account = (await _app.GetAccountsAsync()).FirstOrDefault();
            if (account is not null)
            {
                try
                {
                    return await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct);
                }
                catch (MsalUiRequiredException)
                {
                    // Fall through to interactive sign-in.
                }
            }
            return await _app.AcquireTokenInteractive(_scopes).WithUseEmbeddedWebView(false).ExecuteAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
