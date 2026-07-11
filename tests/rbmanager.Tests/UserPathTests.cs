using Microsoft.Win32;
using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.7: UserPath.Ensure against a scratch registry key with broadcast
// suppressed. Exercises value-kind preservation, unexpanded %VAR% retention,
// idempotency, and segment trimming without touching the real user PATH.
[Trait("Category", "Integration")]
public class UserPathTests
{
    private const string Entry = @"C:\Users\me\AppData\Local\Ruby\bin";

    private static string? RawPath(RegistryKey key) =>
        key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    [Fact] // case 48
    public void NoPathValue_CreatesEntryAsExpandString()
    {
        using var reg = new ScratchRegistryKey();

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal(Entry, RawPath(reg.Key));
        Assert.Equal(RegistryValueKind.ExpandString, reg.Key.GetValueKind("Path"));
    }

    [Fact] // case 49
    public void ExistingRegSz_AppendsAndKeepsKind()
    {
        using var reg = new ScratchRegistryKey();
        reg.Key.SetValue("Path", @"C:\a", RegistryValueKind.String);

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal($@"C:\a;{Entry}", RawPath(reg.Key));
        Assert.Equal(RegistryValueKind.String, reg.Key.GetValueKind("Path"));
    }

    [Fact] // case 50
    public void ExistingExpandSz_KeepsKindAndUnexpandedVars()
    {
        using var reg = new ScratchRegistryKey();
        reg.Key.SetValue("Path", @"%SystemRoot%\tools", RegistryValueKind.ExpandString);

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal($@"%SystemRoot%\tools;{Entry}", RawPath(reg.Key));
        Assert.Equal(RegistryValueKind.ExpandString, reg.Key.GetValueKind("Path"));
    }

    [Fact] // case 51
    public void EntryAlreadyPresentExact_Unchanged()
    {
        using var reg = new ScratchRegistryKey();
        string existing = $@"C:\a;{Entry}";
        reg.Key.SetValue("Path", existing, RegistryValueKind.ExpandString);

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal(existing, RawPath(reg.Key));
    }

    [Fact] // case 52
    public void EntryPresentDifferentCase_Unchanged()
    {
        using var reg = new ScratchRegistryKey();
        string existing = $@"C:\a;{Entry.ToUpperInvariant()}";
        reg.Key.SetValue("Path", existing, RegistryValueKind.ExpandString);

        UserPath.Ensure(Entry.ToLowerInvariant(), reg.Key, broadcast: false);

        Assert.Equal(existing, RawPath(reg.Key));
    }

    [Fact] // case 53
    public void EntryPresentWithWhitespaceAndEmptySegments_Unchanged()
    {
        using var reg = new ScratchRegistryKey();
        string existing = $@";; {Entry} ;";
        reg.Key.SetValue("Path", existing, RegistryValueKind.ExpandString);

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal(existing, RawPath(reg.Key));
    }

    [Fact] // case 54
    public void TrailingSemicolons_TrimmedBeforeAppend()
    {
        using var reg = new ScratchRegistryKey();
        reg.Key.SetValue("Path", @"C:\a;;", RegistryValueKind.ExpandString);

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal($@"C:\a;{Entry}", RawPath(reg.Key));
    }

    [Fact] // case 55
    public void EmptyExistingValue_NoLeadingSemicolon()
    {
        using var reg = new ScratchRegistryKey();
        reg.Key.SetValue("Path", "", RegistryValueKind.ExpandString);

        UserPath.Ensure(Entry, reg.Key, broadcast: false);

        Assert.Equal(Entry, RawPath(reg.Key));
    }
}
