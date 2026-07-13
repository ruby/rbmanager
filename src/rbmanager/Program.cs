using System.IO.Compression;
using System.Reflection;

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
                ["use", var name] => Use(name),
                ["uninstall", var name] => Uninstall(name),
                ["msvc", "enable"] => Msvc.Enable(null),
                ["msvc", "enable", var shell] => Msvc.Enable(shell),
                ["msvc", "exec", .. var command] when command.Length > 0 => Msvc.Exec(command),
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
              install <zip|url>      install a ruby binary package from a zip file or URL
              list                   list installed rubies
              use <version>          switch the active ruby
              uninstall <version>    remove an installed ruby
              msvc enable [shell]    print the MSVC build env to eval (cmd|powershell)
              msvc exec <command...> run a command with the MSVC build env applied
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
        string zip = source;
        string? downloaded = null;
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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
            _ => throw new InvalidOperationException(
                $"'{query}' is ambiguous: {string.Join(", ", matches)}"),
        };
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
