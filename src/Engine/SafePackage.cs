using System.IO.Compression;
using MathMorph.Contracts;

namespace MathMorph.Engine;

public sealed class PackageLimitException(string message) : Exception(message);

/// <summary>One zip entry held in memory, with the metadata needed to write it back.</summary>
public sealed class PackageEntry
{
    public required string Name { get; init; }
    public required byte[] Data { get; set; }
    public DateTimeOffset LastWriteTime { get; init; }
    public long CompressedLength { get; init; }
}

/// <summary>
/// Reads an untrusted .docx (zip) fully into memory while enforcing limits on entry count,
/// total and per-entry uncompressed size and compression ratio. Sizes are enforced on the
/// bytes actually inflated, not on the (forgeable) header values.
/// </summary>
public sealed class SafePackage
{
    public List<PackageEntry> Entries { get; } = new();
    private readonly Dictionary<string, PackageEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive part lookup (OPC part names are case-insensitive). Tries the name as given,
    /// then percent-decoded, so both "image%201.png" and "image 1.png" zip item names resolve.</summary>
    public PackageEntry? Get(string name)
    {
        name = name.TrimStart('/');
        if (_byName.TryGetValue(name, out var e)) return e;
        string dec;
        try { dec = Uri.UnescapeDataString(name); } catch { return null; }
        return dec != name && _byName.TryGetValue(dec, out e) ? e : null;
    }

    public static SafePackage Open(string path, SafetyLimits limits)
    {
        var fi = new FileInfo(path);
        if (fi.Length > limits.MaxInputFileBytes)
            throw new PackageLimitException($"file size {fi.Length} exceeds limit {limits.MaxInputFileBytes}");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Open(fs, limits);
    }

    public static SafePackage Open(Stream stream, SafetyLimits limits)
    {
        var pkg = new SafePackage();
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new PackageLimitException("not a valid zip package: " + ex.Message); }
        using (zip)
        {
            if (zip.Entries.Count > limits.MaxEntries)
                throw new PackageLimitException($"entry count {zip.Entries.Count} exceeds limit {limits.MaxEntries}");
            long total = 0;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName;
                if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(s => s == ".."))
                    throw new PackageLimitException($"unsafe entry name '{name}'");
                if (pkg._byName.ContainsKey(name))
                    throw new PackageLimitException($"duplicate entry name '{name}'");
                if (e.Length > limits.MaxEntryUncompressed)
                    throw new PackageLimitException($"entry '{name}' declares {e.Length} bytes, over limit");
                long cap = Math.Min(limits.MaxEntryUncompressed, limits.MaxTotalUncompressed - total);
                byte[] data;
                using (var s = e.Open())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ms.Length + n > cap)
                            throw new PackageLimitException($"entry '{name}' inflates beyond the size limit");
                        ms.Write(buf, 0, n);
                    }
                    data = ms.ToArray();
                }
                total += data.Length;
                if (data.Length > (1 << 20) && e.CompressedLength > 0 &&
                    (double)data.Length / e.CompressedLength > limits.MaxCompressionRatio)
                    throw new PackageLimitException($"entry '{name}' compression ratio exceeds {limits.MaxCompressionRatio}");
                var pe = new PackageEntry { Name = name, Data = data, LastWriteTime = e.LastWriteTime, CompressedLength = e.CompressedLength };
                pkg.Entries.Add(pe);
                pkg._byName[name] = pe;
            }
        }
        return pkg;
    }

    /// <summary>Writes all entries in original order with original timestamps. Only entries in
    /// <paramref name="replacements"/> get new content; every other entry's bytes are copied unchanged.</summary>
    public void Save(string path, IReadOnlyDictionary<string, byte[]> replacements)
    {
        // map every replacement key to the actual entry (case-insensitive, percent-decoding); unknown keys are an error
        var byEntry = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var kv in replacements)
            byEntry[(Get(kv.Key) ?? throw new InvalidOperationException("replacement for unknown part " + kv.Key)).Name] = kv.Value;
        replacements = byEntry;
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var e in Entries)
            {
                var ze = zip.CreateEntry(e.Name, CompressionLevel.Optimal);
                ze.LastWriteTime = ClampZipTime(e.LastWriteTime);
                var bytes = replacements.TryGetValue(e.Name, out var r) ? r : e.Data;
                if (e.Name.EndsWith('/') && bytes.Length == 0) continue;
                using var s = ze.Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private static DateTimeOffset ClampZipTime(DateTimeOffset t)
    {
        var min = new DateTimeOffset(1980, 1, 1, 0, 0, 0, t.Offset);
        var max = new DateTimeOffset(2107, 12, 31, 0, 0, 0, t.Offset);
        return t < min ? min : t > max ? max : t;
    }
}
