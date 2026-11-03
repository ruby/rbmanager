using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.2: Program.Resolve over a seeded rubies directory. Serial because it
// redirects the install root via RBMANAGER_ROOT. Case 12 pins 6.1: an exact
// match has no precedence over a substring match.
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class ResolveTests
{
    private const string V405 = "ruby-4.0.5-x64-mswin64_140";
    private const string V410 = "ruby-4.1.0-x64-mswin64_140";

    [Fact] // case 7
    public void ExactFullName()
    {
        using var sb = new RbSandbox();
        sb.Seed(V405, V410);
        Assert.Equal(V405, Program.Resolve(V405));
    }

    [Fact] // case 8
    public void UniqueSubstring()
    {
        using var sb = new RbSandbox();
        sb.Seed(V405, V410);
        Assert.Equal(V405, Program.Resolve("4.0.5"));
    }

    [Fact] // case 9
    public void CaseInsensitiveSubstring()
    {
        using var sb = new RbSandbox();
        sb.Seed(V405, V410);
        Assert.Equal(V405, Program.Resolve("RUBY-4.0"));
    }

    [Fact] // case 10
    public void NoMatch_Throws()
    {
        using var sb = new RbSandbox();
        sb.Seed(V405, V410);
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve("9.9"));
        Assert.Equal("no installed ruby matches '9.9'", ex.Message);
    }

    [Fact] // case 11: ambiguous match lists both in sorted order
    public void AmbiguousSubstring_ListsSortedMatches()
    {
        using var sb = new RbSandbox();
        sb.Seed(V410, V405); // seeded out of order; enumeration sorts
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve("4."));
        Assert.Equal($"'4.' is ambiguous: {V405}, {V410}", ex.Message);
    }

    [Fact] // case 12 (pin 6.1): exact match still ambiguous vs a superstring
    public void ExactNameThatIsSubstringOfAnother_IsAmbiguous()
    {
        const string a = "ruby-3.4.0-x64-mswin64_140";
        const string b = "ruby-3.4.0-x64-mswin64_140-rc1"; // contains a
        using var sb = new RbSandbox();
        sb.Seed(a, b);
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve(a));
        Assert.Contains("is ambiguous", ex.Message);
        Assert.Contains(a, ex.Message);
        Assert.Contains(b, ex.Message);
    }

    [Fact] // case 13: no rubies directory behaves as empty
    public void NoRubiesDirectory_NoMatch()
    {
        using var sb = new RbSandbox(); // nothing seeded, rubies dir absent
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve("anything"));
        Assert.Equal("no installed ruby matches 'anything'", ex.Message);
    }

    // Reissued packages (ruby/actions SIGNING.md): same version and
    // platform differing only in the trailing numeric revision resolve
    // to the newest reissue instead of erroring as ambiguous.

    private const string R345 = "ruby-3.4.5-x64-mswin64_140";
    private const string R345r1 = "ruby-3.4.5-1-x64-mswin64_140";
    private const string R345r2 = "ruby-3.4.5-2-x64-mswin64_140";

    [Fact]
    public void RevisionsOfSameVersion_PickHighest()
    {
        using var sb = new RbSandbox();
        sb.Seed(R345, R345r2, R345r1);
        Assert.Equal(R345r2, Program.Resolve("3.4.5"));
    }

    [Fact] // revisions compare numerically, not lexicographically
    public void RevisionsCompareNumerically()
    {
        using var sb = new RbSandbox();
        sb.Seed(R345r2, "ruby-3.4.5-10-x64-mswin64_140");
        Assert.Equal("ruby-3.4.5-10-x64-mswin64_140", Program.Resolve("3.4.5"));
    }

    [Fact] // the superseded original stays reachable by its full name
    public void SupersededOriginal_FullNameStillResolves()
    {
        using var sb = new RbSandbox();
        sb.Seed(R345, R345r1);
        Assert.Equal(R345, Program.Resolve(R345));
    }

    [Fact] // revision preference never crosses version boundaries
    public void RevisionPreference_DifferentVersionsStayAmbiguous()
    {
        using var sb = new RbSandbox();
        sb.Seed(R345, R345r1, "ruby-3.4.51-x64-mswin64_140"); // also contains "3.4.5"
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve("3.4.5"));
        Assert.Contains("is ambiguous", ex.Message);
    }

    [Fact] // a prerelease is a different ruby, not a revision of the release
    public void RevisionPreference_PrereleaseStaysAmbiguous()
    {
        using var sb = new RbSandbox();
        sb.Seed("ruby-3.4.0-x64-mswin64_140", "ruby-3.4.0-rc1-x64-mswin64_140");
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve("3.4.0"));
        Assert.Contains("is ambiguous", ex.Message);
    }

    // Plan 4.15: preferNative, which `use` passes, on an ARM64 host.

    private const string A3412 = "ruby-3.4.12-arm64-mswin64_140";
    private const string X3412 = "ruby-3.4.12-x64-mswin64_140";
    private const string X3413 = "ruby-3.4.13-x64-mswin64_140";
    private const string X3311 = "ruby-3.3.11-x64-mswin64_140";

    private static EnvScope Arm64Host()
    {
        var env = new EnvScope();
        env.Set("RBMANAGER_OS_ARCH", "Arm64");
        return env;
    }

    [Fact] // case 134: an arm64 match hides the x64 ones, even a newer version
    public void PreferNative_Arm64Host_Arm64HidesX64()
    {
        using var sb = new RbSandbox();
        using var env = Arm64Host();
        sb.Seed(A3412, X3412, X3413);
        Assert.Equal(A3412, Program.Resolve("3.4.12", preferNative: true));
        Assert.Equal(A3412, Program.Resolve("3.4", preferNative: true));
    }

    [Fact] // case 135: without an arm64 match the x64 ones resolve as before
    public void PreferNative_Arm64Host_FallsBackToX64()
    {
        using var sb = new RbSandbox();
        using var env = Arm64Host();
        sb.Seed(A3412, X3311);
        Assert.Equal(X3311, Program.Resolve("3.3", preferNative: true));
    }

    [Fact] // case 136: a full x64 name is still that build
    public void PreferNative_Arm64Host_FullX64Name()
    {
        using var sb = new RbSandbox();
        using var env = Arm64Host();
        sb.Seed(A3412, X3412);
        Assert.Equal(X3412, Program.Resolve(X3412, preferNative: true));
    }

    [Fact] // case 137: the preference never decides between arm64 builds
    public void PreferNative_Arm64Host_Arm64VersionsStayAmbiguous()
    {
        using var sb = new RbSandbox();
        using var env = Arm64Host();
        sb.Seed(A3412, "ruby-3.4.11-arm64-mswin64_140", X3413);
        var ex = Assert.Throws<InvalidOperationException>(
            () => Program.Resolve("3.4.1", preferNative: true));
        Assert.Equal(
            $"'3.4.1' is ambiguous: ruby-3.4.11-arm64-mswin64_140, {A3412}", ex.Message);
    }

    [Fact] // case 147: across platforms the exact version beats the platform order
    public void PreferNative_Arm64Host_ExactVersionBeatsPlatform()
    {
        const string x341 = "ruby-3.4.1-x64-mswin64_140";
        using var sb = new RbSandbox();
        using var env = Arm64Host();
        sb.Seed(A3412, x341, X3412);
        Assert.Equal(x341, Program.Resolve("3.4.1", preferNative: true));
        Assert.Equal(A3412, Program.Resolve("3.4.12", preferNative: true));
    }

    [Fact] // case 148: within one platform an exact version is still ambiguous
    public void PreferNative_SinglePlatform_ExactVersionStaysAmbiguous()
    {
        const string x341 = "ruby-3.4.1-x64-mswin64_140";
        using var sb = new RbSandbox();
        sb.Seed(x341, X3412);
        Assert.Throws<InvalidOperationException>(
            () => Program.Resolve("3.4.1", preferNative: true));

        using var env = Arm64Host();
        var ex = Assert.Throws<InvalidOperationException>(
            () => Program.Resolve("3.4.1", preferNative: true));
        Assert.Equal($"'3.4.1' is ambiguous: {x341}, {X3412}", ex.Message);
    }

    [Fact] // case 138: x64 hosts, and uninstall's resolution, are unchanged
    public void PreferNative_X64HostOrNotPreferred_StaysAmbiguous()
    {
        using var sb = new RbSandbox();
        sb.Seed(A3412, X3412);
        Assert.Throws<InvalidOperationException>(
            () => Program.Resolve("3.4.12", preferNative: true));

        using var env = Arm64Host();
        var ex = Assert.Throws<InvalidOperationException>(() => Program.Resolve("3.4.12"));
        Assert.Equal($"'3.4.12' is ambiguous: {A3412}, {X3412}", ex.Message);
    }
}
