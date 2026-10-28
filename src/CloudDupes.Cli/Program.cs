using System.CommandLine;
using CloudDupes.Cli;
using CloudDupes.Core.Hashing;
using CloudDupes.Core.Matching;
using CloudDupes.Core.Models;
using CloudDupes.Core.Planning;
using CloudDupes.Core.Reporting;
using CloudDupes.Core.Selection;
using CloudDupes.Core.Storage;
using CloudDupes.Core.Util;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true)
    .AddUserSecrets<AppSettings>(optional: true)
    .AddEnvironmentVariables("CLOUDDUPES_")
    .Build();
var settings = configuration.Get<AppSettings>() ?? new AppSettings();
var dataDirectory = settings.ResolveDataDirectory();
var log = new ConsoleProgress();

using var providers = new ProviderRegistry(settings, dataDirectory);
var store = new Lazy<InventoryStore>(() => new InventoryStore(Path.Combine(dataDirectory, "inventory.db")));

// ---- shared options --------------------------------------------------------------------------

var providerOption = new Option<string>("--provider", "-p")
{
    Description = "Which cloud(s): all, onedrive or gdrive.",
    DefaultValueFactory = _ => "all",
};
providerOption.AcceptOnlyFromAmong("all", "onedrive", "gdrive");

// ---- login -----------------------------------------------------------------------------------

var loginProvider = new Argument<string>("provider") { Description = "onedrive or gdrive" };
loginProvider.AcceptOnlyFromAmong("onedrive", "gdrive");
var login = new Command("login", "Sign in to a cloud and cache the token.") { loginProvider };
login.SetAction(async (parse, ct) =>
{
    var kind = ParseProviders(parse.GetValue(loginProvider)!).Single();
    var account = await providers.Get(kind).SignInAsync(ct);
    log.Report($"Signed in to {kind} as {account}.");
    return 0;
});

// ---- scan ------------------------------------------------------------------------------------

var fullOption = new Option<bool>("--full") { Description = "Ignore the saved delta token and re-read everything." };
var scan = new Command("scan", "Read the file inventory into the local cache (incremental after the first run).") { providerOption, fullOption };
scan.SetAction(async (parse, ct) =>
{
    foreach (var kind in ConfiguredOrFail(ParseProviders(parse.GetValue(providerOption)!)))
    {
        var token = parse.GetValue(fullOption) ? null : store.Value.GetDeltaToken(kind);
        log.Report(token is null ? $"{kind}: full scan..." : $"{kind}: incremental scan...");
        int files = 0, folders = 0, deleted = 0;
        await foreach (var batch in providers.Get(kind).ScanAsync(token, ct))
        {
            store.Value.ApplyBatch(kind, batch);
            files += batch.Files.Count;
            folders += batch.Folders.Count;
            deleted += batch.DeletedIds.Count;
            log.Report($"  {files:N0} files, {folders:N0} folders, {deleted:N0} removed");
        }
    }
    PrintStatus();
    return 0;
});

// ---- status ----------------------------------------------------------------------------------

var status = new Command("status", "Show what is in the local cache.");
status.SetAction(_ =>
{
    PrintStatus();
    return 0;
});

// ---- find ------------------------------------------------------------------------------------

var modeOption = new Option<string?>("--mode", "-m")
{
    Description = "Comma-separated match modes: exact, similar, namesize. Default from Matching:Modes.",
};
var pathOption = new Option<string[]>("--path")
{
    Description = "Only consider files under these path prefixes (e.g. /Photos). Repeatable.",
    AllowMultipleArgumentsPerToken = true,
};
var keepOption = new Option<string?>("--keep")
{
    Description = $"Which copy to keep in exact/name+size groups: {string.Join(", ", KeepSelector.Strategies)}.",
};
var keepSimilarOption = new Option<string?>("--keep-similar") { Description = "Which copy to keep in similar-photo groups (default: largest)." };
var autoSelectOption = new Option<string?>("--auto-select")
{
    Description = "Modes whose groups get proposed deletions in the plan (others are all 'Keep'). Default from Matching:AutoSelect.",
};
var thresholdOption = new Option<int?>("--threshold") { Description = "Max perceptual-hash distance (0-64) for similar photos." };
var noCrossOption = new Option<bool>("--no-cross-download") { Description = "Don't download files to compare across clouds." };
var noThumbsOption = new Option<bool>("--no-thumbnails") { Description = "Don't embed thumbnails in the HTML report." };
var outOption = new Option<DirectoryInfo>("--out", "-o")
{
    Description = "Output directory for report.html, report.csv and plan.json.",
    DefaultValueFactory = _ => new DirectoryInfo("clouddupes-report"),
};

var find = new Command("find", "Find duplicates in the cached inventory and write a report plus an editable plan.")
{
    modeOption, providerOption, pathOption, keepOption, keepSimilarOption, autoSelectOption,
    thresholdOption, noCrossOption, noThumbsOption, outOption,
};
find.SetAction(async (parse, ct) =>
{
    var modes = ParseModes(parse.GetValue(modeOption), settings.Matching.Modes, [MatchKind.Exact]);
    var autoSelect = ParseModes(parse.GetValue(autoSelectOption), settings.Matching.AutoSelect, [MatchKind.Exact]).ToHashSet();
    var rules = new KeepRules(
        parse.GetValue(keepOption) ?? settings.Keep.Strategy,
        parse.GetValue(keepSimilarOption) ?? settings.Keep.SimilarStrategy,
        settings.Keep.PathPriority);
    foreach (var strategy in new[] { rules.Strategy, rules.SimilarStrategy })
        if (!KeepSelector.IsValid(strategy))
            throw new ArgumentException($"Unknown keep strategy '{strategy}'. Use one of: {string.Join(", ", KeepSelector.Strategies)}.");

    var kinds = ParseProviders(parse.GetValue(providerOption)!);
    var prefixes = parse.GetValue(pathOption) ?? [];
    var files = store.Value.LoadFiles(kinds)
        .Where(f => prefixes.Length == 0 || prefixes.Any(p => f.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (files.Count == 0)
    {
        log.Report("No files in the cache for that selection. Run 'scan' first.");
        return 1;
    }
    log.Report($"Comparing {files.Count:N0} files using: {string.Join(", ", modes)}");

    var clouds = providers.Configured();
    var thumbnails = new ThumbnailService(clouds, store.Value);
    var crossCloud = settings.Matching.CrossCloudDownload && !parse.GetValue(noCrossOption)
        ? new CrossCloudHashResolver(clouds, store.Value, settings.Matching.CrossCloudMaxBytes)
        : null;

    var matchers = modes.Select<MatchKind, IDuplicateMatcher>(mode => mode switch
    {
        MatchKind.Exact => new ExactMatcher(crossCloud, settings.Matching.MaxParallelDownloads, log),
        MatchKind.Similar => new SimilarImageMatcher(thumbnails, parse.GetValue(thresholdOption) ?? settings.Matching.SimilarThreshold,
            settings.Matching.MaxParallelThumbnails, log),
        _ => new NameSizeMatcher(),
    });
    var groups = await DuplicateFinder.FindAsync(matchers, files, ct);
    var plan = PlanBuilder.Build(groups, rules, autoSelect);

    var outDir = parse.GetValue(outOption)!;
    outDir.Create();
    if (!parse.GetValue(noThumbsOption))
    {
        var images = plan.Groups.SelectMany(g => g.Files).Where(f => f.IsImage).DistinctBy(f => f.Key).ToList();
        if (images.Count > 0) log.Report($"Fetching {images.Count:N0} thumbnail(s) for the report...");
        var byKey = files.ToDictionary(f => f.Key);
        await Parallel.ForEachAsync(images, new ParallelOptions { MaxDegreeOfParallelism = settings.Matching.MaxParallelThumbnails, CancellationToken = ct },
            async (f, token) => await thumbnails.GetAsync(byKey[f.Key], token));
    }

    var lookup = files.ToDictionary(f => f.Key);
    HtmlReport.Write(plan, Path.Combine(outDir.FullName, "report.html"),
        f => parse.GetValue(noThumbsOption) || !lookup.TryGetValue(f.Key, out var file) ? null : store.Value.GetThumbnail(file)?.Jpeg);
    CsvReport.Write(plan, Path.Combine(outDir.FullName, "report.csv"));
    var planPath = Path.Combine(outDir.FullName, "plan.json");
    plan.Save(planPath);

    var targets = PlanExecutor.Targets(plan);
    log.Report("");
    foreach (var kind in modes)
        log.Report($"  {kind,-9} {plan.Groups.Count(g => g.Kind == kind):N0} group(s)");
    log.Report($"  Proposed for trash: {targets.Count:N0} file(s), {HtmlReport.FormatSize(targets.Sum(f => f.Size))}");
    if (crossCloud?.Failures > 0) log.Report($"  Warning: {crossCloud.Failures} download(s) failed while hashing; re-run to retry.");
    if (thumbnails.Failures > 0) log.Report($"  Warning: {thumbnails.Failures} thumbnail(s) failed to download; re-run to retry.");
    log.Report($"Report: {Path.Combine(outDir.FullName, "report.html")}");
    log.Report($"Plan:   {planPath}  (edit 'action' to Keep/Delete, then: clouddupes apply \"{planPath}\")");
    return 0;
});

// ---- apply -----------------------------------------------------------------------------------

var planArgument = new Argument<FileInfo>("plan") { Description = "plan.json written by 'find'." };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Only list what would be trashed." };
var yesOption = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation." };
var apply = new Command("apply", "Move the files marked 'Delete' in a plan to the recycle bin / trash.") { planArgument, dryRunOption, yesOption };
apply.SetAction(async (parse, ct) =>
{
    var planFile = parse.GetValue(planArgument)!;
    var plan = DuplicatePlan.Load(planFile.FullName);
    var errors = PlanValidator.Validate(plan);
    if (errors.Count > 0)
    {
        log.Report("Plan is not safe to apply:");
        foreach (var e in errors) log.Report($"  {e}");
        return 2;
    }

    var targets = PlanExecutor.Targets(plan);
    if (targets.Count == 0)
    {
        log.Report("Nothing is marked 'Delete' in this plan.");
        return 0;
    }

    var needed = targets.Select(f => f.Provider).Distinct().ToList();
    var clouds = ConfiguredOrFail(needed).ToDictionary(k => k, providers.Get);
    var executor = new PlanExecutor(clouds, store.Value, log);
    var undoLog = Path.Combine(planFile.DirectoryName ?? ".", $"trashed-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

    var dryRun = parse.GetValue(dryRunOption);
    if (dryRun || !parse.GetValue(yesOption))
    {
        var preview = await executor.ExecuteAsync(plan, dryRun: true, undoLog, ct);
        log.Report($"{preview.Trashed:N0} file(s), {HtmlReport.FormatSize(preview.BytesTrashed)} would be moved to the recycle bin / trash.");
        if (dryRun) return 0;
        if (Console.IsInputRedirected)
        {
            log.Report("Re-run with --yes to proceed.");
            return 0;
        }
        Console.Write("Proceed? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 0;
    }

    var result = await executor.ExecuteAsync(plan, dryRun: false, undoLog, ct);
    log.Report($"Trashed {result.Trashed:N0} ({HtmlReport.FormatSize(result.BytesTrashed)}), skipped {result.Skipped:N0}, failed {result.Failed:N0}.");
    if (result.Trashed > 0) log.Report($"Log of trashed items: {undoLog}");
    return result.Failed > 0 ? 1 : 0;
});

// ---- run -------------------------------------------------------------------------------------

var root = new RootCommand("CloudDupes: find duplicate files and photos across OneDrive and Google Drive.")
{
    login, scan, status, find, apply,
};
try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException or InvalidDataException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// ---- helpers ---------------------------------------------------------------------------------

static CloudProviderKind[] ParseProviders(string value) => value.ToLowerInvariant() switch
{
    "onedrive" => [CloudProviderKind.OneDrive],
    "gdrive" => [CloudProviderKind.GoogleDrive],
    _ => Enum.GetValues<CloudProviderKind>(),
};

static List<MatchKind> ParseModes(string? cli, string[]? configured, MatchKind[] fallback)
{
    var raw = cli is not null ? cli.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : configured;
    if (raw is null || raw.Length == 0) return [.. fallback];
    return raw.Select(m => m.ToLowerInvariant() switch
    {
        "exact" => MatchKind.Exact,
        "similar" => MatchKind.Similar,
        "namesize" or "name-size" or "name" => MatchKind.NameSize,
        _ => throw new ArgumentException($"Unknown mode '{m}'. Use exact, similar or namesize."),
    }).Distinct().Order().ToList();
}

IEnumerable<CloudProviderKind> ConfiguredOrFail(IEnumerable<CloudProviderKind> kinds)
{
    var list = kinds.ToList();
    var configured = list.Where(providers.IsConfigured).ToList();
    foreach (var missing in list.Except(configured)) log.Report(ProviderRegistry.SetupHint(missing));
    if (configured.Count == 0) throw new InvalidOperationException("No cloud is configured yet.");
    return configured;
}

void PrintStatus()
{
    var stats = store.Value.GetStats();
    if (stats.Count == 0)
    {
        log.Report("Cache is empty. Run 'clouddupes scan'.");
        return;
    }
    foreach (var s in stats)
        log.Report($"{s.Provider,-12} {s.Files,10:N0} files {HtmlReport.FormatSize(s.Bytes),10} {s.Folders,8:N0} folders");
    log.Report($"Cache: {Path.Combine(dataDirectory, "inventory.db")}");
}
