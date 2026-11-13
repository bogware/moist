using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

namespace Moist.Core.Tools;

/// <summary>
/// Unpacks a .7z archive (MKVToolNix's Windows portable is LZMA-compressed 7z, which the
/// tar.exe bundled with Windows cannot decode). Every entry's destination is checked to stay
/// inside <c>destination</c> — an entry named <c>..\..\evil.dll</c> is refused rather than
/// trusted to the library — because the extracted binaries are executed afterwards.
/// </summary>
internal static class SevenZipExtractor
{
    public static void ExtractToDirectory(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        using var archive = SevenZipArchive.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Key)) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.Key));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"archive entry '{entry.Key}' would extract outside {root}");

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.OpenEntryStream();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }
}
