using RbManager.Tests.Support;

namespace RbManager.Tests;

// `rb list --remote`: the index rendered as columns, with the feed
// redirected to a local file via RBMANAGER_INDEX_URL. Serial (env vars +
// console).
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class ListRemoteTests
{
    private const string Dev = "ruby-4.1.0dev-20260821-0123456789-x64-mswin64_140";
    private const string Rel = "ruby-4.0.5-x64-mswin64_140";

    private static string BuildJson(string name, string version, string channel,
        string[] tags, string platform = BinaryIndex.X64, string? commitDate = null) => $$"""
        {
          "name": "{{name}}",
          "version": "{{version}}",
          "channel": "{{channel}}",
          "revision": null,
          "tags": [{{string.Join(", ", tags.Select(t => $"\"{t}\""))}}],
          "platform": "{{platform}}",
          "url": "https://example.invalid/{{name}}.zip",
          "sha256": "{{new string('0', 64)}}",
          "size": 1,
          "commit": null,
          "commit_date": {{(commitDate is null ? "null" : $"\"{commitDate}\"")}},
          "published_at": null,
          "signed": false
        }
        """;

    private static void WriteIndex(RbSandbox sb, EnvScope env, params string[] builds)
    {
        string path = Path.Combine(sb.Root, "_index", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {"schema": 1, "next": null, "builds": [{{string.Join(",", builds)}}]}
            """);
        env.Set("RBMANAGER_INDEX_URL", path);
    }

    [Fact] // case 121: name, channel, platform and the tags `install` accepts
    public async Task ListsBuildsNewestFirstInColumns()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        WriteIndex(sb, env,
            BuildJson(Rel, "4.0.5", "release", ["4.0.5", "4.0", "4"]),
            BuildJson(Dev, "4.1.0dev", "dev", ["ruby-dev", "4.1-dev"],
                commitDate: "2026-08-21"));

        int rc = await Program.ListRemote();

        Assert.Equal(0, rc);
        Assert.Equal(
            [
                $"{Dev}  dev      x64-mswin64_140  ruby-dev, 4.1-dev",
                $"{Rel}                         release  x64-mswin64_140  4.0.5, 4.0, 4",
            ],
            cap.OutLines);
    }

    [Fact] // case 130: an arm64 host lists its own builds first, then x64
    public async Task Arm64Host_ListsArm64ThenX64()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        env.Set("RBMANAGER_OS_ARCH", "Arm64");
        WriteIndex(sb, env,
            BuildJson("ruby-4.0.10-x64-mswin64_140", "4.0.10", "release", ["4.0.10", "4.0"]),
            BuildJson("ruby-4.0.9-arm64-mswin64_140", "4.0.9", "release", ["4.0.9", "4.0"],
                platform: BinaryIndex.Arm64));

        int rc = await Program.ListRemote();

        Assert.Equal(0, rc);
        Assert.Equal(
            [
                "ruby-4.0.9-arm64-mswin64_140  release  arm64-mswin64_140  4.0.9, 4.0",
                "ruby-4.0.10-x64-mswin64_140   release  x64-mswin64_140    4.0.10, 4.0",
            ],
            cap.OutLines);
    }

    [Theory] // case 146: an override naming no Architecture stops and names the variable
    [InlineData("arm64ec")]
    [InlineData("99")]
    public async Task UnknownOsArchOverride_Throws(string arch)
    {
        using var sb = new RbSandbox();
        using var env = new EnvScope();
        WriteIndex(sb, env, BuildJson(Rel, "4.0.5", "release", ["4.0.5"]));
        env.Set("RBMANAGER_OS_ARCH", arch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Program.ListRemote);

        Assert.Contains("RBMANAGER_OS_ARCH", ex.Message);
        Assert.Contains($"'{arch}'", ex.Message);
    }

    [Fact] // case 122: an index with nothing for this platform is not an error
    public async Task NoBuildsForThePlatform_NotesOnStderr_Exit0()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        WriteIndex(sb, env,
            BuildJson("ruby-4.0.5-arm64-mswin64_140", "4.0.5", "release", ["4.0.5"],
                platform: "arm64-mswin64_140"));

        int rc = await Program.ListRemote();

        Assert.Equal(0, rc);
        Assert.Equal("", cap.Out);
        Assert.Contains($"no {BinaryIndex.X64} builds", cap.Err);
    }

    [Fact] // case 123: an index this rb does not understand fails here too
    public async Task UnsupportedSchema_Throws()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        string path = Path.Combine(sb.Root, "_index", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"schema": 2, "next": null, "builds": []}""");
        env.Set("RBMANAGER_INDEX_URL", path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Program.ListRemote);

        Assert.Contains("schema 2", ex.Message);
    }
}
