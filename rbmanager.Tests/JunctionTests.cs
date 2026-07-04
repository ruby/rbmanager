using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.6: real NTFS junctions on disk. No env/console/registry state, so
// these run in parallel. Junctions are mount points and need no privilege.
[Trait("Category", "Integration")]
public class JunctionTests
{
    [Fact] // case 39
    public void CreatesJunctionToExistingDir_VisibleThrough()
    {
        using var tmp = new TempDir();
        string target = tmp.At("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "hello");
        string junction = tmp.At("jct");

        Junction.Create(junction, target);

        Assert.True(Directory.Exists(junction));
        Assert.Equal(target, new DirectoryInfo(junction).LinkTarget);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(junction, "marker.txt")));
    }

    [Fact] // case 40: target is normalized through Path.GetFullPath
    public void NonNormalizedTarget_IsNormalizedInLinkTarget()
    {
        using var tmp = new TempDir();
        string target = tmp.At("target");
        Directory.CreateDirectory(target);
        string denormalized = Path.Combine(tmp.Path, "sub", "..", "target");
        string junction = tmp.At("jct");

        Junction.Create(junction, denormalized);

        Assert.Equal(target, new DirectoryInfo(junction).LinkTarget);
    }

    [Fact] // case 41
    public void TrailingBackslashTarget_Trimmed()
    {
        using var tmp = new TempDir();
        string target = tmp.At("target");
        Directory.CreateDirectory(target);
        string junction = tmp.At("jct");

        Junction.Create(junction, target + "\\");

        Assert.Equal(target, new DirectoryInfo(junction).LinkTarget);
    }

    [Fact] // case 42
    public void MissingTarget_Throws()
    {
        using var tmp = new TempDir();
        string missing = tmp.At("nope");
        string junction = tmp.At("jct");

        var ex = Assert.Throws<DirectoryNotFoundException>(() => Junction.Create(junction, missing));
        Assert.Contains(missing, ex.Message);
    }

    [Fact] // case 43
    public void SpacesAndNonAsciiTarget_RoundTrips()
    {
        using var tmp = new TempDir();
        string target = tmp.At("ターゲット dir");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "f.txt"), "x");
        string junction = tmp.At("接合 point");

        Junction.Create(junction, target);

        Assert.Equal(target, new DirectoryInfo(junction).LinkTarget);
        Assert.True(File.Exists(Path.Combine(junction, "f.txt")));
    }

    [Fact] // case 44
    public void PreexistingEmptyJunctionDir_Succeeds()
    {
        using var tmp = new TempDir();
        string target = tmp.At("target");
        Directory.CreateDirectory(target);
        string junction = tmp.At("jct");
        Directory.CreateDirectory(junction); // already there, empty

        Junction.Create(junction, target);

        Assert.Equal(target, new DirectoryInfo(junction).LinkTarget);
    }

    [Fact] // case 45
    public void PreexistingNonEmptyJunctionDir_Throws()
    {
        using var tmp = new TempDir();
        string target = tmp.At("target");
        Directory.CreateDirectory(target);
        string junction = tmp.At("jct");
        Directory.CreateDirectory(junction);
        File.WriteAllText(Path.Combine(junction, "occupied.txt"), "x");

        var ex = Assert.Throws<IOException>(() => Junction.Create(junction, target));
        Assert.Contains("cannot create junction", ex.Message);
    }

    [Fact] // case 46: the SwitchTo recreate flow leaves the old target intact
    public void RecreateToNewTarget_OldTargetContentsIntact()
    {
        using var tmp = new TempDir();
        string a = tmp.At("a");
        string b = tmp.At("b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "a.txt"), "aaa");
        string junction = tmp.At("current");

        Junction.Create(junction, a);
        Directory.Delete(junction); // removes the reparse point, not a's contents
        Junction.Create(junction, b);

        Assert.Equal(b, new DirectoryInfo(junction).LinkTarget);
        Assert.True(File.Exists(Path.Combine(a, "a.txt")));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(a, "a.txt")));
    }
}
