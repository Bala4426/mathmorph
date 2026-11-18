using MathMorph.Contracts;
using MathMorph.Engine;
using static MathMorph.Tests.Docx;

namespace MathMorph.Tests;

public static class ReportsTests
{
    [Test]
    public static void Csv_QuotesAndDoublesQuotes()
    {
        Assert.Equal("\"abc\"", Reports.Csv("abc"));
        Assert.Equal("\"\"", Reports.Csv(null));
        Assert.Equal("\"\"", Reports.Csv(""));
        Assert.Equal("\"a,b\"", Reports.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Reports.Csv("say \"hi\""));
        Assert.Equal("\"line1\nline2\"", Reports.Csv("line1\nline2"));
        Assert.Equal("\"\\frac{a}{b}\"", Reports.Csv("\\frac{a}{b}"));
    }

    [Test]
    public static void Csv_PrefixesFormulaStartersWithApostrophe()
    {
        Assert.Equal("\"'=1+1\"", Reports.Csv("=1+1"));
        Assert.Equal("\"'+x\"", Reports.Csv("+x"));
        Assert.Equal("\"'-b\\pm c\"", Reports.Csv("-b\\pm c"));
        Assert.Equal("\"'@SUM(A1)\"", Reports.Csv("@SUM(A1)"));
        Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\")\"", Reports.Csv("=HYPERLINK(\"http://x\")"));
        Assert.Equal("\"a=b\"", Reports.Csv("a=b"), "only a leading character is neutralized");
    }

    [Test]
    public static void ParseCsvLine_RoundTripsCsvCells()
    {
        var vals = new[] { "plain", "a,b", "q\"uote", "", "=x" };
        var line = string.Join(",", vals.Select(Reports.Csv));
        Assert.SeqEqual(new[] { "plain", "a,b", "q\"uote", "", "'=x" }, Reports.ParseCsvLine(line));
    }

    /// <summary>The image format column comes from the media part's file extension when the bytes are not a known
    /// format, i.e. from untrusted input. It is written without <see cref="Reports.Csv"/>, so a part named
    /// "image1.=1+2,3" injects a formula and shifts the columns of inventory.csv.</summary>
    [Test]
    public static void InventoryCsv_FormatFromHostileExtension_IsNeutralized()
    {
        var zip = Build(Document(P(R(Inline("rIdX")))),
            extraRels: new[] { ("rIdX", "image", "media/image9.=1+2,3") },
            extraParts: new[] { ("word/media/image9.=1+2,3", new byte[32]) });
        var inv = Scan(zip).Inventory;
        Assert.Equal("=1+2,3", inv.Pictures.Single().Format, "precondition: format taken from the extension");
        var dir = TestEnv.TempDir("reports_inventory");
        var path = Path.Combine(dir, "inventory.csv");
        var res = new DocResult { InputPath = Path.Combine(dir, "x.docx"), Inventory = inv };
        Reports.WriteInventoryCsv(res, path);
        var lines = File.ReadAllLines(path);
        var header = Reports.ParseCsvLine(lines[0]);
        var row = Reports.ParseCsvLine(lines[1]);
        Assert.Equal(header.Count, row.Count, "column count of the picture row");
        var fmt = row[header.IndexOf("image_format")];
        Assert.False(fmt.Length > 0 && "=+-@".Contains(fmt[0]), "image_format cell starts with a formula character: " + fmt);
    }

    [Test]
    public static void Pct_Interpolates()
    {
        var s = new List<double> { 0.0, 1.0, 2.0, 3.0 };
        Assert.Equal(1.5, Reports.Pct(s, 0.5));
        Assert.Equal(0.0, Reports.Pct(s, 0));
        Assert.Equal(3.0, Reports.Pct(s, 1));
        Assert.True(double.IsNaN(Reports.Pct(new List<double>(), 0.5)));
    }
}

public static class GatesTests
{
    private static (string, byte[]) E(string n, string c) => (n, U8(c));

    [Test]
    public static void Compare_IdenticalAndChangedEntries()
    {
        var dir = TestEnv.TempDir("gates_compare");
        var a = Path.Combine(dir, "a.docx");
        var b = Path.Combine(dir, "b.docx");
        File.WriteAllBytes(a, Zip(new[] { E("[Content_Types].xml", "<T/>"), E("word/document.xml", "<d>1</d>"), E("word/media/i.png", "PNG") }));
        // same content, different compression: entries compare equal on uncompressed bytes
        File.WriteAllBytes(b, Zip(new[] { E("[Content_Types].xml", "<T/>"), E("word/document.xml", "<d>2</d>"), E("word/media/i.png", "PNG") },
            System.IO.Compression.CompressionLevel.NoCompression));
        var (differ, onlyA, onlyB, order) = Gates.Compare(a, b, new SafetyLimits());
        Assert.SeqEqual(new[] { "word/document.xml" }, differ, "differ");
        Assert.Equal(0, onlyA.Count);
        Assert.Equal(0, onlyB.Count);
        Assert.True(order, "same order");

        var self = Gates.Compare(a, a, new SafetyLimits());
        Assert.Equal(0, self.Differ.Count);
        Assert.True(self.SameOrder);
    }

    [Test]
    public static void Compare_MissingAddedAndReordered()
    {
        var dir = TestEnv.TempDir("gates_compare2");
        var a = Path.Combine(dir, "a.docx");
        var b = Path.Combine(dir, "b.docx");
        File.WriteAllBytes(a, Zip(new[] { E("x.xml", "<x/>"), E("y.xml", "<y/>"), E("gone.xml", "<g/>") }));
        File.WriteAllBytes(b, Zip(new[] { E("y.xml", "<y/>"), E("x.xml", "<x/>"), E("new.xml", "<n/>") }));
        var (differ, onlyA, onlyB, order) = Gates.Compare(a, b, new SafetyLimits());
        Assert.Equal(0, differ.Count, "differ");
        Assert.SeqEqual(new[] { "gone.xml" }, onlyA, "onlyA");
        Assert.SeqEqual(new[] { "new.xml" }, onlyB, "onlyB");
        Assert.False(order, "order differs");
    }

    [Test]
    public static void Compare_AfterSafePackageSave_OnlyReplacedPartDiffers()
    {
        var dir = TestEnv.TempDir("gates_compare3");
        var src = Path.Combine(dir, "in.docx");
        var dst = Path.Combine(dir, "out.docx");
        File.WriteAllBytes(src, Build(Document(P(R(Inline("rId1"))))));
        var pkg = SafePackage.Open(src, new SafetyLimits());
        pkg.Save(dst, new Dictionary<string, byte[]> { ["word/document.xml"] = U8(Document(P(T("replaced")))) });
        var (differ, onlyA, onlyB, order) = Gates.Compare(src, dst, new SafetyLimits());
        Assert.SeqEqual(new[] { "word/document.xml" }, differ);
        Assert.Equal(0, onlyA.Count + onlyB.Count);
        Assert.True(order);
    }

    [Test]
    public static void Compare_RespectsLimits()
    {
        var dir = TestEnv.TempDir("gates_compare4");
        var a = Path.Combine(dir, "a.docx");
        File.WriteAllBytes(a, Zip(new[] { E("1", "a"), E("2", "b"), E("3", "c") }));
        Assert.Throws<PackageLimitException>(() => Gates.Compare(a, a, new SafetyLimits { MaxEntries = 2 }));
    }
}
