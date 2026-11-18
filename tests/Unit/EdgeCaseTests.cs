using System.Text;
using MathMorph.Contracts;
using MathMorph.Engine;
using static MathMorph.Tests.Docx;

namespace MathMorph.Tests;

/// <summary>Edge cases found while reading the engine source.</summary>
public static class EdgeCaseTests
{
    [Test]
    public static void SpanOf_AfterSurrogatePairsEntitiesAndTabsOnSameLine()
    {
        var text = "<r><t>\U0001F600\U0001F600 &amp; &#x1F600;\t</t>\t<a k=\"\U0001F600\"><b/></a><c/></r>";
        var doc = SafeXml.Parse(text, lineInfo: true);
        var sp = new XmlSplicer(text);
        var (s, e) = sp.SpanOf(doc.Root!.Element("a")!);
        Assert.Equal("<a k=\"\U0001F600\"><b/></a>", text[s..e]);
        var (cs, ce) = sp.SpanOf(doc.Root!.Element("c")!);
        Assert.Equal("<c/>", text[cs..ce]);
    }

    /// <summary>OPC part names are case-insensitive. SafePackage.Get honours that, but Save looks replacements up
    /// with the caller's dictionary, keyed by the part name that Inventory derived from the relationship target.
    /// If the target's case differs from the zip entry name, the rewritten part is silently not written.</summary>
    [Test]
    public static void Save_PartNameFromRelationshipWithDifferentCase_IsReplaced()
    {
        var rootRels = RootRels.Replace("Target=\"word/document.xml\"", "Target=\"word/Document.xml\"");
        var zip = Zip(new[]
        {
            ("[Content_Types].xml", U8(ContentTypes)), ("_rels/.rels", U8(rootRels)),
            ("word/document.xml", U8(Document(P(R(Inline("rId1")))))),
            ("word/_rels/document.xml.rels", U8(Rels(StandardRels))),
            ("word/media/image1.png", Png),
        });
        var pkg = Open(zip);
        var scan = Inventory.Scan(pkg, "case.docx");
        var part = scan.Parts[0].Part;
        Assert.Equal(1, scan.Inventory.Pictures.Count, "picture found through case-insensitive lookup");
        var dir = TestEnv.TempDir("edge_case_save");
        var outPath = Path.Combine(dir, "out.docx");
        var newBytes = U8(Document(P(T("rewritten"))));
        pkg.Save(outPath, new Dictionary<string, byte[]> { [part] = newBytes });
        var written = SafePackage.Open(outPath, new SafetyLimits()).Get("word/document.xml")!.Data;
        Assert.True(written.AsSpan().SequenceEqual(newBytes),
            $"replacement keyed '{part}' was not applied to entry 'word/document.xml' (output still has the original part)");
    }

    /// <summary>OPC maps part names to zip item names in percent-encoded form (e.g. "image%201.png").
    /// Relationships.Resolve unescapes the target, so the entry is not found and the picture is reported NO_IMAGE.</summary>
    [Test]
    public static void Image_PercentEncodedZipItemName_IsFound()
    {
        var zip = Build(Document(P(R(Inline("rIdSp")))),
            extraRels: new[] { ("rIdSp", "image", "media/image%201.png") },
            extraParts: new[] { ("word/media/image%201.png", Png) });
        var p = Scan(zip).Inventory.Pictures.Single();
        Assert.False(Inventory.IsNoImage(p), $"picture reported as '{p.Reason}' (target resolved to '{p.Target}')");
        Assert.Equal(Category.Candidate, p.Category);
    }

    [Test]
    public static void Image_LiteralSpaceZipItemName_IsFound()
    {
        var zip = Build(Document(P(R(Inline("rIdSp")))),
            extraRels: new[] { ("rIdSp", "image", "media/image%201.png") },
            extraParts: new[] { ("word/media/image 1.png", Png) });
        var p = Scan(zip).Inventory.Pictures.Single();
        Assert.Equal(Category.Candidate, p.Category);
    }

    /// <summary>The logo/figure word list contains "fig." but the pattern is wrapped in \b...\b; after a '.'
    /// followed by a space or end of text there is no word boundary, so the usual "Fig. 2" never matches.</summary>
    [Test]
    public static void Classify_FigDotAltText_IsExcluded()
    {
        var p = new PictureInfo
        {
            Part = "word/document.xml", Container = "body", ParaIndex = 0, Ordinal = 0, Kind = PictureKind.Inline,
            Format = "png", ExtentCy = 100, DocPrName = "Picture 3", DocPrDescr = "Fig. 2 schematic",
        };
        Inventory.Classify(p, null, true);
        Assert.Equal(Category.Excluded, p.Category, "category for alt text 'Fig. 2 schematic' (reason: " + p.Reason + ")");
    }

    [Test]
    public static void Rewrite_AltContentWithRunPropertiesAndSelfClosingFallback()
    {
        var body = P(T("a ") + "<w:r><w:rPr><w:b/></w:rPr><mc:AlternateContent><mc:Choice Requires=\"wps\">" + Inline("rId1") +
                     "</mc:Choice><mc:Fallback/></mc:AlternateContent></w:r>" + T(" b"));
        var scan = Scan(Build(Document(body))).Parts[0];
        var (info, el) = scan.Items.Single();
        Assert.True(info.Run!.RunAltContentChoice);
        Assert.Null(Pipeline.NotReplaceable(info, el));
        var om = System.Xml.Linq.XElement.Parse($"<m:oMath xmlns:m=\"{MNs}\"><m:r><m:t>z</m:t></m:r></m:oMath>");
        var outText = Rewriter.Rewrite(scan, new[] { new Rewriter.Replacement(info, el, om) });
        Assert.Contains($"<mc:AlternateContent><mc:Choice Requires=\"wps\"><m:oMath xmlns:m=\"{MNs}\">", outText);
        Assert.Contains("<mc:Fallback><w:r><w:rPr><w:b/></w:rPr></w:r></mc:Fallback></mc:AlternateContent>", outText);
        var p = SafeXml.Parse(outText).Descendants(Ns.W + "p").Single();
        Assert.SeqEqual(new[] { "r", "AlternateContent", "r" }, p.Elements().Select(e => e.Name.LocalName));
    }

    [Test]
    public static void Scan_Utf16Document_IsReadAndRewritable()
    {
        var text = Document(P(T("ä ") + R(Inline("rId1")))).Replace("encoding=\"UTF-8\"", "encoding=\"UTF-16\"");
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(new UnicodeEncoding(false, false).GetBytes(text)).ToArray();
        var scan = Scan(Build("", documentBytes: bytes)).Parts[0];
        var (info, el) = scan.Items.Single();
        var om = System.Xml.Linq.XElement.Parse($"<m:oMath xmlns:m=\"{MNs}\"><m:r><m:t>z</m:t></m:r></m:oMath>");
        var outText = Rewriter.Rewrite(scan, new[] { new Rewriter.Replacement(info, el, om) });
        var outBytes = SafeXml.Encode(outText, scan.Encoding, scan.Preamble);
        Assert.True(outBytes[0] == 0xFF && outBytes[1] == 0xFE, "UTF-16 LE BOM kept");
        Assert.Equal(1, Scan(Build("", documentBytes: outBytes)).Inventory.NativeOMath);
    }
}
