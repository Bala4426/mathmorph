using System.Buffers.Binary;
using System.Text;

namespace MathMorph.Engine;

/// <summary>
/// Minimal read-only reader for OLE compound files (MS-CFB), enough to pull one named stream
/// (e.g. MathType's "Equation Native") out of an embedded oleObject*.bin. Input is untrusted:
/// every sector index and chain is bounds-checked and loop-checked.
/// </summary>
public static class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const int MaxStreamBytes = 16 << 20;
    private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    public static bool IsCompoundFile(byte[] d) => d.Length >= 512 && d.AsSpan(0, 8).SequenceEqual(Signature);

    /// <summary>Returns the bytes of the first stream named <paramref name="streamName"/> (case-insensitive), or null.</summary>
    public static byte[]? ReadStream(byte[] d, string streamName)
    {
        if (!IsCompoundFile(d)) throw new InvalidDataException("not an OLE compound file");
        int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x1E));
        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x20));
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("unsupported compound file sector size");
        int sectorSize = 1 << sectorShift;
        int sectorCount = Math.Max(0, (d.Length - sectorSize) / sectorSize + 1);
        uint numFat = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x2C));
        uint firstDir = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x30));
        uint cutoff = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x38));
        uint firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x3C));
        uint firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x44));
        if (numFat > (uint)sectorCount) throw new InvalidDataException("bad FAT sector count");

        ReadOnlySpan<byte> Sector(uint id)
        {
            long off = ((long)id + 1) << sectorShift;
            if (id >= 0xFFFFFFFA || off + sectorSize > d.Length) throw new InvalidDataException("sector out of range");
            return d.AsSpan((int)off, sectorSize);
        }

        // FAT sector list from the header DIFAT (109 entries) and the DIFAT chain
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < numFat; i++)
            fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x4C + i * 4)));
        uint difat = firstDifat;
        int guard = 0;
        while (fatSectors.Count < numFat && difat != EndOfChain && difat != FreeSect)
        {
            if (++guard > sectorCount) throw new InvalidDataException("DIFAT loop");
            var s = Sector(difat);
            int per = sectorSize / 4 - 1;
            for (int i = 0; i < per && fatSectors.Count < numFat; i++) fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]));
            difat = BinaryPrimitives.ReadUInt32LittleEndian(s[(per * 4)..]);
        }
        var fat = new uint[fatSectors.Count * (sectorSize / 4)];
        for (int f = 0; f < fatSectors.Count; f++)
        {
            var s = Sector(fatSectors[f]);
            for (int i = 0; i < sectorSize / 4; i++) fat[f * (sectorSize / 4) + i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]);
        }

        byte[] Chain(uint start, long size, bool sizeKnown)
        {
            var ms = new MemoryStream();
            uint cur = start;
            int steps = 0;
            while (cur != EndOfChain && cur != FreeSect)
            {
                if (++steps > sectorCount + 1 || cur >= fat.Length) throw new InvalidDataException("bad sector chain");
                ms.Write(Sector(cur));
                if (ms.Length > MaxStreamBytes) throw new InvalidDataException("stream too large");
                cur = fat[cur];
            }
            var buf = ms.ToArray();
            if (sizeKnown)
            {
                if (size > buf.Length) throw new InvalidDataException("stream shorter than its size");
                Array.Resize(ref buf, (int)size);
            }
            return buf;
        }

        var dir = Chain(firstDir, 0, false);
        if (dir.Length < 128) throw new InvalidDataException("empty directory");
        int entries = dir.Length / 128;
        uint rootStart = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(0x74));
        long rootSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(0x78)) & 0xFFFFFFFF;

        for (int e = 1; e < entries; e++)
        {
            var de = dir.AsSpan(e * 128, 128);
            if (de[0x42] != 2) continue; // stream objects only
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(de[0x40..]);
            if (nameLen < 2 || nameLen > 64) continue;
            var name = Encoding.Unicode.GetString(de[..(nameLen - 2)]);
            if (!string.Equals(name, streamName, StringComparison.OrdinalIgnoreCase)) continue;
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(de[0x74..]);
            long size = (long)(BinaryPrimitives.ReadUInt64LittleEndian(de[0x78..]) & 0xFFFFFFFF);
            if (size > MaxStreamBytes) throw new InvalidDataException("stream too large");
            if (size >= cutoff) return Chain(start, size, true);

            // small stream: lives in the mini stream (64-byte sectors, own FAT)
            var mini = Chain(rootStart, rootSize, true);
            var miniFatBytes = firstMiniFat is EndOfChain or FreeSect ? Array.Empty<byte>() : Chain(firstMiniFat, 0, false);
            var miniFat = new uint[miniFatBytes.Length / 4];
            for (int i = 0; i < miniFat.Length; i++) miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));
            var outp = new MemoryStream();
            uint cur = start;
            int steps = 0;
            while (cur != EndOfChain && cur != FreeSect && outp.Length < size)
            {
                long off = (long)cur << 6;
                if (++steps > miniFat.Length + 1 || cur >= miniFat.Length || off + 64 > mini.Length) throw new InvalidDataException("bad mini sector chain");
                outp.Write(mini, (int)off, 64);
                cur = miniFat[cur];
            }
            if (outp.Length < size) throw new InvalidDataException("mini stream shorter than its size");
            var res = outp.ToArray();
            Array.Resize(ref res, (int)size);
            return res;
        }
        return null;
    }
}
