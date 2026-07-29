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
}
