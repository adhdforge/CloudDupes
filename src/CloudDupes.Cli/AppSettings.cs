using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;
using CloudDupes.Providers.GoogleDrive;
using CloudDupes.Providers.OneDrive;

namespace CloudDupes.Cli;

sealed class AppSettings
{
    /// <summary>Where the database and token caches live. Defaults to %LOCALAPPDATA%\CloudDupes.</summary>
    public string DataDirectory { get; set; } = "";

    public OneDriveSettings OneDrive { get; set; } = new();
    public GoogleDriveSettings GoogleDrive { get; set; } = new();
    public MatchingSettings Matching { get; set; } = new();
    public KeepSettings Keep { get; set; } = new();

    public string ResolveDataDirectory()
    {
        var dir = string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudDupes")
            : Environment.ExpandEnvironmentVariables(DataDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

sealed class OneDriveSettings
{
    public string ClientId { get; set; } = "";
    public string Tenant { get; set; } = "common";
}

sealed class GoogleDriveSettings
{
    public string ClientSecretsPath { get; set; } = "client_secret.json";
}

// Collections have no initializers: the configuration binder appends to existing items instead of replacing them.
sealed class MatchingSettings
{
    public string[]? Modes { get; set; }
    public string[]? AutoSelect { get; set; }
    public int SimilarThreshold { get; set; } = 8;
    public bool CrossCloudDownload { get; set; } = true;
    public long CrossCloudMaxBytes { get; set; } = 500L * 1024 * 1024;
    public int MaxParallelDownloads { get; set; } = 4;
    public int MaxParallelThumbnails { get; set; } = 8;
}

sealed class KeepSettings
{
    public string Strategy { get; set; } = "oldest";
    public string SimilarStrategy { get; set; } = "largest";
    public string[]? PathPriority { get; set; }
}

/// <summary>Creates providers lazily, only for clouds that are configured.</summary>
sealed class ProviderRegistry(AppSettings settings, string dataDirectory) : IDisposable
{
    readonly Dictionary<CloudProviderKind, ICloudProvider> _created = [];

    public bool IsConfigured(CloudProviderKind kind) => kind switch
    {
        CloudProviderKind.OneDrive => !string.IsNullOrWhiteSpace(settings.OneDrive.ClientId),
        CloudProviderKind.GoogleDrive => File.Exists(GoogleSecretsPath()),
        _ => false,
    };

    public ICloudProvider Get(CloudProviderKind kind)
    {
        if (_created.TryGetValue(kind, out var existing)) return existing;
        if (!IsConfigured(kind)) throw new InvalidOperationException(SetupHint(kind));

        ICloudProvider provider = kind switch
        {
            CloudProviderKind.OneDrive => new OneDriveProvider(new OneDriveOptions
            {
                ClientId = settings.OneDrive.ClientId,
                Tenant = settings.OneDrive.Tenant,
                CacheDirectory = dataDirectory,
            }),
            _ => new GoogleDriveProvider(new GoogleDriveOptions
            {
                ClientSecretsPath = GoogleSecretsPath(),
                CacheDirectory = dataDirectory,
            }),
        };
        return _created[kind] = provider;
    }

    /// <summary>All configured providers (created on demand).</summary>
    public IReadOnlyDictionary<CloudProviderKind, ICloudProvider> Configured() =>
        Enum.GetValues<CloudProviderKind>().Where(IsConfigured).ToDictionary(k => k, Get);

    public static string SetupHint(CloudProviderKind kind) => kind == CloudProviderKind.OneDrive
        ? "OneDrive is not configured: set OneDrive:ClientId in appsettings.json (see README)."
        : "Google Drive is not configured: put your OAuth client JSON at GoogleDrive:ClientSecretsPath (see README).";

    /// <summary>A relative path is looked up in the working directory, next to the exe, then in the data directory.</summary>
    string GoogleSecretsPath()
    {
        var path = settings.GoogleDrive.ClientSecretsPath;
        if (Path.IsPathRooted(path)) return path;
        return new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, dataDirectory }
            .Select(dir => Path.Combine(dir, path))
            .FirstOrDefault(File.Exists) ?? Path.Combine(dataDirectory, path);
    }

    public void Dispose()
    {
        foreach (var provider in _created.Values) (provider as IDisposable)?.Dispose();
    }
}
