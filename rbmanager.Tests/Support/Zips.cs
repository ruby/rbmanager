using System.IO.Compression;

namespace RbManager.Tests.Support;

// Zip fixtures are generated in code; no binary fixtures are committed.
// Entry names are stored verbatim (the caller controls separators) so the
// backslash-vs-slash behavior of the product can be exercised directly.
internal static class Zips
{
    // Writes a zip at `path` whose entries are exactly `entryNames`, each an
    // empty entry. A name ending in "/" is a directory entry.
    public static string Write(string path, params string[] entryNames)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (string name in entryNames)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream _ = entry.Open();
        }
        return path;
    }

    // A minimal but realistic mswin package: one ruby-* root with a couple of
    // nested files carrying stub bytes, enough for extract + trust-hook tests.
    public static string WriteRuby(string path, string rootName)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, $"{rootName}/bin/ruby.exe", [0x4D, 0x5A]); // "MZ"
        WriteEntry(zip, $"{rootName}/lib/ruby/site_ruby/.keep", []);
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using Stream s = entry.Open();
        s.Write(content, 0, content.Length);
    }
}
