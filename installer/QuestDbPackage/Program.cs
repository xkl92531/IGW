using System.Formats.Tar;
using System.IO.Compression;

if (args.Length != 2) throw new ArgumentException("Usage: QuestDbPackage archive.tar.gz empty-extraction-directory");
string destination = Path.GetFullPath(args[1]);
Directory.CreateDirectory(destination);
string prefix = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
string Resolve(string name)
{
    if (Path.IsPathFullyQualified(name) || name.StartsWith('/')) throw new InvalidDataException("Absolute archive path.");
    string path = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
    if (!path.StartsWith(prefix, comparison) && !path.Equals(destination, comparison)) throw new InvalidDataException("Archive path escapes extraction root.");
    return path;
}
var links = new List<(string Path, string Target)>();
await using var input = File.OpenRead(args[0]);
await using var gzip = new GZipStream(input, CompressionMode.Decompress);
using var tar = new TarReader(gzip);
while (await tar.GetNextEntryAsync() is TarEntry entry)
{
    string path = Resolve(entry.Name);
    if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
    {
        // Windows cannot reliably create Linux symlinks. Materialize their verified in-archive targets.
        string target = entry.EntryType == TarEntryType.HardLink ? entry.LinkName
            : Path.GetRelativePath(destination, Path.Combine(Path.GetDirectoryName(path)!, entry.LinkName));
        links.Add((path, Resolve(target)));
    }
    else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output);
    }
    else throw new InvalidDataException($"Unsupported archive entry: {entry.EntryType}");
}
while (links.Count > 0)
{
    int before = links.Count;
    for (int index = links.Count - 1; index >= 0; index--)
    {
        var link = links[index];
        if (!File.Exists(link.Target)) continue;
        File.Copy(link.Target, link.Path, overwrite: false); links.RemoveAt(index);
    }
    if (links.Count == before) throw new InvalidDataException("Unresolved or circular archive link.");
}
