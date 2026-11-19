using System.Security.Cryptography;
using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.16: rb install --update [<series>]. Installed rubies are seeded
// directories, the feed is a local file, and the one package an update
// downloads comes from the loopback server. Every other entry points at
// an unreachable URL, so an unexpected download fails the test. Serial
// (env vars + console).
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class UpdateTests
{
    private const string V408 = "ruby-4.0.8-x64-mswin64_140";
    private const string V409 = "ruby-4.0.9-x64-mswin64_140";
    private const string Arm409 = "ruby-4.0.9-arm64-mswin64_140";
    private const string V341 = "ruby-3.4.1-x64-mswin64_140";
    private const string V342 = "ruby-3.4.2-x64-mswin64_140";
    private const string Dev0820 = "ruby-4.1.0dev-20260820-aaaaaaaaaa-x64-mswin64_140";
    private const string Dev0821 = "ruby-4.1.0dev-20260821-bbbbbbbbbb-x64-mswin64_140";
    private const string Unreachable = "https://example.invalid/x.zip";

    private static string Json(string[] tags) =>
        "[" + string.Join(", ", tags.Select(t => $"\"{t}\"")) + "]";

    // A release entry tagged as the newest revision of its version.
    private static string Release(string name, string version, string url = Unreachable,
        string? sha256 = null, string platform = BinaryIndex.X64)
    {
        string series = string.Join('.', version.Split('.').Take(2));
        return $$"""
            {"name": "{{name}}", "version": "{{version}}", "channel": "release", "revision": 0,
             "tags": {{Json([version, series, version.Split('.')[0]])}}, "platform": "{{platform}}",
             "url": "{{url}}", "sha256": "{{sha256 ?? new string('0', 64)}}", "size": 1,
             "commit": null, "commit_date": null, "published_at": "2026-11-01", "signed": true}
            """;
    }

    private static string Dev(string name, string date, string commit, string url = Unreachable,
        string? sha256 = null, string series = "4.1", bool rubyDev = false) => $$"""
        {"name": "{{name}}", "version": "{{series}}.0dev", "channel": "dev", "revision": null,
         "tags": {{Json([$"{series}-dev", $"{series}-dev-{date}", $"{series}.0dev-{date}-{commit}",
             .. rubyDev ? new[] { "ruby-dev" } : []])}},
         "platform": "{{BinaryIndex.X64}}", "url": "{{url}}",
         "sha256": "{{sha256 ?? new string('0', 64)}}", "size": 1, "commit": "{{commit}}",
         "commit_date": "{{date[..4]}}-{{date[4..6]}}-{{date[6..]}}",
         "published_at": "2026-11-01", "signed": false}
        """;

    private static void UseIndex(RbSandbox sb, EnvScope env, params string[] builds)
    {
        string path = Path.Combine(sb.Root, "_index", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $$"""{"schema": 1, "next": null, "builds": [{{string.Join(",", builds)}}]}""");
        env.Set("RBMANAGER_INDEX_URL", path);
    }

    // Serves the package for `name`; Sha256 is what its index entry records.
    private sealed class Package : IDisposable
    {
        private readonly LoopbackZipServer _server;
        public string Url => _server.ZipUrl;
        public string Sha256 { get; }

        public Package(RbSandbox sb, string name)
        {
            byte[] zip = File.ReadAllBytes(
                Zips.WriteRuby(Path.Combine(sb.Root, "_src", $"{name}.zip"), name));
            _server = new LoopbackZipServer(zip, $"/{name}.zip");
            Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        }

        public void Dispose() => _server.Dispose();
    }

    [Theory] // case 151
    [InlineData(V408, "4.0", BinaryIndex.X64)]
    [InlineData("ruby-4.0.5-1-arm64-mswin64_140", "4.0", BinaryIndex.Arm64)]
    [InlineData(Dev0821, "4.1-dev", BinaryIndex.X64)]
    [InlineData("ruby-4.1.0-rc1-x64-mswin64_140", null, null)]
    [InlineData("ruby-head-x64-mswin64_140", null, null)]
    public void Follow_MapsAPackageToItsSeriesAndPlatform(
        string name, string? series, string? platform)
    {
        Program.Followed? f = Program.Follow(name);

        Assert.Equal(series, f?.Series);
        Assert.Equal(platform, f?.Platform);
    }

    [Fact] // case 152
    public void Follow_RanksReissuesAndSnapshotsAsTheIndexDoes()
    {
        static Program.Followed F(string name) => Program.Follow(name)!;

        Assert.True(F("ruby-4.0.5-1-x64-mswin64_140").Rank.CompareTo(
            F("ruby-4.0.5-x64-mswin64_140").Rank) > 0);
        Assert.True(F("ruby-4.0.10-x64-mswin64_140").Rank.CompareTo(F(V409).Rank) > 0);
        Assert.True(F(Dev0821).Rank.CompareTo(F(Dev0820).Rank) > 0);
    }

    [Fact] // case 153
    public async Task UpdateAll_InstallsNextToTheOld_AndMovesCurrent()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, V409);
        sb.Seed(V408);
        sb.Activate(V408);
        UseIndex(sb, env, Release(V409, "4.0.9", pkg.Url, pkg.Sha256), Release(V408, "4.0.8"));

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V409)));
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V408)));
        Assert.Equal(V409, Program.CurrentTarget());
        Assert.Contains($"Updating {V408} to {V409}", cap.Out);
        Assert.Contains($"Installed {V409}", cap.Out);
        Assert.Contains($"Kept {V408} (run `rb uninstall {V408}` to remove it)", cap.Out);
    }

    [Fact] // case 154
    public async Task UpdateAll_AlreadyNewest_DownloadsNothing()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        sb.Seed(V409);
        sb.Activate(V409);
        UseIndex(sb, env, Release(V409, "4.0.9"), Release(V408, "4.0.8"));

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.Equal($"{V409} is up to date", cap.Out.TrimEnd());
    }

    [Fact] // case 155
    public async Task UpdateSeries_LeavesOtherSeriesAndTheirCurrentAlone()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, V342);
        sb.Seed(V341, V408);
        sb.Activate(V408);
        UseIndex(sb, env, Release(V409, "4.0.9"), Release(V408, "4.0.8"),
            Release(V342, "3.4.2", pkg.Url, pkg.Sha256), Release(V341, "3.4.1"));

        int rc = await Program.Update("3.4");

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V342)));
        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, V409)));
        Assert.Equal(V408, Program.CurrentTarget());
        Assert.DoesNotContain("Open a new terminal", cap.Out);
    }

    [Fact] // case 156: an x64 ruby on ARM64 stays x64
    public async Task UpdateAll_Arm64Host_KeepsTheInstalledPlatform()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, V409);
        env.Set("RBMANAGER_OS_ARCH", "Arm64");
        sb.Seed(V408);
        sb.Activate(V408);
        UseIndex(sb, env, Release(Arm409, "4.0.9", platform: BinaryIndex.Arm64),
            Release(V409, "4.0.9", pkg.Url, pkg.Sha256), Release(V408, "4.0.8"));

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.Equal(V409, Program.CurrentTarget());
        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, Arm409)));
        Assert.Equal("", cap.Err);
    }

    [Fact] // case 157: a hand-installed build newer than the index is not downgraded
    public async Task UpdateAll_InstalledNewerThanTheIndex_IsUpToDate()
    {
        const string v4010 = "ruby-4.0.10-x64-mswin64_140";
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        sb.Seed(v4010);
        sb.Activate(v4010);
        UseIndex(sb, env, Release(V409, "4.0.9"));

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.Equal(v4010, Program.CurrentTarget());
        Assert.Contains($"{v4010} is up to date", cap.Out);
    }

    [Fact] // case 158
    public async Task UpdateAll_DevSnapshot_MovesToTheNewestOfItsSeries()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, Dev0821);
        sb.Seed(Dev0820);
        sb.Activate(Dev0820);
        UseIndex(sb, env, Dev(Dev0821, "20260821", "bbbbbbbbbb", pkg.Url, pkg.Sha256),
            Dev(Dev0820, "20260820", "aaaaaaaaaa"));

        int rc = await Program.Update("4.1-dev");

        Assert.Equal(0, rc);
        Assert.Equal(Dev0821, Program.CurrentTarget());
        Assert.Contains($"warning: {Dev0821} is not code-signed", cap.Err);
    }

    [Fact] // case 159: a series with no index build for a platform is skipped
    public async Task UpdateAll_NoBuildForAPlatform_WarnsAndUpdatesTheRest()
    {
        const string x86 = "ruby-4.0.8-x86-mswin64_140";
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, V342);
        sb.Seed(V341, x86);
        UseIndex(sb, env, Release(V342, "3.4.2", pkg.Url, pkg.Sha256), Release(V341, "3.4.1"));

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V342)));
        Assert.Contains("warning: the binary index has no x86-mswin64_140 build of 4.0", cap.Err);
    }

    [Fact] // case 160
    public async Task UpdateSeries_NothingInstalledFollowsIt_Throws()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        env.Set("RBMANAGER_INDEX_URL", Path.Combine(sb.Root, "_index", "absent.json"));
        sb.Seed(V408);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.Update("3.4"));

        Assert.Equal("no installed ruby follows '3.4' (installed rubies follow 4.0)", ex.Message);
    }

    private static void Record(RbSandbox sb, string name, string tag) =>
        File.WriteAllText(Path.Combine(sb.Rubies, name, Program.TagFile), tag);

    private static string? RecordedTag(RbSandbox sb, string name)
    {
        string path = Path.Combine(sb.Rubies, name, Program.TagFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private const string Dev1226 = "ruby-4.2.0dev-20261226-cccccccccc-x64-mswin64_140";

    [Theory] // case 163: ruby-dev moves on to the next dev series
    [InlineData(null)]
    [InlineData("ruby-dev")]
    public async Task Update_RecordedRubyDev_FollowsItAcrossSeries(string? series)
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, Dev1226);
        sb.Seed(Dev0821);
        Record(sb, Dev0821, "ruby-dev");
        sb.Activate(Dev0821);
        UseIndex(sb, env,
            Dev(Dev1226, "20261226", "cccccccccc", pkg.Url, pkg.Sha256, series: "4.2", rubyDev: true),
            Dev(Dev0821, "20260821", "bbbbbbbbbb"));

        int rc = await Program.Update(series);

        Assert.Equal(0, rc);
        Assert.Equal(Dev1226, Program.CurrentTarget());
        Assert.Equal("ruby-dev", RecordedTag(sb, Dev1226));
    }

    [Fact] // case 164: a named series stays within itself, even for a ruby installed as `4`
    public async Task UpdateSeries_RecordedMajorTag_StaysWithinTheSeries()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, V409);
        sb.Seed(V408);
        Record(sb, V408, "4");
        UseIndex(sb, env, Release("ruby-4.1.0-x64-mswin64_140", "4.1.0"),
            Release(V409, "4.0.9", pkg.Url, pkg.Sha256), Release(V408, "4.0.8"));

        int rc = await Program.Update("4.0");

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V409)));
        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, "ruby-4.1.0-x64-mswin64_140")));
        Assert.Null(RecordedTag(sb, V409));
    }

    [Fact] // case 165: `current` on an older ruby of the series was chosen by `rb use`
    public async Task UpdateAll_CurrentPinnedToAnOlderPatch_Stays()
    {
        const string v407 = "ruby-4.0.7-x64-mswin64_140";
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        using var pkg = new Package(sb, V409);
        sb.Seed(v407, V408);
        sb.Activate(v407);
        UseIndex(sb, env, Release(V409, "4.0.9", pkg.Url, pkg.Sha256), Release(V408, "4.0.8"),
            Release(v407, "4.0.7"));

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V409)));
        Assert.Equal(v407, Program.CurrentTarget());
    }

    [Fact] // case 161: a prerelease follows no series
    public async Task UpdateAll_NothingFollowsASeries_ExitsZero()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        env.Set("RBMANAGER_INDEX_URL", Path.Combine(sb.Root, "_index", "absent.json"));
        sb.Seed("ruby-4.1.0-rc1-x64-mswin64_140");

        int rc = await Program.Update(series: null);

        Assert.Equal(0, rc);
        Assert.Equal("Nothing to update", cap.Out.TrimEnd());
    }
}
