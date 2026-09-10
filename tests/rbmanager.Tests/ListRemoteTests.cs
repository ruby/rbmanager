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
        string[] tags, string platform = BinaryIndex.Platform, string? commitDate = null) => $$"""
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

    [Fact] // case 121: name, channel and the tags `install` accepts
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
                $"{Dev}  dev      ruby-dev, 4.1-dev",
                $"{Rel}                         release  4.0.5, 4.0, 4",
            ],
            cap.OutLines);
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
        Assert.Contains($"no {BinaryIndex.Platform} builds", cap.Err);
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
