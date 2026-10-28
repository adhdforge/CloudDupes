using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDupes.Core.Models;
using CloudDupes.Core.Selection;

namespace CloudDupes.Core.Planning;

public enum FileAction
{
    Keep,
    Delete,
}

/// <summary>The editable keep/delete plan written by "find" and executed by "apply".</summary>
public sealed class DuplicatePlan
{
    public DateTimeOffset CreatedUtc { get; set; }
    public List<PlanGroup> Groups { get; set; } = [];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static DuplicatePlan Load(string path) =>
        JsonSerializer.Deserialize<DuplicatePlan>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is not a valid plan.");
}

public sealed class PlanGroup
{
    public int Number { get; set; }
    public MatchKind Kind { get; set; }
    public string Key { get; set; } = "";
    public bool NeedsReview { get; set; }
    public List<PlanFile> Files { get; set; } = [];
}

public sealed class PlanFile
{
    public CloudProviderKind Provider { get; set; }
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset Modified { get; set; }
    public string? Version { get; set; }
    public string? WebUrl { get; set; }
    public bool IsImage { get; set; }
    public FileAction Action { get; set; }

    [JsonIgnore]
    public string Key => $"{Provider}:{Id}";
}

public static class PlanBuilder
{
    /// <summary>
    /// Builds the plan. Only groups whose kind is in <paramref name="autoSelect"/> get proposed deletions;
    /// the rest are all "keep" for the user to edit. A file deleted by an earlier group is never chosen
    /// as the keeper of a later one.
    /// </summary>
    public static DuplicatePlan Build(IReadOnlyList<DuplicateGroup> groups, KeepRules rules, IReadOnlySet<MatchKind> autoSelect)
    {
        var plan = new DuplicatePlan { CreatedUtc = DateTimeOffset.UtcNow };
        var deleted = new HashSet<string>();

        foreach (var group in groups)
        {
            var alive = group.Files.Where(f => !deleted.Contains(f.Key)).ToList();
            var keeper = autoSelect.Contains(group.Kind) && alive.Count > 1 ? KeepSelector.Choose(alive, group.Kind, rules) : null;

            var planGroup = new PlanGroup
            {
                Number = plan.Groups.Count + 1,
                Kind = group.Kind,
                Key = group.Key,
                NeedsReview = group.NeedsReview,
            };
            foreach (var f in group.Files)
            {
                var delete = deleted.Contains(f.Key) || (keeper is not null && f.Key != keeper.Key);
                if (delete) deleted.Add(f.Key);
                planGroup.Files.Add(new PlanFile
                {
                    Provider = f.Provider,
                    Id = f.Id,
                    Path = f.Path,
                    Size = f.Size,
                    Modified = f.Modified,
                    Version = f.Version,
                    WebUrl = f.WebUrl,
                    IsImage = f.IsImage,
                    Action = delete ? FileAction.Delete : FileAction.Keep,
                });
            }
            plan.Groups.Add(planGroup);
        }
        return plan;
    }
}

public static class PlanValidator
{
    /// <summary>Returns problems that make the plan unsafe to apply (empty when fine).</summary>
    public static List<string> Validate(DuplicatePlan plan)
    {
        var errors = new List<string>();
        var deleted = plan.Groups.SelectMany(g => g.Files).Where(f => f.Action == FileAction.Delete).Select(f => f.Key).ToHashSet();

        foreach (var group in plan.Groups)
        {
            if (group.Files.Where(f => f.Action == FileAction.Delete).Any() && group.Files.All(f => deleted.Contains(f.Key)))
                errors.Add($"Group {group.Number}: every copy would be deleted; mark at least one file \"Keep\".");

            foreach (var f in group.Files.Where(f => f.Action == FileAction.Keep && deleted.Contains(f.Key)))
                errors.Add($"Group {group.Number}: {f.Path} is \"Keep\" here but \"Delete\" in another group.");
        }
        return errors;
    }
}
