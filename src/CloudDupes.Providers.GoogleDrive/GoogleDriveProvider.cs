using System.Net;
using System.Runtime.CompilerServices;
using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;
using CloudDupes.Core.Util;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Requests;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace CloudDupes.Providers.GoogleDrive;

public sealed class GoogleDriveOptions
{
    /// <summary>Path to the OAuth "Desktop app" client JSON downloaded from Google Cloud Console.</summary>
    public string ClientSecretsPath { get; set; } = "client_secret.json";

    public string CacheDirectory { get; set; } = "";
}

/// <summary>Google Drive ("My Drive", files you own) via the Drive v3 API.</summary>
public sealed class GoogleDriveProvider(GoogleDriveOptions options) : ICloudProvider, IDisposable
{
    const string FolderMime = "application/vnd.google-apps.folder";
    const string GoogleNativePrefix = "application/vnd.google-apps.";
    const string FileFields =
        "id,name,mimeType,size,md5Checksum,sha1Checksum,sha256Checksum,modifiedTime,parents,ownedByMe,trashed,version,webViewLink";

    readonly SemaphoreSlim _gate = new(1, 1);
    DriveService? _service;

    public CloudProviderKind Kind => CloudProviderKind.GoogleDrive;

    async Task<DriveService> ServiceAsync(CancellationToken ct)
    {
        if (_service is not null) return _service;
        await _gate.WaitAsync(ct);
        try
        {
            if (_service is not null) return _service;
            if (!File.Exists(options.ClientSecretsPath))
                throw new FileNotFoundException("Google OAuth client file not found. See README for setup.", options.ClientSecretsPath);

            await using var stream = File.OpenRead(options.ClientSecretsPath);
            var secrets = (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets;
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets, [DriveService.Scope.Drive], "user", ct,
                new FileDataStore(Path.Combine(options.CacheDirectory, "google-token"), fullPath: true));

            return _service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "CloudDupes",
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SignInAsync(CancellationToken ct)
    {
        var service = await ServiceAsync(ct);
        var request = service.About.Get();
        request.Fields = "user(emailAddress)";
        var about = await ExecuteAsync(request, ct);
        return about.User?.EmailAddress ?? "(unknown)";
    }

    public async IAsyncEnumerable<ScanBatch> ScanAsync(string? deltaToken, [EnumeratorCancellation] CancellationToken ct)
    {
        var service = await ServiceAsync(ct);

        if (deltaToken is not null)
        {
            string? pageToken = deltaToken;
            while (pageToken is not null)
            {
                var request = service.Changes.List(pageToken);
                request.Fields = $"nextPageToken,newStartPageToken,changes(fileId,removed,file({FileFields}))";
                request.PageSize = 1000;
                request.Spaces = "drive";
                request.IncludeRemoved = true;
                var response = await ExecuteAsync(request, ct);

                var files = new List<CloudFile>();
                var folders = new List<FolderEntry>();
                var deleted = new List<string>();
                foreach (var change in response.Changes ?? [])
                {
                    if (change.Removed == true || change.File is null || change.File.Trashed == true || change.File.OwnedByMe != true)
                        deleted.Add(change.FileId);
                    else
                        Add(change.File, files, folders);
                }

                yield return new ScanBatch(files, folders, deleted) { NextToken = response.NewStartPageToken };
                pageToken = response.NextPageToken;
            }
            yield break;
        }

        // Take the change token *before* listing so nothing that changes during the scan is missed.
        var startToken = (await ExecuteAsync(service.Changes.GetStartPageToken(), ct)).StartPageTokenValue;
        string? next = null;
        var first = true;
        do
        {
            var request = service.Files.List();
            request.Q = "trashed = false and 'me' in owners";
            request.Fields = $"nextPageToken,files({FileFields})";
            request.PageSize = 1000;
            request.Spaces = "drive";
            request.PageToken = next;
            var response = await ExecuteAsync(request, ct);
            next = response.NextPageToken;

            var files = new List<CloudFile>();
            var folders = new List<FolderEntry>();
            foreach (var f in response.Files ?? []) Add(f, files, folders);

            yield return new ScanBatch(files, folders, []) { Reset = first, NextToken = next is null ? startToken : null };
            first = false;
        } while (next is not null);
    }

    static void Add(DriveFile f, List<CloudFile> files, List<FolderEntry> folders)
    {
        var parentId = f.Parents?.FirstOrDefault();
        if (f.MimeType == FolderMime)
        {
            folders.Add(new FolderEntry(f.Id, parentId, f.Name ?? ""));
            return;
        }
        // Google Docs/Sheets/Slides and shortcuts have no size or checksum.
        if (f.MimeType?.StartsWith(GoogleNativePrefix, StringComparison.Ordinal) == true) return;

        files.Add(new CloudFile
        {
            Provider = CloudProviderKind.GoogleDrive,
            Id = f.Id,
            Name = f.Name ?? "",
            ParentId = parentId,
            Size = f.Size ?? 0,
            Modified = f.ModifiedTimeDateTimeOffset ?? default,
            Version = f.Version?.ToString(),
            MimeType = f.MimeType,
            WebUrl = f.WebViewLink,
            Md5 = f.Md5Checksum?.ToLowerInvariant(),
            Sha1 = f.Sha1Checksum?.ToLowerInvariant(),
            Sha256 = f.Sha256Checksum?.ToLowerInvariant(),
        });
    }

    public async Task<Stream> OpenContentAsync(string id, CancellationToken ct)
    {
        var service = await ServiceAsync(ct);
        return await Retry.ExecuteAsync(
            token => service.HttpClient.GetStreamAsync($"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media", token),
            RetryDelay, ct);
    }

    public async Task<Stream?> OpenThumbnailAsync(string id, CancellationToken ct)
    {
        // thumbnailLink is short-lived, so always ask for a fresh one.
        var service = await ServiceAsync(ct);
        var request = service.Files.Get(id);
        request.Fields = "thumbnailLink";
        var file = await ExecuteAsync(request, ct);
        if (string.IsNullOrEmpty(file.ThumbnailLink)) return null;

        var response = await service.HttpClient.GetAsync(file.ThumbnailLink, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<string?> GetCurrentVersionAsync(string id, CancellationToken ct)
    {
        var service = await ServiceAsync(ct);
        var request = service.Files.Get(id);
        request.Fields = "id,trashed,version";
        try
        {
            var file = await ExecuteAsync(request, ct);
            return file.Trashed == true ? null : file.Version?.ToString();
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Moves the file to the Drive trash (restorable for 30 days).</summary>
    public async Task TrashAsync(string id, string? expectedVersion, CancellationToken ct)
    {
        var service = await ServiceAsync(ct);
        var request = service.Files.Update(new DriveFile { Trashed = true }, id);
        request.Fields = "id";
        await ExecuteAsync(request, ct);
    }

    static Task<T> ExecuteAsync<T>(ClientServiceRequest<T> request, CancellationToken ct) =>
        Retry.ExecuteAsync(request.ExecuteAsync, RetryDelay, ct);

    static TimeSpan? RetryDelay(Exception ex) => ex switch
    {
        GoogleApiException { HttpStatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => TimeSpan.Zero,
        GoogleApiException { HttpStatusCode: HttpStatusCode.Forbidden } g
            when g.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") == true => TimeSpan.Zero,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or null } => TimeSpan.Zero,
        _ => null,
    };

    public void Dispose() => _service?.Dispose();
}
