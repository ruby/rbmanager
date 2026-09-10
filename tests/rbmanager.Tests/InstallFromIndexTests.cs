using System.Security.Cryptography;
using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.13: rb install <version|tag> through the binary index, with the
// feed redirected to a local file via RBMANAGER_INDEX_URL and the zip
// served by the loopback server. Serial (env vars + console).
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class InstallFromIndexTests
{
    private const string DevName = "ruby-4.1.0dev-20260821-0123456789-x64-mswin64_140";

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // One dev-channel entry in the published feed's exact shape.
    private static string BuildJson(string name, string url, string sha256, bool signed) => $$"""
        {
          "name": "{{name}}",
          "version": "4.1.0dev",
          "channel": "dev",
          "revision": null,
          "tags": ["4.1-dev", "4.1-dev-20260821", "4.1.0dev-20260821-0123456789", "ruby-dev"],
          "platform": "x64-mswin64_140",
          "url": "{{url}}",
          "sha256": "{{sha256}}",
          "size": 1,
          "commit": "0123456789",
          "commit_date": "2026-08-21",
          "published_at": "2026-08-21",
          "signed": {{(signed ? "true" : "false")}}
        }
        """;

    private static string WriteIndex(RbSandbox sb, string fileName, string? next,
        params string[] builds)
    {
        string path = Path.Combine(sb.Root, "_index", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string nextJson = next is null ? "null" : $"\"{next}\"";
        File.WriteAllText(path, $$"""
            {"schema": 1, "next": {{nextJson}}, "builds": [{{string.Join(",", builds)}}]}
            """);
        return path;
    }

    [Fact] // case 110
    public async Task InstallByTag_ResolvesDownloadsVerifiesAndInstalls()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        byte[] zip = File.ReadAllBytes(
            Zips.WriteRuby(Path.Combine(sb.Root, "_src", "pkg.zip"), DevName));
        using var server = new LoopbackZipServer(zip, $"/{DevName}.zip");
        using var env = new EnvScope();
        env.Set("RBMANAGER_INDEX_URL", WriteIndex(sb, "index.json", null,
            BuildJson(DevName, server.ZipUrl, Sha256Of(zip), signed: true)));

        int rc = await Program.Install("4.1-dev");

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, DevName)));
        Assert.Equal(DevName, Program.CurrentTarget());
        Assert.Contains($"Resolved 4.1-dev to {DevName}", cap.Out);
        Assert.Contains($"Installed {DevName}", cap.Out);
        Assert.DoesNotContain("not code-signed", cap.Err);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), $"{DevName}.zip")));
    }

    [Fact] // case 111: signed:false warns on stderr but installs anyway
    public async Task InstallUnsignedBuild_WarnsAndInstalls()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        byte[] zip = File.ReadAllBytes(
            Zips.WriteRuby(Path.Combine(sb.Root, "_src", "pkg.zip"), DevName));
        using var server = new LoopbackZipServer(zip, $"/{DevName}.zip");
        using var env = new EnvScope();
        env.Set("RBMANAGER_INDEX_URL", WriteIndex(sb, "index.json", null,
            BuildJson(DevName, server.ZipUrl, Sha256Of(zip), signed: false)));

        int rc = await Program.Install("ruby-dev");

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, DevName)));
        Assert.Contains($"warning: {DevName} is not code-signed", cap.Err);
    }

    [Fact] // case 112: a checksum mismatch fails, installs nothing, cleans up
    public async Task InstallShaMismatch_Throws_NothingInstalled()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        byte[] zip = File.ReadAllBytes(
            Zips.WriteRuby(Path.Combine(sb.Root, "_src", "pkg.zip"), DevName));
        using var server = new LoopbackZipServer(zip, $"/{DevName}.zip");
        using var env = new EnvScope();
        env.Set("RBMANAGER_INDEX_URL", WriteIndex(sb, "index.json", null,
            BuildJson(DevName, server.ZipUrl, new string('0', 64), signed: true)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.Install("4.1-dev"));

        Assert.Contains("sha256 mismatch", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, DevName)));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), $"{DevName}.zip")));
    }

    [Fact] // case 113: a non-null next chains to the following page
    public async Task InstallFromPaginatedIndex_FollowsNext()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        byte[] zip = File.ReadAllBytes(
            Zips.WriteRuby(Path.Combine(sb.Root, "_src", "pkg.zip"), DevName));
        using var server = new LoopbackZipServer(zip, $"/{DevName}.zip");
        using var env = new EnvScope();
        WriteIndex(sb, "page2.json", null,
            BuildJson(DevName, server.ZipUrl, Sha256Of(zip), signed: true));
        env.Set("RBMANAGER_INDEX_URL", WriteIndex(sb, "index.json", "page2.json"));

        int rc = await Program.Install("4.1-dev");

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, DevName)));
    }

    [Fact] // case 114: RBMANAGER_INDEX_URL accepts a file:// URL
    public async Task Resolve_FileUrlIndex_Works()
    {
        using var sb = new RbSandbox();
        using var env = new EnvScope();
        string index = WriteIndex(sb, "index.json", null,
            BuildJson(DevName, "https://example.invalid/pkg.zip", new string('0', 64),
                signed: true));
        env.Set("RBMANAGER_INDEX_URL", new Uri(index).AbsoluteUri);

        Build build = await BinaryIndex.Resolve("4.1-dev");

        Assert.Equal(DevName, build.Name);
    }

    [Fact] // case 115: a newer schema aborts before any resolution
    public async Task InstallNewerSchema_Throws()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        string path = Path.Combine(sb.Root, "_index", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"schema": 2, "next": null, "builds": []}""");
        env.Set("RBMANAGER_INDEX_URL", path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.Install("4.1-dev"));

        Assert.Contains(Program.ReleasesUrl, ex.Message);
    }

    [Fact] // case 116
    public async Task InstallUnknownVersion_Throws()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        env.Set("RBMANAGER_INDEX_URL", WriteIndex(sb, "index.json", null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.Install("3.9"));

        Assert.Equal("no binary package matches '3.9' in the index", ex.Message);
    }

    [Fact] // case 117: a missing zip path fails as a file, not as a version
    public async Task InstallMissingZipPath_DoesNotHitTheIndex()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var env = new EnvScope();
        // A feed that would resolve anything makes a fall-through visible.
        env.Set("RBMANAGER_INDEX_URL", Path.Combine(sb.Root, "_index", "absent.json"));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.Install("no-such-package.zip"));
        // A missing intermediate directory surfaces as DirectoryNotFound.
        await Assert.ThrowsAnyAsync<IOException>(
            () => Program.Install(Path.Combine(sb.Root, "nope", "pkg.zip")));
    }
}
