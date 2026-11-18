using System.IO.Compression;
using MathMorph.Contracts;
using MathMorph.Engine;

namespace MathMorph.Tests;

public static class SafePackageTests
{
    private static (string, byte[]) E(string name, string content) => (name, Docx.U8(content));

    [Test]
    public static void Open_ReadsAllEntriesInOrder()
    {
        var zip = Docx.Zip(new[] { E("a.xml", "<a/>"), E("dir/b.xml", "<b/>"), E("c.bin", "ccc") });
        var pkg = Docx.Open(zip);
        Assert.SeqEqual(new[] { "a.xml", "dir/b.xml", "c.bin" }, pkg.Entries.Select(e => e.Name));
        Assert.Equal("<b/>", System.Text.Encoding.UTF8.GetString(pkg.Get("dir/b.xml")!.Data));
        Assert.NotNull(pkg.Get("/dir/b.xml"), "leading slash lookup");
        Assert.Null(pkg.Get("missing.xml"));
    }

    [Test]
    public static void Open_RejectsTooManyEntries()
    {
        var zip = Docx.Zip(new[] { E("a", "1"), E("b", "2"), E("c", "3") });
        var ex = Assert.Throws<PackageLimitException>(() => Docx.Open(zip, new SafetyLimits { MaxEntries = 2 }));
        Assert.Contains("entry count", ex.Message);
        Docx.Open(zip, new SafetyLimits { MaxEntries = 3 }); // exactly at the limit is fine
    }

    [Test]
    public static void Open_RejectsEntryOverMaxEntryUncompressed()
    {
        var zip = Docx.Zip(new[] { ("big.bin", new byte[5000]) });
        Assert.Throws<PackageLimitException>(() => Docx.Open(zip, new SafetyLimits { MaxEntryUncompressed = 4000 }));
        Docx.Open(zip, new SafetyLimits { MaxEntryUncompressed = 5000 });
    }

    [Test]
    public static void Open_RejectsTotalOverMaxTotalUncompressed()
    {
        // every entry is under the per-entry limit, the sum is not: the streaming cap must stop the third entry
        var zip = Docx.Zip(new[] { ("a.bin", new byte[600]), ("b.bin", new byte[600]), ("c.bin", new byte[600]) });
        var lim = new SafetyLimits { MaxEntryUncompressed = 1000, MaxTotalUncompressed = 1500 };
        var ex = Assert.Throws<PackageLimitException>(() => Docx.Open(zip, lim));
        Assert.Contains("c.bin", ex.Message);
        Assert.Contains("inflates beyond", ex.Message);
        Docx.Open(zip, new SafetyLimits { MaxEntryUncompressed = 1000, MaxTotalUncompressed = 1800 });
    }

    [Test]
    public static void Open_ForgedSmallHeaderSize_NeverYieldsMoreThanLimit()
    {
        // header claims 100 bytes; the deflate stream really holds 200 000 bytes
        var zip = Docx.Zip(new[] { ("bomb.bin", new byte[200_000]) });
        ForgeUncompressedSize(zip, 100);
        var lim = new SafetyLimits { MaxEntryUncompressed = 10_000, MaxTotalUncompressed = 10_000 };
        try
        {
            var pkg = Docx.Open(zip, lim);
            Assert.True(pkg.Entries.All(e => e.Data.Length <= lim.MaxEntryUncompressed), "inflated data stays within the limit");
        }
        catch (PackageLimitException) { /* rejected: also fine */ }
    }

    [Test]
    public static void Open_RejectsHighCompressionRatio()
    {
        var zip = Docx.Zip(new[] { ("zeros.bin", new byte[(1 << 20) + 4096]) });
        var ex = Assert.Throws<PackageLimitException>(() => Docx.Open(zip, new SafetyLimits { MaxCompressionRatio = 10 }));
        Assert.Contains("compression ratio", ex.Message);
        // the ratio rule only applies to entries over 1 MiB
        var small = Docx.Zip(new[] { ("zeros.bin", new byte[100_000]) });
        Docx.Open(small, new SafetyLimits { MaxCompressionRatio = 10 });
    }

    [Test]
    public static void Open_RejectsUnsafeNames()
    {
        foreach (var bad in new[] { "../x", "/abs", "word/../../x.xml", "a\\b.xml" })
        {
            var zip = Docx.Zip(new[] { E("ok.xml", "<a/>"), E(bad, "<a/>") });
            var ex = Assert.Throws<PackageLimitException>(() => Docx.Open(zip), "name " + bad);
            Assert.Contains("unsafe entry name", ex.Message, "name " + bad);
        }
    }

    [Test]
    public static void Open_RejectsNamesDifferingOnlyByCase()
    {
        // lookups are case-insensitive, so two such entries would be ambiguous
        var zip = Docx.Zip(new[] { E("word/document.xml", "<a/>"), E("Word/Document.xml", "<b/>") });
        var ex = Assert.Throws<PackageLimitException>(() => Docx.Open(zip));
        Assert.Contains("duplicate", ex.Message);
    }

    [Test]
    public static void Open_RejectsNonZip()
    {
        Assert.Throws<PackageLimitException>(() => SafePackage.Open(new MemoryStream(Docx.U8("this is not a zip file at all, just text")), new SafetyLimits()));
    }

    [Test]
    public static void Open_File_RejectsOverMaxInputFileBytes()
    {
        var dir = TestEnv.TempDir("safepackage_size");
        var path = Path.Combine(dir, "x.docx");
        File.WriteAllBytes(path, Docx.Zip(new[] { ("a.bin", new byte[100]) }, CompressionLevel.NoCompression));
        Assert.Throws<PackageLimitException>(() => SafePackage.Open(path, new SafetyLimits { MaxInputFileBytes = 50 }));
        SafePackage.Open(path, new SafetyLimits());
    }

    [Test]
    public static void Save_KeepsOrderAndBytes_ReplacesOnlyGivenEntries()
    {
        var media = new byte[3000];
        new Random(7).NextBytes(media);
        var entries = new List<(string, byte[])>
        {
            E("[Content_Types].xml", "<Types/>"),
            E("_rels/.rels", "<Relationships/>"),
            E("word/document.xml", "<w:document>old</w:document>"),
            ("word/media/image1.png", media),
            E("docProps/core.xml", "<core>ä€</core>"),
            E("customXml/item1.xml", "<x/>"),
        };
        var pkg = Docx.Open(Docx.Zip(entries));
        var dir = TestEnv.TempDir("safepackage_save");
        var outPath = Path.Combine(dir, "out.docx");
        var newDoc = Docx.U8("<w:document>new</w:document>");
        pkg.Save(outPath, new Dictionary<string, byte[]> { ["word/document.xml"] = newDoc });

        Assert.False(File.Exists(outPath + ".tmp"), "temp file removed");
        var again = SafePackage.Open(outPath, new SafetyLimits());
        Assert.SeqEqual(entries.Select(e => e.Item1), again.Entries.Select(e => e.Name), "entry order");
        foreach (var (name, data) in entries)
        {
            var got = again.Get(name)!.Data;
            if (name == "word/document.xml") Assert.BytesEqual(newDoc, got, name);
            else Assert.BytesEqual(data, got, name);
        }
        // the in-memory package is not modified by Save
        Assert.Equal("<w:document>old</w:document>", System.Text.Encoding.UTF8.GetString(pkg.Get("word/document.xml")!.Data));

        // saving over an existing file replaces it
        pkg.Save(outPath, new Dictionary<string, byte[]>());
        Assert.Equal("<w:document>old</w:document>",
            System.Text.Encoding.UTF8.GetString(SafePackage.Open(outPath, new SafetyLimits()).Get("word/document.xml")!.Data));
    }

    /// <summary>Overwrites the uncompressed-size field of every central and local header.</summary>
    internal static void ForgeUncompressedSize(byte[] zip, uint size)
    {
        int eocd = -1;
        for (int i = zip.Length - 22; i >= 0; i--)
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 5 && zip[i + 3] == 6) { eocd = i; break; }
        if (eocd < 0) throw new InvalidOperationException("no EOCD");
        int count = BitConverter.ToUInt16(zip, eocd + 10);
        int cd = (int)BitConverter.ToUInt32(zip, eocd + 16);
        for (int k = 0; k < count; k++)
        {
            if (BitConverter.ToUInt32(zip, cd) != 0x02014b50) throw new InvalidOperationException("bad central header");
            BitConverter.GetBytes(size).CopyTo(zip, cd + 24);
            int local = (int)BitConverter.ToUInt32(zip, cd + 42);
            BitConverter.GetBytes(size).CopyTo(zip, local + 22);
            int n = BitConverter.ToUInt16(zip, cd + 28), x = BitConverter.ToUInt16(zip, cd + 30), c = BitConverter.ToUInt16(zip, cd + 32);
            cd += 46 + n + x + c;
        }
    }
}
