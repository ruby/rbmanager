using System.Runtime.InteropServices;

namespace RbManager.Tests;

// Plan 4.13: BinaryIndex.Parse and BinaryIndex.Pick — pure logic against
// hand-built pages and builds, no filesystem, no network.
[Trait("Category", "Unit")]
public class BinaryIndexTests
{
    private static readonly string[] X64Host = BinaryIndex.PlatformsFor(Architecture.X64);
    private static readonly string[] Arm64Host = BinaryIndex.PlatformsFor(Architecture.Arm64);

    private static Build Make(string name, string version, string channel = "release",
        int? revision = 0, string[]? tags = null, string platform = BinaryIndex.X64,
        string? commit = null, string? commitDate = null, string? publishedAt = null) => new()
    {
        Name = name,
        Version = version,
        Channel = channel,
        Revision = revision,
        Tags = tags ?? [],
        Platform = platform,
        Url = $"https://cache.ruby-lang.org/pub/ruby/binaries/mswin64/{name}.zip",
        Sha256 = new string('0', 64),
        Size = 1,
        Commit = commit,
        CommitDate = commitDate,
        PublishedAt = publishedAt,
        Signed = false,
    };

    [Fact] // case 99: the published feed's shape round-trips
    public void Parse_PublishedShape_PopulatesEveryKey()
    {
        IndexPage page = BinaryIndex.Parse("""
            {
              "schema": 1,
              "next": null,
              "builds": [
                {
                  "name": "ruby-4.1.0dev-20260821-e4462a9514-x64-mswin64_140",
                  "version": "4.1.0dev",
                  "channel": "dev",
                  "revision": null,
                  "tags": ["4.1-dev", "4.1-dev-20260821",
                           "4.1.0dev-20260821-e4462a9514", "ruby-dev"],
                  "platform": "x64-mswin64_140",
                  "url": "https://cache.ruby-lang.org/pub/ruby/binaries/mswin64/dev/ruby-4.1.0dev-20260821-e4462a9514-x64-mswin64_140.zip",
                  "sha256": "313063a97245ec48f125180346f589b43403ea8dc1afe98de0309f7e2baefd6b",
                  "size": 29021956,
                  "commit": "e4462a9514",
                  "commit_date": "2026-08-21",
                  "published_at": "2026-08-20",
                  "signed": false
                }
              ]
            }
            """);

        Assert.Equal(1, page.Schema);
        Assert.Null(page.Next);
        Build b = Assert.Single(page.Builds);
        Assert.Equal("ruby-4.1.0dev-20260821-e4462a9514-x64-mswin64_140", b.Name);
        Assert.Equal("4.1.0dev", b.Version);
        Assert.Equal("dev", b.Channel);
        Assert.Null(b.Revision);
        Assert.Contains("ruby-dev", b.Tags);
        Assert.Equal(BinaryIndex.X64, b.Platform);
        Assert.Equal("313063a97245ec48f125180346f589b43403ea8dc1afe98de0309f7e2baefd6b", b.Sha256);
        Assert.Equal(29021956, b.Size);
        Assert.Equal("2026-08-21", b.CommitDate);
        Assert.Equal("2026-08-20", b.PublishedAt);
        Assert.False(b.Signed);
    }

    [Fact] // case 100: an rb older than the feed names itself and the remedy
    public void Parse_UnsupportedSchema_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BinaryIndex.Parse("""{"schema": 2, "next": null, "builds": []}"""));
        Assert.Contains("schema 2", ex.Message);
        Assert.StartsWith(Program.SelfVersion(), ex.Message);
        Assert.Contains(Program.ReleasesUrl, ex.Message);
    }

    [Fact] // case 101: a series tag sits on every release of the series
    public void Pick_SeriesTag_PicksHighestVersion_RegardlessOfOrder()
    {
        Build[] builds =
        [
            Make("ruby-4.0.4-x64-mswin64_140", "4.0.4", tags: ["4.0.4", "4.0", "4"]),
            Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", tags: ["4.0.5", "4.0", "4"]),
        ];

        Assert.Equal("ruby-4.0.5-x64-mswin64_140", BinaryIndex.Pick(builds, "4.0", X64Host)!.Name);
        Assert.Equal("ruby-4.0.5-x64-mswin64_140",
            BinaryIndex.Pick(builds.Reverse(), "4", X64Host)!.Name);
    }

    [Fact] // case 102: a reissue supersedes by revision, not by feed order
    public void Pick_SameVersion_PicksHighestRevision()
    {
        Build[] builds =
        [
            Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", revision: 0, tags: ["4.0.5-0", "4.0.5"]),
            Make("ruby-4.0.5-1-x64-mswin64_140", "4.0.5", revision: 1,
                tags: ["4.0.5-1", "4.0.5", "4.0", "4"]),
        ];

        Assert.Equal("ruby-4.0.5-1-x64-mswin64_140",
            BinaryIndex.Pick(builds, "4.0.5", X64Host)!.Name);
        Assert.Equal("ruby-4.0.5-1-x64-mswin64_140",
            BinaryIndex.Pick(builds.Reverse(), "4.0.5", X64Host)!.Name);
    }

    [Fact] // case 103: a superseded revision stays reachable by its exact tag
    public void Pick_SupersededRevision_ByRevisionedTag()
    {
        Build[] builds =
        [
            Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", revision: 0, tags: ["4.0.5-0"]),
            Make("ruby-4.0.5-1-x64-mswin64_140", "4.0.5", revision: 1,
                tags: ["4.0.5-1", "4.0.5", "4.0", "4"]),
        ];

        Assert.Equal("ruby-4.0.5-x64-mswin64_140",
            BinaryIndex.Pick(builds, "4.0.5-0", X64Host)!.Name);
    }

    [Fact] // case 104: equal dev versions fall back to the commit date
    public void Pick_DevSeries_PicksNewestSnapshot()
    {
        Build[] builds =
        [
            Make("ruby-4.1.0dev-20260818-39d4744b68-x64-mswin64_140", "4.1.0dev",
                channel: "dev", revision: null, tags: ["4.1-dev", "ruby-dev"],
                commit: "39d4744b68", commitDate: "2026-08-18", publishedAt: "2026-08-18"),
            Make("ruby-4.1.0dev-20260821-e4462a9514-x64-mswin64_140", "4.1.0dev",
                channel: "dev", revision: null, tags: ["4.1-dev", "ruby-dev"],
                commit: "e4462a9514", commitDate: "2026-08-21", publishedAt: "2026-08-20"),
        ];

        Assert.Equal("ruby-4.1.0dev-20260821-e4462a9514-x64-mswin64_140",
            BinaryIndex.Pick(builds, "ruby-dev", X64Host)!.Name);
        Assert.Equal("ruby-4.1.0dev-20260821-e4462a9514-x64-mswin64_140",
            BinaryIndex.Pick(builds.Reverse(), "4.1-dev", X64Host)!.Name);
    }

    [Fact] // case 105: other platforms never resolve, even on a tag match
    public void Pick_ForeignPlatform_Filtered()
    {
        Build arm = Make("ruby-4.0.5-arm64-mswin64_140", "4.0.5",
            tags: ["4.0.5", "4.0", "4"], platform: "arm64-mswin64_140");
        Build x64 = Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", tags: ["4.0.5", "4.0", "4"]);

        Assert.Null(BinaryIndex.Pick([arm], "4.0.5", X64Host));
        Assert.Equal(x64.Name, BinaryIndex.Pick([arm, x64], "4.0.5", X64Host)!.Name);
    }

    [Fact] // case 106: the full package name resolves alongside the tags
    public void Pick_ExactName_Resolves()
    {
        Build b = Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", tags: ["4.0.5", "4.0", "4"]);

        Assert.Equal(b.Name, BinaryIndex.Pick([b], "ruby-4.0.5-x64-mswin64_140", X64Host)!.Name);
        Assert.Equal(b.Name, BinaryIndex.Pick([b], "RUBY-4.0.5-X64-MSWIN64_140", X64Host)!.Name);
    }

    [Fact] // case 125: arm64 hosts add x64 after their own platform
    public void PlatformsFor_Arm64PrefersItsOwnThenX64()
    {
        Assert.Equal([BinaryIndex.Arm64, BinaryIndex.X64], Arm64Host);
        Assert.Equal([BinaryIndex.X64], X64Host);
        Assert.Equal([BinaryIndex.X64], BinaryIndex.PlatformsFor(Architecture.X86));
    }

    // 3.4 has an arm64 build and a newer x64 one, 3.3 is x64 only.
    private static readonly Build[] Mixed =
    [
        Make("ruby-3.4.13-x64-mswin64_140", "3.4.13", tags: ["3.4.13", "3.4", "3"]),
        Make("ruby-3.4.12-arm64-mswin64_140", "3.4.12", tags: ["3.4.12", "3.4", "3"],
            platform: BinaryIndex.Arm64),
        Make("ruby-3.3.11-x64-mswin64_140", "3.3.11", tags: ["3.3.11", "3.3", "3"]),
    ];

    [Fact] // case 126: tags are per platform, so arm64 wins over a newer x64
    public void Pick_Arm64Host_PrefersArm64OverNewerX64()
    {
        Assert.Equal("ruby-3.4.12-arm64-mswin64_140",
            BinaryIndex.Pick(Mixed, "3.4", Arm64Host)!.Name);
        Assert.Equal("ruby-3.4.12-arm64-mswin64_140",
            BinaryIndex.Pick(Mixed.Reverse(), "3", Arm64Host)!.Name);
        Assert.Equal("ruby-3.4.13-x64-mswin64_140",
            BinaryIndex.Pick(Mixed, "3.4", X64Host)!.Name);
    }

    [Fact] // case 127: a tag with no arm64 build falls back to x64
    public void Pick_Arm64Host_FallsBackToX64()
    {
        Assert.Equal("ruby-3.3.11-x64-mswin64_140",
            BinaryIndex.Pick(Mixed, "3.3", Arm64Host)!.Name);
    }

    [Fact] // case 128: a full package name bypasses the platform order
    public void Pick_Arm64Host_FullX64NameIsTaken()
    {
        Assert.Equal("ruby-3.4.13-x64-mswin64_140",
            BinaryIndex.Pick(Mixed, "ruby-3.4.13-x64-mswin64_140", Arm64Host)!.Name);
    }

    [Fact] // case 145: only the matches are ranked, so another build's version never is
    public void Pick_RanksOnlyTheMatches()
    {
        Build[] builds = [.. Mixed, Make("ruby-head-x64-mswin64_140", "head", tags: ["head"])];

        Assert.Equal("ruby-3.4.12-arm64-mswin64_140",
            BinaryIndex.Pick(builds, "3.4", Arm64Host)!.Name);
        Assert.Equal("ruby-3.3.11-x64-mswin64_140",
            BinaryIndex.Pick(builds, "ruby-3.3.11-x64-mswin64_140", X64Host)!.Name);
    }

    [Fact] // case 129: arm64 lines first, each platform newest first
    public void Available_Arm64Host_Arm64FirstThenX64()
    {
        Assert.Equal(
            [
                "ruby-3.4.12-arm64-mswin64_140",
                "ruby-3.4.13-x64-mswin64_140",
                "ruby-3.3.11-x64-mswin64_140",
            ],
            BinaryIndex.Available(Mixed, Arm64Host).Select(b => b.Name).ToArray());
    }

    [Fact] // case 119: the order `rb list --remote` prints, newest first
    public void Available_NewestFirst()
    {
        Build[] builds =
        [
            Make("ruby-4.0.4-x64-mswin64_140", "4.0.4", tags: ["4.0.4", "4.0"]),
            Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", revision: 0, tags: ["4.0.5-0"]),
            Make("ruby-4.0.5-1-x64-mswin64_140", "4.0.5", revision: 1, tags: ["4.0.5-1"]),
        ];

        Assert.Equal(
            [
                "ruby-4.0.5-1-x64-mswin64_140",
                "ruby-4.0.5-x64-mswin64_140",
                "ruby-4.0.4-x64-mswin64_140",
            ],
            BinaryIndex.Available(builds, X64Host).Select(b => b.Name).ToArray());
    }

    [Fact] // case 120: builds this rb cannot install are not offered
    public void Available_ForeignPlatform_Filtered()
    {
        Build[] builds =
        [
            Make("ruby-4.0.5-arm64-mswin64_140", "4.0.5", platform: "arm64-mswin64_140"),
            Make("ruby-4.0.5-x64-mswin64_140", "4.0.5"),
        ];

        Build b = Assert.Single(BinaryIndex.Available(builds, X64Host));
        Assert.Equal("ruby-4.0.5-x64-mswin64_140", b.Name);
    }

    [Fact] // case 107
    public void Pick_NoMatch_ReturnsNull()
    {
        Build b = Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", tags: ["4.0.5", "4.0", "4"]);

        Assert.Null(BinaryIndex.Pick([b], "3.9", X64Host));
    }

    [Fact] // case 108: a prerelease tag never shadows the release of a series
    public void Pick_PrereleaseExactTagOnly()
    {
        Build[] builds =
        [
            Make("ruby-4.1.0-rc1-x64-mswin64_140", "4.1.0-rc1", channel: "prerelease",
                tags: ["4.1.0-rc1"]),
            Make("ruby-4.0.5-x64-mswin64_140", "4.0.5", tags: ["4.0.5", "4.0", "4"]),
        ];

        Assert.Equal("ruby-4.1.0-rc1-x64-mswin64_140",
            BinaryIndex.Pick(builds, "4.1.0-rc1", X64Host)!.Name);
        Assert.Null(BinaryIndex.Pick(builds, "4.1", X64Host));
    }
}

// Opt-in: proves the published index still parses and resolves. Needs the
// network, so it is skipped unless RBMANAGER_TEST_NETWORK=1.
[Trait("Category", "Network")]
public class BinaryIndexNetworkTests
{
    [SkippableFact] // case 109
    public async Task PublishedIndex_ParsesAndResolvesRubyDev()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RBMANAGER_TEST_NETWORK") == "1",
            "set RBMANAGER_TEST_NETWORK=1 to run network tests");

        using var http = new HttpClient();
        string json = await http.GetStringAsync(
            "https://cache.ruby-lang.org/pub/ruby/binaries/index.json");

        IndexPage page = BinaryIndex.Parse(json);
        Build? build = BinaryIndex.Pick(page.Builds, "ruby-dev",
            BinaryIndex.PlatformsFor(Architecture.X64));
        Assert.NotNull(build);
        Assert.Equal(BinaryIndex.X64, build!.Platform);
        Assert.Matches("^[0-9a-f]{64}$", build.Sha256);
        Assert.StartsWith("https://cache.ruby-lang.org/", build.Url);
    }
}
