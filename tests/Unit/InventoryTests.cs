using System.Xml.Linq;
using MathMorph.Contracts;
using MathMorph.Engine;
using static MathMorph.Tests.Docx;

namespace MathMorph.Tests;

public static class InventoryTests
{
    private static PictureInfo Only(Inventory.Result r)
    {
        Assert.Equal(1, r.Inventory.Pictures.Count, "picture count");
        return r.Inventory.Pictures[0];
    }

    private static PictureInfo One(string body) => Only(ScanBody(body));

    [Test]
    public static void Classify_SmallPngAndJpegInline_AreCandidates()
    {
        var png = One(P(R(Inline("rId1"))));
        Assert.Equal(Category.Candidate, png.Category);
        Assert.Equal("png", png.Format);
        Assert.Equal(PictureKind.Inline, png.Kind);
        Assert.Equal("word/media/image1.png", png.Target);
        Assert.Equal("body", png.Container);
        Assert.Equal(64, png.Sha256!.Length, "sha256 hex length");
        var jpg = One(P(R(Inline("rId2"))));
        Assert.Equal(Category.Candidate, jpg.Category);
        Assert.Equal("jpeg", jpg.Format);
    }

    [Test]
    public static void Classify_HeaderContainer_IsExcluded()
    {
        var hdr = Part("w:hdr", P(R(Inline("rIdH1"))));
        var zip = Build(Document(P(T("body text"))),
            extraRels: new[] { ("rIdHdr", "header", "header1.xml") },
            extraParts: new[] { ("word/header1.xml", U8(hdr)), ("word/_rels/header1.xml.rels", U8(Rels(("rIdH1", "image", "media/image1.png")))) });
        var p = Only(Scan(zip));
        Assert.Equal("word/header1.xml", p.Part);
        Assert.Equal("header", p.Container);
        Assert.Equal(Category.Excluded, p.Category);
        Assert.Equal("container:header", p.Reason);
        Assert.Equal("png", p.Format, "header rels resolved");
    }

    [Test]
    public static void Classify_McFallback_IsExcluded_ChoiceIsCandidate()
    {
        var body = P(R("<mc:AlternateContent><mc:Choice Requires=\"wps\">" + Inline("rId1") + "</mc:Choice><mc:Fallback>" + Vml("rId1") + "</mc:Fallback></mc:AlternateContent>"));
        var pics = ScanBody(body).Inventory.Pictures;
        Assert.Equal(2, pics.Count);
        Assert.Equal(AltContentBranch.Choice, pics[0].AltContent);
        Assert.Equal(Category.Candidate, pics[0].Category);
        Assert.False(pics[0].ParagraphHasOtherContent, "fallback mirror is not other content");
        Assert.Equal(AltContentBranch.Fallback, pics[1].AltContent);
        Assert.Equal(Category.Excluded, pics[1].Category);
        Assert.Contains("mc:Fallback", pics[1].Reason);
        Assert.Equal(pics[0].ParaIndex, pics[1].ParaIndex);
    }

    [Test]
    public static void Classify_AnchorAndAbsoluteVml_AreExcluded()
    {
        var a = One(P(R(Anchor("rId1"))));
        Assert.Equal(PictureKind.Anchor, a.Kind);
        Assert.Equal(Category.Excluded, a.Category);
        Assert.Equal("anchored/floating", a.Reason);
        var v = One(P(R(Vml("rId1", "position:absolute;margin-left:10pt;width:30pt;height:15pt"))));
        Assert.Equal(PictureKind.VmlFloating, v.Kind);
        Assert.Equal(Category.Excluded, v.Category);
        var inl = One(P(R(Vml("rId1", "width:30pt;height:15pt"))));
        Assert.Equal(PictureKind.Vml, inl.Kind);
        Assert.Equal(Category.Candidate, inl.Category);
        Assert.Equal(30 * 12700L, inl.ExtentCx);
        Assert.Equal(15 * 12700L, inl.ExtentCy);
    }

    [Test]
    public static void Classify_EmfWmf_AreUnsupported()
    {
        var emf = One(P(R(Inline("rId3"))));
        Assert.Equal("emf", emf.Format);
        Assert.Equal(Category.UnsupportedFormat, emf.Category);
        var wmf = One(P(R(Inline("rId4"))));
        Assert.Equal("wmf", wmf.Format);
        Assert.Equal(Category.UnsupportedFormat, wmf.Category);
        var gif = One(P(R(Inline("rId5"))));
        Assert.Equal(Category.UnsupportedFormat, gif.Category);
        Assert.Equal("format gif", gif.Reason);
    }

    [Test]
    public static void Classify_TallerThanOneInch_IsExcluded()
    {
        var tall = One(P(R(Inline("rId1", cy: Inventory.OneInchEmu + 1))));
        Assert.Equal(Category.Excluded, tall.Category);
        Assert.Equal("taller than 1 inch", tall.Reason);
        var exact = One(P(R(Inline("rId1", cy: Inventory.OneInchEmu))));
        Assert.Equal(Category.Candidate, exact.Category, "exactly 1 inch is not taller");
    }

    [Test]
    public static void Classify_OlePreviews()
    {
        var png = One(P(R(Ole("rId1"))));
        Assert.Equal(PictureKind.Ole, png.Kind);
        Assert.Equal(Category.OlePreview, png.Category);
        Assert.NotNull(png.Run, "OLE run locator");
        Assert.True(png.Run!.OnlyPictureInRun, "w:object is the only content of its run");
        var wmf = One(P(R(Ole("rId4"))));
        Assert.Equal(Category.UnsupportedFormat, wmf.Category);
        Assert.Equal("wmf", wmf.Format);
        // OLE size falls back to dxaOrig/dyaOrig (twips) when the shape has no style size
        var noStyle = One(P(R(Ole("rId1", style: ""))));
        Assert.Equal(600 * 635L, noStyle.ExtentCx);
        Assert.Equal(300 * 635L, noStyle.ExtentCy);
    }

    [Test]
    public static void Classify_LogoNameOrAltText_IsExcluded()
    {
        var logo = One(P(R(Inline("rId1", name: "Company logo"))));
        Assert.Equal(Category.Excluded, logo.Category);
        Assert.Contains("logo", logo.Reason);
        var alt = One(P(R(Inline("rId1", descr: "Figure 3"))));
        Assert.Equal(Category.Excluded, alt.Category);
        var plain = One(P(R(Inline("rId1", name: "Picture 7", descr: "equation"))));
        Assert.Equal(Category.Candidate, plain.Category);
    }

    [Test]
    public static void Classify_MissingOrExternalImage_IsNoImage()
    {
        var missing = One(P(R(Inline("rId99"))));
        Assert.Equal(Category.Excluded, missing.Category);
        Assert.True(Inventory.IsNoImage(missing), "no_image reason: " + missing.Reason);
        // external link
        var relsXml = "<?xml version=\"1.0\"?><Relationships xmlns=\"" + PrNs + "\"><Relationship Id=\"rIdExt\" Type=\"" + RelBase +
                      "image\" Target=\"http://example.invalid/a.png\" TargetMode=\"External\"/></Relationships>";
        var ext = Only(Scan(Zip(new[] { ("_rels/.rels", U8(RootRels)), ("word/document.xml", U8(Document(P(R(Inline("rIdExt")))))),
            ("word/_rels/document.xml.rels", U8(relsXml)) })));
        Assert.True(ext.ExternalLink, "external");
        Assert.Contains("external link", ext.Reason);
    }

    [Test]
    public static void Classify_PriorityOrder_DirectCalls()
    {
        PictureInfo Pi(string container = "body", PictureKind kind = PictureKind.Inline, AltContentBranch alt = AltContentBranch.None,
            string format = "png", long cy = 100, string name = "") =>
            new() { Part = "word/document.xml", Container = container, ParaIndex = 0, Ordinal = 0, Kind = kind, AltContent = alt, Format = format, ExtentCy = cy, DocPrName = name };
        void Check(PictureInfo p, bool hasImage, Category c, string reasonPart)
        {
            Inventory.Classify(p, null, hasImage);
            Assert.Equal(c, p.Category, "category for " + reasonPart);
            Assert.Contains(reasonPart, p.Reason);
        }
        Check(Pi("header", PictureKind.Anchor, AltContentBranch.Fallback, "emf", 9_999_999, "logo"), true, Category.Excluded, "container:header");
        Check(Pi("footnote"), true, Category.Excluded, "container:footnote");
        Check(Pi(alt: AltContentBranch.Fallback, kind: PictureKind.Anchor), true, Category.Excluded, "mc:Fallback");
        Check(Pi(kind: PictureKind.Anchor, format: "emf"), true, Category.Excluded, "anchored");
        Check(Pi(kind: PictureKind.VmlFloating), false, Category.Excluded, "anchored");
        Check(Pi(format: "emf"), false, Category.Excluded, "no_image");
        Check(Pi(format: "emf", cy: 9_999_999), true, Category.UnsupportedFormat, "EMF");
        Check(Pi(format: "gif", cy: 9_999_999, name: "logo"), true, Category.Excluded, "taller");
        Check(Pi(format: "gif", name: "logo"), true, Category.UnsupportedFormat, "format gif");
        Check(Pi(format: "png", kind: PictureKind.Ole, name: "chart"), true, Category.Excluded, "logo");
        Check(Pi(format: "jpeg", kind: PictureKind.Ole), true, Category.OlePreview, "OLE");
        Check(Pi(format: "png-unverified"), true, Category.UnsupportedFormat, "png-unverified");
        Check(Pi(kind: PictureKind.Vml), true, Category.Candidate, "candidate");

        // a picture inside a native equation wins over everything
        var el = XElement.Parse("<m:oMath xmlns:m=\"" + MNs + "\" xmlns:w=\"" + WNs + "\"><w:drawing/></m:oMath>").Elements().First();
        var n = Pi("header", PictureKind.Anchor);
        Inventory.Classify(n, el, true);
        Assert.Equal(Category.Native, n.Category);
    }

    [Test]
    public static void VmlSize_ParsesUnits()
    {
        Assert.Equal((12 * 12700L, 6 * 12700L), Inventory.VmlSize("width:12pt;height:6pt"));
        Assert.Equal((457200L, 914400L), Inventory.VmlSize("width:0.5in;height:1in"));
        Assert.Equal((96 * 9525L, 48 * 9525L), Inventory.VmlSize("width:96px;height:48px"));
        Assert.Equal((360000L, 180000L), Inventory.VmlSize("WIDTH: 1cm ; HEIGHT : 0.5CM"));
        Assert.Equal((36000L, 72000L), Inventory.VmlSize("width:1mm;height:2mm"));
        Assert.Equal((152400L, 10L), Inventory.VmlSize("width:1pc;height:10emu"));
        Assert.Equal((10 * 9525L, 0L), Inventory.VmlSize("width:10"), "unitless = px, no height");
        Assert.Equal((0L, 12700L), Inventory.VmlSize("mso-width-percent:100;margin-left:3pt;height:1pt"), "mso-width-* is not width");
        Assert.Equal((0L, 0L), Inventory.VmlSize(""));
        Assert.Equal((12700L, 0L), Inventory.VmlSize("position:absolute;width:1pt;mso-height-relative:margin"));
    }

    [Test]
    public static void DetectFormat_MagicNumbers()
    {
        Assert.Equal("png", Inventory.DetectFormat(Png, "x.jpg"));
        Assert.Equal("jpeg", Inventory.DetectFormat(Jpeg, "x.png"));
        Assert.Equal("gif", Inventory.DetectFormat(Gif, "x"));
        Assert.Equal("bmp", Inventory.DetectFormat(Bmp, "x"));
        Assert.Equal("emf", Inventory.DetectFormat(Emf, "x.png"));
        Assert.Equal("wmf", Inventory.DetectFormat(WmfPlaceable, "x.png"));
        Assert.Equal("wmf", Inventory.DetectFormat(WmfStandard, "x"));
        Assert.Equal("tiff", Inventory.DetectFormat(Tiff, "x"));
        var junk = new byte[16];
        Assert.Equal("png-unverified", Inventory.DetectFormat(junk, "word/media/a.PNG"));
        Assert.Equal("jpeg-unverified", Inventory.DetectFormat(junk, "a.jpg"));
        Assert.Equal("svg", Inventory.DetectFormat(junk, "a.svg"));
        Assert.Equal("unknown", Inventory.DetectFormat(junk, "noext"));
        Assert.Equal("unknown", Inventory.DetectFormat(Array.Empty<byte>(), "noext"));
        Assert.Equal("png-unverified", Inventory.DetectFormat(new byte[] { 0x89, 0x50, 0x4E }, "a.png"), "truncated PNG signature");
    }

    [Test]
    public static void ParagraphIndex_CountsTableAndTextBoxParagraphs_TextBoxPictureOnce()
    {
        var body =
            P(T("p0")) +
            "<w:tbl><w:tr><w:tc>" + P(T("p1")) + "</w:tc><w:tc>" + P(R(Inline("rId1", name: "A"))) + "</w:tc></w:tr></w:tbl>" +
            P(T("p3 holds a text box ") + R(TextBox(P(R(Inline("rId1", name: "B")))))) +
            P(R(Inline("rId2", name: "C")));
        var r = ScanBody(body);
        var pics = r.Inventory.Pictures;
        Assert.Equal(3, pics.Count, "text box shape itself is not a picture; its inner picture counts once");
        Assert.Equal("A", pics[0].DocPrName);
        Assert.Equal(2, pics[0].ParaIndex);
        Assert.Equal("table", pics[0].Container);
        Assert.Equal("B", pics[1].DocPrName);
        Assert.Equal(4, pics[1].ParaIndex);
        Assert.Equal("textbox", pics[1].Container);
        Assert.False(pics[1].ParagraphHasOtherContent, "inner text box paragraph holds only the picture");
        Assert.Equal(Category.Candidate, pics[1].Category);
        Assert.Equal("C", pics[2].DocPrName);
        Assert.Equal(5, pics[2].ParaIndex);
        Assert.Equal("body", pics[2].Container);
        Assert.SeqEqual(new[] { 0, 1, 2 }, pics.Select(p => p.Ordinal), "ordinals");
    }

    [Test]
    public static void DrawingWithoutBlip_IsNotAPicture()
    {
        var r = ScanBody(P(R(TextBox(P(T("just text"))))) + P(R("<w:pict><v:rect style=\"width:10pt;height:10pt\"/></w:pict>")));
        Assert.Equal(0, r.Inventory.Pictures.Count);
    }

    [Test]
    public static void NativeMath_IsCounted()
    {
        var body = "<w:p><m:oMathPara xmlns:m=\"" + MNs + "\"><m:oMath><m:r><m:t>x</m:t></m:r></m:oMath></m:oMathPara></w:p>" +
                   "<w:p><m:oMath xmlns:m=\"" + MNs + "\"><m:r><m:t>y</m:t></m:r></m:oMath></w:p>";
        var inv = ScanBody(body).Inventory;
        Assert.Equal(2, inv.NativeOMath);
        Assert.Equal(1, inv.NativeOMathPara);
    }

    private static (XElement Para, XElement Pic) ParaAndPic(string paraXml)
    {
        var doc = SafeXml.Parse(Document(paraXml));
        var para = doc.Descendants(Ns.W + "p").First();
        var pic = para.Descendants().First(e => e.Name == Ns.W + "drawing" && e.Descendants(Ns.A + "blip").Any());
        return (para, pic);
    }

    [Test]
    public static void ParagraphHasOtherContent_Cases()
    {
        bool H(string p) { var (para, pic) = ParaAndPic(p); return Inventory.ParagraphHasOtherContent(para, pic); }
        Assert.False(H(P("<w:pPr><w:jc w:val=\"center\"/></w:pPr><w:bookmarkStart w:id=\"0\" w:name=\"b\"/>" + R("<w:rPr><w:b/></w:rPr>" + Inline("rId1")) + "<w:bookmarkEnd w:id=\"0\"/><w:r><w:rPr/></w:r>")), "only picture + markup");
        Assert.False(H(P(T("   ") + R(Inline("rId1")))), "whitespace text");
        Assert.True(H(P(T("x = ") + R(Inline("rId1")))), "text");
        Assert.True(H(P(R("<w:tab/>") + R(Inline("rId1")))), "tab");
        Assert.True(H(P(R(Inline("rId1")) + R("<w:br/>"))), "break");
        Assert.True(H(P(R(Inline("rId1")) + R(Inline("rId2")))), "second picture");
        Assert.True(H(P(R(Inline("rId1")) + R("<w:sym w:font=\"Symbol\" w:char=\"F061\"/>"))), "symbol");
        Assert.True(H(P(R(Inline("rId1")) + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>")), "footnote reference");
        Assert.True(H(P(R(Inline("rId1")) + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>")), "field");
        Assert.True(H(P("<w:ins w:id=\"1\" w:author=\"a\"><w:r><w:t>inserted</w:t></w:r></w:ins>" + R(Inline("rId1")))), "text inside w:ins");
        Assert.True(H(P(R(Inline("rId1")) + "<w:del w:id=\"2\" w:author=\"a\"><w:r><w:delText>gone</w:delText></w:r></w:del>")), "deleted text");
        Assert.True(H(P(R(Inline("rId1")) + "<w:hyperlink r:id=\"x\"><w:r><w:t>link</w:t></w:r></w:hyperlink>")), "hyperlink text");
    }
}
