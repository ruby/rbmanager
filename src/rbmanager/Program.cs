using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RbManager;

internal static class Program
{
    // %LOCALAPPDATA%\Ruby, named after the language like pymanager's
    // %LocalAppData%\Python, not after the tool. Computed per access (not
    // cached in a static initializer) so RBMANAGER_ROOT can redirect the
    // whole layout for tests; the Known Folder API ignores %LOCALAPPDATA%.
    private static string Root =>
        Environment.GetEnvironmentVariable("RBMANAGER_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ruby");
    private static string Rubies => Path.Combine(Root, "rubies");
    private static string Current => Path.Combine(Root, "current");

    internal const string ReleasesUrl = "https://github.com/ruby/rbmanager/releases";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["setup"] => await Setup(assumeYes: false),
                ["setup", "--yes" or "-y"] => await Setup(assumeYes: true),
                ["install", var source] => await Install(source),
                ["list"] => List(),
                ["list", "--remote"] => await ListRemote(),
                ["use", var name] => Use(name),
                ["uninstall", var name] => Uninstall(name),
                // The flag spellings answer too: probing a tool with
                // --version is how a caller identifies the build it got,
                // and falling to usage there reads as a broken binary.
                ["version" or "--version" or "-V"] => Version(),
                // Everything after `msvc` belongs to Msvc's own parser: it
                // owns one reserved word (`enable`) and passes the rest
                // through as the user's command line.
                ["msvc", .. var rest] =>
                    Msvc.Parse(rest) is { } msvc ? Msvc.Dispatch(msvc) : Usage(),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"rb: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            usage: rb <command>

              setup [--yes]          copy rb onto PATH and set up the VC++ runtime
              install <version|zip>  install a ruby binary package resolved from the
                                     binary index, or from a zip file or URL
              list                   list installed rubies
              list --remote          list the builds the binary index offers
              use <version>          switch the active ruby
              uninstall <version>    remove an installed ruby
              msvc <command...>      run a command with the MSVC build env applied
              msvc enable [shell]    print the MSVC build env to eval (cmd|powershell)
              msvc --list            list installed Visual Studio C++ toolchains
                                     (msvc and msvc enable accept --vsver <year>)
              version                print the rbmanager version
                                     (also --version, -V)
            """);
        return 2;
    }

    // Self-installation: rbmanager is distributed as a bare exe, so `setup`
    // is what makes it durably available instead of an installer. It also
    // checks (and offers to install) the VC++ runtime the official mswin
    // packages depend on; --yes skips the consent prompt for unattended runs.
    private static async Task<int> Setup(bool assumeYes)
    {
        string self = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine own path");
        string binDir = Path.Combine(Root, "bin");
        string dest = Path.Combine(binDir, "rb.exe");
        if (!string.Equals(self, dest, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(binDir);
            File.Copy(self, dest, overwrite: true);
        }
        UserPath.Ensure(binDir);
        Console.WriteLine($"Installed rb to {dest}");
        Console.WriteLine("Open a new terminal to pick up PATH changes.");
        return await VcRedist.Ensure(assumeYes);
    }

    internal static async Task<int> Install(string source)
    {
        // Anything that is not a URL or a zip path is a version or tag to
        // resolve through the binary index (BinaryIndex.cs). sha256
        // verification is only possible on this path; a direct URL
        // carries no expected checksum.
        string? sha256 = null;
        if (!IsUrl(source) && !LooksLikeZipPath(source))
        {
            Build build = await BinaryIndex.Resolve(source);
            Console.WriteLine($"Resolved {source} to {build.Name}");
            if (!build.Signed)
                Console.Error.WriteLine($"warning: {build.Name} is not code-signed");
            source = build.Url;
            sha256 = build.Sha256;
        }

        string zip = source;
        string? downloaded = null;
        if (IsUrl(source))
        {
            downloaded = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(source).LocalPath));
            Console.WriteLine($"Downloading {source} ...");
            using var http = new HttpClient();
            await using (var body = await http.GetStreamAsync(source))
            await using (var file = File.Create(downloaded))
            {
                await body.CopyToAsync(file);
            }
            zip = downloaded;
        }

        try
        {
            if (sha256 is not null) await VerifySha256(zip, sha256);
            string name = SingleRootDirectory(zip);
            string dest = Path.Combine(Rubies, name);
            if (Directory.Exists(dest))
                throw new InvalidOperationException($"{name} is already installed");

            Console.WriteLine($"Extracting {name} ...");
            Directory.CreateDirectory(Rubies);
            ZipFile.ExtractToDirectory(zip, Rubies);

            InjectTrustHook(dest);
            SwitchTo(name);

            Console.WriteLine($"Installed {name}");
            Console.WriteLine("Open a new terminal to pick up PATH changes.");
            VcRedist.WarnIfMissing();
            return 0;
        }
        finally
        {
            if (downloaded is not null) File.Delete(downloaded);
        }
    }

    private static bool IsUrl(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // Tags in the index never contain a path separator or a .zip suffix,
    // so those mark the argument as a zip path even when the file does
    // not exist (a typo'd path must fail as a missing file, not as an
    // unknown version).
    private static bool LooksLikeZipPath(string source) =>
        File.Exists(source) || source.Contains('\\') || source.Contains('/') ||
        source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    internal static async Task VerifySha256(string file, string expected)
    {
        await using var stream = File.OpenRead(file);
        string actual = Convert.ToHexString(
            await System.Security.Cryptography.SHA256.HashDataAsync(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"sha256 mismatch for {Path.GetFileName(file)}: expected {expected}, got {actual.ToLowerInvariant()}");
    }

    internal static int List()
    {
        string? current = CurrentTarget();
        foreach (string dir in InstalledRubies())
        {
            string name = Path.GetFileName(dir);
            Console.WriteLine($"{(name == current ? "*" : " ")} {name}");
        }
        return 0;
    }

    // The index side of `list`: what this rb can install, so a caller
    // never has to fetch and interpret the feed itself. The tags are the
    // arguments `install` takes, so they carry the line; the name is what
    // the install ends up called.
    internal static async Task<int> ListRemote()
    {
        Build[] builds = await BinaryIndex.Available();
        if (builds.Length == 0)
        {
            Console.Error.WriteLine(
                $"rb: the binary index offers no {BinaryIndex.Platform} builds");
            return 0;
        }
        int name = builds.Max(b => b.Name.Length);
        int channel = builds.Max(b => b.Channel.Length);
        foreach (Build b in builds)
            Console.WriteLine($"{b.Name.PadRight(name)}  {b.Channel.PadRight(channel)}  " +
                string.Join(", ", b.Tags));
        return 0;
    }

    internal static int Use(string query)
    {
        SwitchTo(Resolve(query));
        Console.WriteLine($"Now using {CurrentTarget()}");
        VcRedist.WarnIfMissing();
        return 0;
    }

    internal static int Uninstall(string query)
    {
        string name = Resolve(query);
        if (name == CurrentTarget())
        {
            Directory.Delete(Current);
            Console.WriteLine("Removed the active selection; run `rb use` to pick another.");
        }
        Directory.Delete(Path.Combine(Rubies, name), recursive: true);
        Console.WriteLine($"Uninstalled {name}");
        return 0;
    }

    // Which build is this? An upgraded rb.exe is otherwise only
    // distinguishable by its file timestamp.
    private static int Version()
    {
        Console.WriteLine(SelfVersion());
        return 0;
    }

    // Read back from the assembly rather than kept as a literal here. The
    // release workflow stamps the version from the tag (-p:Version=<tag>)
    // and rbmanager.csproj stamps the commit, so constants in this file
    // would drift from the shipped exe.
    internal static string SelfVersion()
    {
        Assembly self = Assembly.GetExecutingAssembly();
        return FormatVersion(
            self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            self.GetName().Version?.ToString(),
            self.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "CommitDate")?.Value);
    }

    // cargo's shape, `cargo 1.75.0 (1d8b05cdd 2023-11-20)`. The commit
    // rides along as SourceRevisionId's "+<commit>" suffix on the
    // informational version. A build with no git checkout to ask has
    // neither commit nor date, and then only the version is printed.
    internal static string FormatVersion(string? informational, string? assembly, string? date)
    {
        string[] parts = (informational is { Length: > 0 } ? informational
            : assembly ?? "unknown").Split('+', 2);
        string stamp = string.Join(' ', new[] { parts.Length > 1 ? parts[1] : null, date }
            .Where(s => !string.IsNullOrEmpty(s)));
        return stamp.Length == 0 ? $"rbmanager {parts[0]}" : $"rbmanager {parts[0]} ({stamp})";
    }

    private static IEnumerable<string> InstalledRubies() =>
        Directory.Exists(Rubies) ? Directory.EnumerateDirectories(Rubies).Order() : [];

    internal static string Resolve(string query)
    {
        string[] matches = InstalledRubies()
            .Select(Path.GetFileName)
            .Where(n => n == query || n!.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray()!;
        return matches switch
        {
            [var single] => single!,
            [] => throw new InvalidOperationException($"no installed ruby matches '{query}'"),
            _ => HighestRevision(matches!) ?? throw new InvalidOperationException(
                $"'{query}' is ambiguous: {string.Join(", ", matches)}"),
        };
    }

    // ruby-<version>[-<n>]-<platform>: a reissued release package
    // (SIGNING.md in ruby/actions) differs from the original only by the
    // numeric revision after the version. The revision never collides
    // with a prerelease segment, which starts with a letter (rc1,
    // preview1), and dev snapshot names never parse here because their
    // date/commit segments sit between the version and the platform.
    private static readonly Regex PackageName = new(
        @"^ruby-(?<ver>\d+\.\d+\.\d+(?:-[a-z][a-z0-9]*)?)(?:-(?<rev>\d+))?-(?<plat>(?:x64|x86|arm64)-.+)$",
        RegexOptions.IgnoreCase);

    // When every match is the same ruby version on the same platform and
    // they differ only in revision, the newest reissue wins; the
    // superseded packages stay reachable by their full names. Anything
    // else (different versions, dev snapshots, foreign names) stays
    // ambiguous.
    private static string? HighestRevision(string[] matches)
    {
        Match[] parsed = matches.Select(n => PackageName.Match(n)).ToArray();
        if (parsed.Any(m => !m.Success)) return null;
        bool sameRuby = parsed
            .DistinctBy(m => $"{m.Groups["ver"].Value}|{m.Groups["plat"].Value}",
                StringComparer.OrdinalIgnoreCase)
            .Count() == 1;
        if (!sameRuby) return null;
        return parsed
            .MaxBy(m => m.Groups["rev"].Success ? int.Parse(m.Groups["rev"].Value) : 0)!
            .Value;
    }

    internal static string? CurrentTarget()
    {
        var info = new DirectoryInfo(Current);
        if (!info.Exists || info.LinkTarget is null) return null;
        return Path.GetFileName(info.LinkTarget.TrimEnd('\\'));
    }

    private static void SwitchTo(string name)
    {
        if (Directory.Exists(Current)) Directory.Delete(Current);
        Junction.Create(Current, Path.Combine(Rubies, name));
        UserPath.Ensure(Path.Combine(Current, "bin"));
    }

    // The binary-package zip carries exactly one root directory named after
    // the runtime (ruby-X.Y.Z-<arch>-mswinNN_MMM); that name becomes the
    // installation directory name.
    internal static string SingleRootDirectory(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var roots = archive.Entries
            .Select(e => e.FullName.Split('/', 2)[0])
            .Distinct()
            .ToArray();
        return roots switch
        {
            [var single] when single.StartsWith("ruby-", StringComparison.Ordinal) => single,
            [var single] => throw new InvalidOperationException(
                $"unexpected root directory '{single}' (want ruby-*)"),
            _ => throw new InvalidOperationException(
                "the zip must contain a single root directory"),
        };
    }

    private static void InjectTrustHook(string rubyRoot)
    {
        string dest = Path.Combine(rubyRoot, "lib", "ruby", "site_ruby",
            "rubygems", "defaults", "operating_system.rb");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var hook = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("operating_system.rb")!;
        using var file = File.Create(dest);
        hook.CopyTo(file);
    }
}
