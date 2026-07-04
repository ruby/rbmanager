using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.1: Program.SingleRootDirectory — pure zip inspection, no filesystem
// state beyond the fixture zip. Cases 4 and 6 pin known limitations (see
// docs/test-plan.md section 6.4): a root-level file and backslash-separated
// entry names are both treated as extra roots.
[Trait("Category", "Unit")]
public class SingleRootDirectoryTests
{
    private const string Root = "ruby-4.0.5-x64-mswin64_140";

    [Fact] // case 1
    public void SingleRubyRoot_ReturnsRootName()
    {
        using var tmp = new TempDir();
        string zip = Zips.Write(tmp.At("pkg.zip"),
            $"{Root}/", $"{Root}/bin/ruby.exe", $"{Root}/lib/ruby/rbconfig.rb");

        Assert.Equal(Root, Program.SingleRootDirectory(zip));
    }

    [Fact] // case 2
    public void NonRubyRoot_Throws()
    {
        using var tmp = new TempDir();
        string zip = Zips.Write(tmp.At("pkg.zip"), "python-3.12/", "python-3.12/bin/python.exe");

        var ex = Assert.Throws<InvalidOperationException>(() => Program.SingleRootDirectory(zip));
        Assert.Equal("unexpected root directory 'python-3.12' (want ruby-*)", ex.Message);
    }

    [Fact] // case 3
    public void TwoRoots_Throws()
    {
        using var tmp = new TempDir();
        string zip = Zips.Write(tmp.At("pkg.zip"),
            "ruby-4.0.5-x64-mswin64_140/bin/ruby.exe",
            "ruby-4.1.0-x64-mswin64_140/bin/ruby.exe");

        var ex = Assert.Throws<InvalidOperationException>(() => Program.SingleRootDirectory(zip));
        Assert.Contains("must contain a single root directory", ex.Message);
    }

    [Fact] // case 4 (pin 6.4): a root-level file counts as a second root
    public void RootLevelFile_CountsAsSecondRoot_Throws()
    {
        using var tmp = new TempDir();
        string zip = Zips.Write(tmp.At("pkg.zip"), $"{Root}/bin/ruby.exe", "README.txt");

        var ex = Assert.Throws<InvalidOperationException>(() => Program.SingleRootDirectory(zip));
        Assert.Contains("must contain a single root directory", ex.Message);
    }

    [Fact] // case 5
    public void EmptyZip_Throws()
    {
        using var tmp = new TempDir();
        string zip = Zips.Write(tmp.At("pkg.zip"));

        var ex = Assert.Throws<InvalidOperationException>(() => Program.SingleRootDirectory(zip));
        Assert.Contains("must contain a single root directory", ex.Message);
    }

    [Fact] // case 6 (pin 6.4): backslash separators are not recognized, so a
           // single-root tree written with '\' looks like many roots.
    public void BackslashSeparators_NotTreatedAsSeparators_Throws()
    {
        using var tmp = new TempDir();
        string zip = Zips.Write(tmp.At("pkg.zip"),
            @"ruby-4.0.5-x64-mswin64_140\bin\a.txt",
            @"ruby-4.0.5-x64-mswin64_140\bin\b.txt");

        var ex = Assert.Throws<InvalidOperationException>(() => Program.SingleRootDirectory(zip));
        Assert.Contains("must contain a single root directory", ex.Message);
    }
}
