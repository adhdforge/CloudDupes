using CloudDupes.Core.Models;
using CloudDupes.Core.Planning;
using CloudDupes.Core.Selection;
using CloudDupes.Core.Util;
using static CloudDupes.Core.Tests.TestData;

namespace CloudDupes.Core.Tests;

public class KeepSelectorTests
{
    static readonly DateTimeOffset Old = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("oldest", "old")]
    [InlineData("newest", "new")]
    [InlineData("shortest-path", "new")]
    [InlineData("largest", "old")]
    [InlineData("prefer:gdrive", "new")]
    public void Picks_by_strategy(string strategy, string expected)
    {
        var old = File("a.jpg", size: 200, path: "/Photos/2020/Backup/a.jpg", modified: Old, id: "old");
        var @new = File("a.jpg", size: 100, path: "/a.jpg", modified: New, provider: CloudProviderKind.GoogleDrive, id: "new");

        Assert.Equal(expected, KeepSelector.Choose([old, @new], MatchKind.Exact, new KeepRules(strategy)).Id);
    }

    [Fact]
    public void Path_priority_wins_over_strategy()
    {
        var inCamera = File("a.jpg", path: "/Camera Roll/a.jpg", modified: New, id: "camera");
        var inArchive = File("a.jpg", path: "/Archive/a.jpg", modified: Old, id: "archive");

        var chosen = KeepSelector.Choose([inArchive, inCamera], MatchKind.Exact, new KeepRules("oldest", PathPriority: ["/camera roll"]));

        Assert.Equal("camera", chosen.Id);
    }

    [Fact]
    public void Similar_groups_use_similar_strategy()
    {
        var small = File("a.jpg", size: 10, id: "small", modified: Old);
        var big = File("a.jpg", size: 1000, id: "big", modified: New);
        Assert.Equal("big", KeepSelector.Choose([small, big], MatchKind.Similar, new KeepRules("oldest", "largest")).Id);
    }

    [Fact]
    public void Unknown_strategy_throws() =>
        Assert.Throws<ArgumentException>(() => KeepSelector.Choose([File("a")], MatchKind.Exact, new KeepRules("random")));
}

public class PlanTests
{
    static readonly HashSet<MatchKind> AutoAll = [MatchKind.Exact, MatchKind.Similar, MatchKind.NameSize];

    [Fact]
    public void Builder_keeps_one_per_group_and_only_auto_selects_configured_kinds()
    {
        var a = File("a", modified: DateTimeOffset.MinValue);
        var b = File("a");
        var c = File("c");
        var d = File("c");
        DuplicateGroup[] groups = [new(MatchKind.Exact, "1", [a, b]), new(MatchKind.NameSize, "2", [c, d])];

        var plan = PlanBuilder.Build(groups, new KeepRules(), new HashSet<MatchKind> { MatchKind.Exact });

        Assert.Equal([FileAction.Keep, FileAction.Delete], plan.Groups[0].Files.Select(f => f.Action));
        Assert.All(plan.Groups[1].Files, f => Assert.Equal(FileAction.Keep, f.Action));
        Assert.Empty(PlanValidator.Validate(plan));
    }

    [Fact]
    public void Overlapping_groups_never_lose_every_copy()
    {
        // Exact {a, a2} keeps a2 (shorter path); similar {a, a2, b} must not then keep a (already deleted).
        var a = File("a.jpg", size: 300, path: "/long/path/a.jpg");
        var a2 = File("a.jpg", size: 300, path: "/a.jpg");
        var b = File("b.jpg", size: 100, path: "/b.jpg");
        DuplicateGroup[] groups = [new(MatchKind.Exact, "e", [a, a2]), new(MatchKind.Similar, "s", [a, a2, b])];

        var plan = PlanBuilder.Build(groups, new KeepRules("shortest-path", "largest"), AutoAll);

        Assert.Empty(PlanValidator.Validate(plan));
        var deleted = PlanExecutor.Targets(plan).Select(f => f.Key).ToHashSet();
        Assert.Equal([a.Key, b.Key], deleted.Order());
        Assert.DoesNotContain(a2.Key, deleted);
    }

    [Fact]
    public void Validator_rejects_group_with_no_survivor()
    {
        var plan = PlanBuilder.Build([new(MatchKind.Exact, "1", [File("a"), File("a")])], new KeepRules(), AutoAll);
        plan.Groups[0].Files.ForEach(f => f.Action = FileAction.Delete);

        Assert.Contains(PlanValidator.Validate(plan), e => e.Contains("every copy"));
    }

    [Fact]
    public void Validator_rejects_conflicting_actions()
    {
        var a = File("a");
        var b = File("a");
        var c = File("c");
        var plan = PlanBuilder.Build([new(MatchKind.Exact, "1", [a, b]), new(MatchKind.NameSize, "2", [a, c])], new KeepRules(), new HashSet<MatchKind>());
        plan.Groups[0].Files[0].Action = FileAction.Delete; // a deleted in group 1, still "Keep" in group 2

        Assert.Contains(PlanValidator.Validate(plan), e => e.Contains("another group"));
    }

    [Fact]
    public void Plan_round_trips_through_json()
    {
        var path = Path.GetTempFileName();
        try
        {
            var plan = PlanBuilder.Build([new(MatchKind.Exact, "1", [File("a"), File("a")])], new KeepRules(), AutoAll);
            plan.Save(path);
            var loaded = DuplicatePlan.Load(path);
            Assert.Equal(plan.Groups[0].Files.Select(f => (f.Key, f.Action)), loaded.Groups[0].Files.Select(f => (f.Key, f.Action)));
            Assert.Contains("\"Delete\"", System.IO.File.ReadAllText(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}

public class PlanExecutorTests
{
    static (DuplicatePlan Plan, FakeProvider Provider, CloudFile Keep, CloudFile Delete) Setup()
    {
        var keep = File("a", id: "keep", modified: DateTimeOffset.MinValue);
        var delete = File("a", id: "del");
        var provider = new FakeProvider(CloudProviderKind.OneDrive);
        provider.Versions["keep"] = "v1";
        provider.Versions["del"] = "v1";
        var plan = PlanBuilder.Build([new(MatchKind.Exact, "1", [keep, delete])], new KeepRules(), new HashSet<MatchKind> { MatchKind.Exact });
        return (plan, provider, keep, delete);
    }

    static PlanExecutor Executor(FakeProvider provider) =>
        new(new Dictionary<CloudProviderKind, Abstractions.ICloudProvider> { [provider.Kind] = provider }, null, new ConsoleProgress());

    [Fact]
    public async Task Dry_run_trashes_nothing()
    {
        var (plan, provider, _, _) = Setup();
        var result = await Executor(provider).ExecuteAsync(plan, dryRun: true, Path.GetTempFileName(), CancellationToken.None);
        Assert.Equal(1, result.Trashed);
        Assert.Empty(provider.Trashed);
    }

    [Fact]
    public async Task Trashes_only_files_marked_delete_and_writes_log()
    {
        var (plan, provider, _, _) = Setup();
        var log = Path.Combine(Path.GetTempPath(), $"undo-{Guid.NewGuid():N}.jsonl");
        try
        {
            var result = await Executor(provider).ExecuteAsync(plan, dryRun: false, log, CancellationToken.None);
            Assert.Equal(["del"], provider.Trashed);
            Assert.Equal(1, result.Trashed);
            Assert.Contains("\"id\":\"del\"", System.IO.File.ReadAllText(log));
        }
        finally
        {
            System.IO.File.Delete(log);
        }
    }

    [Fact]
    public async Task Skips_files_changed_since_scan()
    {
        var (plan, provider, _, _) = Setup();
        provider.Versions["del"] = "v2";
        var result = await Executor(provider).ExecuteAsync(plan, dryRun: false, Path.GetTempFileName(), CancellationToken.None);
        Assert.Empty(provider.Trashed);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Refuses_unsafe_plan()
    {
        var (plan, provider, _, _) = Setup();
        plan.Groups[0].Files.ForEach(f => f.Action = FileAction.Delete);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor(provider).ExecuteAsync(plan, dryRun: false, Path.GetTempFileName(), CancellationToken.None));
        Assert.Empty(provider.Trashed);
    }
}

public class ReportTests
{
    [Fact]
    public void Writes_html_and_csv_with_escaping()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var a = File("a,<b>.jpg", path: "/x/a,<b>.jpg") with { MimeType = "image/jpeg" };
            var b = File("a,<b>.jpg", path: "/y/a,<b>.jpg") with { MimeType = "image/jpeg" };
            var plan = PlanBuilder.Build([new(MatchKind.Exact, "1", [a, b])], new KeepRules(), new HashSet<MatchKind> { MatchKind.Exact });

            Reporting.HtmlReport.Write(plan, Path.Combine(dir, "r.html"), _ => [0xFF, 0xD8]);
            Reporting.CsvReport.Write(plan, Path.Combine(dir, "r.csv"));

            var html = System.IO.File.ReadAllText(Path.Combine(dir, "r.html"));
            Assert.Contains("a,&lt;b&gt;.jpg", html);
            Assert.Contains("data:image/jpeg;base64,", html);
            Assert.Contains("\"/x/a,<b>.jpg\"", System.IO.File.ReadAllText(Path.Combine(dir, "r.csv")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
