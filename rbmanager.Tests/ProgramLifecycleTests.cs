using System.Reflection;
using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.3: install / list / use / uninstall against a redirected root with
// the PATH write routed to a scratch key. Serial (env vars + console). Cases
// 17 and 20 pin 6.2 and 6.3 respectively.
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class ProgramLifecycleTests
{
    private const string V999 = "ruby-9.9.9-x64-mswin64_140";
    private const string V405 = "ruby-4.0.5-x64-mswin64_140";
    private const string V410 = "ruby-4.1.0-x64-mswin64_140";

    private static string MakeZip(RbSandbox sb, string rootName) =>
        Zips.WriteRuby(Path.Combine(sb.Root, "_src", $"{rootName}.zip"), rootName);

    private static byte[] EmbeddedTrustHook()
    {
        using Stream res = typeof(Program).Assembly
            .GetManifestResourceStream("operating_system.rb")!;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact] // case 14
    public async Task InstallLocalZip_ExtractsSwitchesAndEnsuresPath()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        string zip = MakeZip(sb, V999);

        int rc = await Program.Install(zip);

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V999)));
        Assert.Equal(V999, Program.CurrentTarget());
        Assert.Contains(Path.Combine(sb.Current, "bin"), sb.WrittenPath());
        Assert.Contains($"Installed {V999}", cap.Out);
    }

    [Fact] // case 15
    public async Task InstallLocalZip_InjectsTrustHookByteIdentical()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        string zip = MakeZip(sb, V999);

        await Program.Install(zip);

        string hook = Path.Combine(sb.Rubies, V999,
            "lib", "ruby", "site_ruby", "rubygems", "defaults", "operating_system.rb");
        Assert.True(File.Exists(hook));
        Assert.Equal(EmbeddedTrustHook(), File.ReadAllBytes(hook));
    }

    [Fact] // case 16
    public async Task InstallTwice_ThrowsAlreadyInstalled_LeavesTreeIntact()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        string zip = MakeZip(sb, V999);
        await Program.Install(zip);
        string marker = Path.Combine(sb.Rubies, V999, "bin", "ruby.exe");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Program.Install(zip));

        Assert.Equal($"{V999} is already installed", ex.Message);
        Assert.True(File.Exists(marker)); // untouched
    }

    [Fact] // case 17 (pin 6.2): install always switches current to the new one
    public async Task InstallSecondVersion_SwitchesCurrent()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        await Program.Install(MakeZip(sb, V405));
        Assert.Equal(V405, Program.CurrentTarget());

        await Program.Install(MakeZip(sb, V410));

        Assert.Equal(V410, Program.CurrentTarget());
    }

    [Fact] // case 18
    public async Task InstallFromUrl_InstallsAndDeletesTempDownload()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        byte[] zipBytes = File.ReadAllBytes(MakeZip(sb, V999));
        using var server = new LoopbackZipServer(zipBytes, $"/{V999}.zip");
        string tempDownload = Path.Combine(Path.GetTempPath(), $"{V999}.zip");

        int rc = await Program.Install(server.ZipUrl);

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V999)));
        Assert.False(File.Exists(tempDownload)); // finally-deleted
    }

    [Fact] // case 19
    public async Task InstallFromUrl_404_Throws_NoInstall_NoLeftover()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        using var server = new LoopbackZipServer([1, 2, 3], "/exists.zip");
        string missingUrl = server.BaseUrl + "missing.zip";
        string tempDownload = Path.Combine(Path.GetTempPath(), "missing.zip");

        await Assert.ThrowsAsync<HttpRequestException>(() => Program.Install(missingUrl));

        Assert.False(Directory.Exists(sb.Rubies) &&
            Directory.EnumerateDirectories(sb.Rubies).Any());
        Assert.False(File.Exists(tempDownload));
    }

    [Fact] // case 20 (pin 6.3): a URL ending in '/' yields an empty temp name
    public async Task InstallFromUrl_EmptyFileName_Throws()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        byte[] zipBytes = File.ReadAllBytes(MakeZip(sb, V999));
        using var server = new LoopbackZipServer(zipBytes, "/");

        await Assert.ThrowsAnyAsync<Exception>(() => Program.Install(server.BaseUrl));
    }

    [Fact] // case 21
    public async Task InstallBadRoot_Throws_NothingInstalled()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        string zip = Zips.Write(Path.Combine(sb.Root, "_src", "bad.zip"),
            "python-3.12/", "python-3.12/bin/python.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Program.Install(zip));

        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, "python-3.12")));
    }

    [Fact] // case 22
    public void List_NoRubiesDir_PrintsNothing()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();

        int rc = Program.List();

        Assert.Equal(0, rc);
        Assert.Equal("", cap.Out);
    }

    [Fact] // case 23
    public void List_TwoInstalls_OneActive_SortedAndStarred()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V410, V405);
        sb.Activate(V405);

        Program.List();

        Assert.Equal([$"* {V405}", $"  {V410}"], cap.OutLines);
    }

    [Fact] // case 24
    public void List_NoCurrent_NoStar()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V405, V410);

        Program.List();

        Assert.All(cap.OutLines, line => Assert.False(line.StartsWith('*')));
    }

    [Fact] // case 25
    public void Use_UniqueQuery_RetargetsAndEnsuresPath()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V405, V410);
        sb.Activate(V405);

        int rc = Program.Use("4.1.0");

        Assert.Equal(0, rc);
        Assert.Equal(V410, Program.CurrentTarget());
        Assert.Contains($"Now using {V410}", cap.Out);
        Assert.Contains(Path.Combine(sb.Current, "bin"), sb.WrittenPath());
    }

    [Fact] // case 26
    public void Use_Ambiguous_Throws_JunctionUnchanged()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V405, V410);
        sb.Activate(V405);

        Assert.Throws<InvalidOperationException>(() => Program.Use("4."));

        Assert.Equal(V405, Program.CurrentTarget());
    }

    [Fact] // case 27
    public void Uninstall_NonActive_RemovesDir_KeepsCurrent()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V405, V410);
        sb.Activate(V405);

        int rc = Program.Uninstall("4.1.0");

        Assert.Equal(0, rc);
        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, V410)));
        Assert.Equal(V405, Program.CurrentTarget());
        Assert.Contains($"Uninstalled {V410}", cap.Out);
    }

    [Fact] // case 28
    public void Uninstall_Active_RemovesJunctionAndDir_PrintsHint()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V405);
        sb.Activate(V405);

        Program.Uninstall("4.0.5");

        Assert.False(Directory.Exists(sb.Current));
        Assert.False(Directory.Exists(Path.Combine(sb.Rubies, V405)));
        Assert.Contains("run `rb use`", cap.Out);
        Assert.Contains($"Uninstalled {V405}", cap.Out);
    }

    [Fact] // case 29
    public void Uninstall_Ambiguous_Throws_NothingDeleted()
    {
        using var sb = new RbSandbox();
        using var cap = new ConsoleCapture();
        sb.Seed(V405, V410);

        Assert.Throws<InvalidOperationException>(() => Program.Uninstall("4."));

        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V405)));
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, V410)));
    }
}
