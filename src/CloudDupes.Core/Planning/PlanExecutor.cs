using System.Text.Json;
using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;
using CloudDupes.Core.Storage;

namespace CloudDupes.Core.Planning;

public sealed record ApplyResult(int Trashed, int Skipped, int Failed, long BytesTrashed);

/// <summary>Moves every "Delete" file of a validated plan to its cloud's recycle bin / trash.</summary>
public sealed class PlanExecutor(
    IReadOnlyDictionary<CloudProviderKind, ICloudProvider> providers,
    InventoryStore? store,
    IProgress<string> log)
{
    public static IReadOnlyList<PlanFile> Targets(DuplicatePlan plan) =>
        plan.Groups.SelectMany(g => g.Files).Where(f => f.Action == FileAction.Delete).DistinctBy(f => f.Key).ToList();

    public async Task<ApplyResult> ExecuteAsync(DuplicatePlan plan, bool dryRun, string undoLogPath, CancellationToken ct)
    {
        var errors = PlanValidator.Validate(plan);
        if (errors.Count > 0) throw new InvalidOperationException("Plan is not safe to apply:\n  " + string.Join("\n  ", errors));

        int trashed = 0, skipped = 0, failed = 0;
        long bytes = 0;
        StreamWriter? undo = null;
        try
        {
            foreach (var file in Targets(plan))
            {
                ct.ThrowIfCancellationRequested();
                if (!providers.TryGetValue(file.Provider, out var provider))
                {
                    log.Report($"skip    {file.Provider} not configured: {file.Path}");
                    skipped++;
                    continue;
                }
                if (dryRun)
                {
                    log.Report($"would trash  [{file.Provider}] {file.Path}");
                    trashed++;
                    bytes += file.Size;
                    continue;
                }

                try
                {
                    var current = await provider.GetCurrentVersionAsync(file.Id, ct);
                    if (current is null)
                    {
                        log.Report($"skip    already gone: [{file.Provider}] {file.Path}");
                        store?.RemoveFile(file.Provider, file.Id);
                        skipped++;
                        continue;
                    }
                    if (file.Version is not null && current != file.Version)
                    {
                        log.Report($"skip    changed since scan: [{file.Provider}] {file.Path}");
                        skipped++;
                        continue;
                    }

                    await provider.TrashAsync(file.Id, file.Version, ct);
                    store?.RemoveFile(file.Provider, file.Id);

                    undo ??= new StreamWriter(undoLogPath, append: true);
                    await undo.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        trashedUtc = DateTimeOffset.UtcNow,
                        provider = file.Provider.ToString(),
                        id = file.Id,
                        path = file.Path,
                        size = file.Size,
                        webUrl = file.WebUrl,
                    }));
                    await undo.FlushAsync(ct);

                    log.Report($"trashed [{file.Provider}] {file.Path}");
                    trashed++;
                    bytes += file.Size;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Report($"FAILED  [{file.Provider}] {file.Path}: {ex.Message}");
                    failed++;
                }
            }
        }
        finally
        {
            if (undo is not null) await undo.DisposeAsync();
        }
        return new ApplyResult(trashed, skipped, failed, bytes);
    }
}
