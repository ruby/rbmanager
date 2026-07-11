using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.6 case 47 + 4.3 case 30: Program.CurrentTarget parsing, including the
// dangling-junction case (pin 6.5). Serial because it redirects the root.
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class CurrentTargetTests
{
    private const string V405 = "ruby-4.0.5-x64-mswin64_140";

    [Fact] // case 47a
    public void JunctionPresent_ReturnsLeafName()
    {
        using var sb = new RbSandbox();
        sb.Seed(V405);
        sb.Activate(V405);
        Assert.Equal(V405, Program.CurrentTarget());
    }

    [Fact] // case 47b
    public void NoJunction_ReturnsNull()
    {
        using var sb = new RbSandbox();
        Assert.Null(Program.CurrentTarget());
    }

    [Fact] // case 47c
    public void PlainDirectoryAtCurrent_ReturnsNull()
    {
        using var sb = new RbSandbox();
        Directory.CreateDirectory(sb.Current); // real dir, not a reparse point
        Assert.Null(Program.CurrentTarget());
    }

    [Fact] // case 30 (pin 6.5): target deleted out of band
    public void DanglingJunction_TargetDeleted_ObservedBehavior()
    {
        using var sb = new RbSandbox();
        sb.Seed(V405);
        sb.Activate(V405);
        Directory.Delete(Path.Combine(sb.Rubies, V405), recursive: true);

        // The reparse point still carries its link target, so the name still
        // parses even though nothing is there.
        Assert.Equal(V405, Program.CurrentTarget());

        // Resolve no longer finds it (rubies entry gone), so uninstall fails.
        Assert.Throws<InvalidOperationException>(() => Program.Uninstall(V405));
    }
}
