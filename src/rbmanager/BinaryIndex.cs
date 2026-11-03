using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RbManager;

// Consumer of https://cache.ruby-lang.org/pub/ruby/binaries/index.json,
// the feed ruby/actions regenerates after every mswin package publish
// (tool/update_binaries_index.rb there). The feed is the resolution
// authority: builds are matched through their `tags` rather than by
// parsing `version`, whose syntax depends on the channel.
internal static class BinaryIndex
{
    internal const string X64 = "x64-mswin64_140";
    internal const string Arm64 = "arm64-mswin64_140";

    // The platforms this machine runs, native first. ARM64 Windows runs
    // x64 binaries under emulation, so x64 builds back up arm64 there.
    internal static string[] Platforms => PlatformsFor(HostArchitecture());

    internal static string[] PlatformsFor(Architecture host) =>
        host == Architecture.Arm64 ? [Arm64, X64] : [X64];

    // OSArchitecture reports the machine rather than the process, so
    // rb-x64.exe under emulation still sees Arm64. RBMANAGER_OS_ARCH
    // stands in for it in tests.
    private static Architecture HostArchitecture()
    {
        if (Environment.GetEnvironmentVariable("RBMANAGER_OS_ARCH") is not { Length: > 0 } arch)
            return RuntimeInformation.OSArchitecture;
        string name = Enum.GetNames<Architecture>()
            .FirstOrDefault(n => n.Equals(arch, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"RBMANAGER_OS_ARCH must name an Architecture such as X64 or Arm64, not '{arch}'");
        return Enum.Parse<Architecture>(name);
    }

    internal static bool IsOn(string name, string platform) =>
        name.EndsWith($"-{platform}", StringComparison.OrdinalIgnoreCase);

    private const string DefaultUrl =
        "https://cache.ruby-lang.org/pub/ruby/binaries/index.json";

    // RBMANAGER_INDEX_URL redirects the feed for tests; it accepts an
    // http(s) URL, a file:// URL, or an absolute local path.
    private static string Url =>
        Environment.GetEnvironmentVariable("RBMANAGER_INDEX_URL") is { Length: > 0 } url
            ? url
            : DefaultUrl;

    public static async Task<Build> Resolve(string query) =>
        Pick(await FetchAll(), query, Platforms) ?? throw new InvalidOperationException(
            $"no binary package matches '{query}' in the index");

    public static async Task<Build[]> Available() => Available(await FetchAll(), Platforms);

    private static async Task<List<Build>> FetchAll()
    {
        var page = new Uri(Url, UriKind.Absolute);
        var builds = new List<Build>();
        while (true)
        {
            IndexPage index = Parse(await Fetch(page));
            builds.AddRange(index.Builds);
            if (index.Next is null) break;
            page = new Uri(page, index.Next);
        }
        return builds;
    }

    private static async Task<string> Fetch(Uri uri)
    {
        if (uri.IsFile) return await File.ReadAllTextAsync(uri.LocalPath);
        using var http = new HttpClient();
        return await http.GetStringAsync(uri);
    }

    internal static IndexPage Parse(string json)
    {
        IndexPage page = JsonSerializer.Deserialize(json, IndexJsonContext.Default.IndexPage)
            ?? throw new InvalidOperationException("the binary index is empty");
        // The schema number is the only channel the feed has for telling
        // an old rb that it is old.
        if (page.Schema != 1)
            throw new InvalidOperationException(
                $"{Program.SelfVersion()} does not understand schema {page.Schema} " +
                $"of the binary index. Upgrade from {Program.ReleasesUrl}");
        return page;
    }

    // The first of `platforms` with a match wins, even over a newer build
    // of a later one, because the feed assigns tags per platform. Within
    // it the newest match wins regardless of feed order: series tags like
    // "4.0" sit on every 4.0.x release, and dev tags like "4.1-dev" on
    // every snapshot of the series. A full package name matches only its
    // own build, so it is taken whatever its platform.
    internal static Build? Pick(IEnumerable<Build> builds, string query, string[] platforms) =>
        Ordered(builds.Where(b => b.Tags.Contains(query, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(b.Name, query, StringComparison.OrdinalIgnoreCase)), platforms)
            .FirstOrDefault();

    // Everything installable here, in the order Pick resolves, so a tag
    // always installs the topmost line carrying it.
    internal static Build[] Available(IEnumerable<Build> builds, string[] platforms) =>
        Ordered(builds, platforms).ToArray();

    private static IEnumerable<Build> Ordered(IEnumerable<Build> builds, string[] platforms) =>
        builds
            .Where(b => platforms.Contains(b.Platform))
            .OrderBy(b => Array.IndexOf(platforms, b.Platform))
            .ThenByDescending(Rank);

    // Version, then reissue revision (SIGNING.md in ruby/actions), then
    // commit date, which keeps this consistent with Program.Resolve's
    // revision handling for installed rubies.
    private static (Version, int, string, string) Rank(Build b) =>
        (NumericVersion(b.Version), b.Revision ?? 0,
            b.CommitDate ?? b.PublishedAt ?? "", b.Commit ?? "");

    // The numeric prefix of `version` ("4.1.0dev" and "4.1.0-rc1" both
    // compare as 4.1.0). Channel suffixes never decide between two
    // matches of one tag: a tag matches either releases or dev builds,
    // never both.
    private static Version NumericVersion(string version)
    {
        int end = 0;
        while (end < version.Length && (char.IsAsciiDigit(version[end]) || version[end] == '.'))
            end++;
        return Version.Parse(version[..end].TrimEnd('.'));
    }
}

internal sealed record IndexPage
{
    public int Schema { get; init; }
    public string? Next { get; init; }
    public List<Build> Builds { get; init; } = [];
}

// One build entry. Every key is always present in the feed, with null
// standing in where a key does not apply to the channel.
internal sealed record Build
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public int? Revision { get; init; }
    public required string[] Tags { get; init; }
    public required string Platform { get; init; }
    public required string Url { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public string? Commit { get; init; }
    public string? CommitDate { get; init; }
    public string? PublishedAt { get; init; }
    public bool Signed { get; init; }
}

// Reflection-free serializer for NativeAOT.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IndexPage))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;
