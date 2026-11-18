using System.Text;
using System.Xml.Linq;
using MathMorph.Contracts;
using MathMorph.Engine;
using static MathMorph.Tests.Docx;

namespace MathMorph.Tests;

public static class RewriterTests
{
    private const string NL = "\r\n";
    private static readonly string FallbackPict = Vml("rId1", "width:30pt;height:15pt", "_x0000_i1099");

    private static string Body() =>
        "  " + P("<w:pPr><w:jc w:val=\"center\"/></w:pPr>" + R(Inline("rId1", id: 1))) + NL +
        "  <w:p>" + NL +
        "    " + T("Before ") + NL +
        "    <w:r w:rsidR=\"00A1B2C3\">\t<w:rPr><w:i/></w:rPr>" + Inline("rId2", id: 2) + "</w:r>" + NL +
        "    " + T(" after ä€") + NL +
        "  </w:p>" + NL +
        "  " + P(R("<mc:AlternateContent><mc:Choice Requires=\"wps\">" + Inline("rId1", id: 3) + "</mc:Choice><mc:Fallback>" +
                   FallbackPict + "</mc:Fallback></mc:AlternateContent>")) + NL +
        "  " + P(T("tail text \U0001F600"));

    private static XElement Om(string t) => XElement.Parse($"<m:oMath xmlns:m=\"{MNs}\"><m:r><m:t>{t}</m:t></m:r></m:oMath>");

    [Test]
    public static void Rewrite_ReplacesOnlyTargetRuns_KeepsEverythingElse()
    {
        var docText = Document(Body(), NL);
        var docBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(docText)).ToArray();
        var zip = Build("", documentBytes: docBytes);
        var res = Scan(zip);
        var scan = res.Parts.Single(p => p.Part == "word/document.xml");
        Assert.Equal(docText, scan.Text, "decoded text");
        Assert.Equal(4, scan.Items.Count, "a, b, c Choice, c Fallback");
        var targets = scan.Items.Where(i => i.Info.AltContent != AltContentBranch.Fallback).ToList();
        Assert.Equal(3, targets.Count);
        foreach (var (info, el) in targets)
            Assert.Null(Pipeline.NotReplaceable(info, el), "replaceable: ordinal " + info.Ordinal);
        Assert.True(targets[0].Info.Run!.ParagraphOnlyThisPicture, "a is display");
        Assert.False(targets[1].Info.Run!.ParagraphOnlyThisPicture, "b is inline");
        Assert.True(targets[2].Info.Run!.RunAltContentChoice, "c is run-level AlternateContent");
        Assert.Equal("wps", targets[2].Info.Run!.ChoiceRequires);

        var reps = targets.Select((t, k) => new Rewriter.Replacement(t.Info, t.Element, Om("X" + (k + 1)))).ToList();
        var outText = Rewriter.Rewrite(scan, reps);

        // original spans of the three runs
        var sp = new XmlSplicer(scan.Text);
        var spans = targets.Select(t => sp.SpanOf(t.Info.Run!.Line, t.Info.Run!.Column, t.Info.Run!.QualifiedName)).ToList();
        Assert.True(spans[0].End <= spans[1].Start && spans[1].End <= spans[2].Start, "spans in document order");
        Assert.True(scan.Text[spans[1].Start..spans[1].End].StartsWith("<w:r w:rsidR=", StringComparison.Ordinal), "span b is the run");
        var seg0 = scan.Text[..spans[0].Start];
        var seg1 = scan.Text[spans[0].End..spans[1].Start];
        var seg2 = scan.Text[spans[1].End..spans[2].Start];
        var seg3 = scan.Text[spans[2].End..];
        Assert.True(outText.StartsWith(seg0, StringComparison.Ordinal), "prefix identical");
        Assert.True(outText.EndsWith(seg3, StringComparison.Ordinal), "suffix identical");
        int i1 = outText.IndexOf(seg1, seg0.Length, StringComparison.Ordinal);
        Assert.True(i1 > 0, "segment between a and b kept");
        int i2 = outText.IndexOf(seg2, i1 + seg1.Length, StringComparison.Ordinal);
        Assert.True(i2 > 0, "segment between b and c kept");
        var repA = outText[seg0.Length..i1];
        var repB = outText[(i1 + seg1.Length)..i2];
        var repC = outText[(i2 + seg2.Length)..(outText.Length - seg3.Length)];

        // (a) display: m:oMathPara
        Assert.True(repA.StartsWith($"<m:oMathPara xmlns:m=\"{MNs}\"><m:oMath>", StringComparison.Ordinal), "a: " + repA);
        Assert.Contains("X1", repA);
        // (b) inline: m:oMath in place of the run
        Assert.True(repB.StartsWith($"<m:oMath xmlns:m=\"{MNs}\">", StringComparison.Ordinal), "b: " + repB);
        Assert.Contains("X2", repB);
        // (c) AlternateContent: math in Choice, untouched fallback content in a run in Fallback
        Assert.True(repC.StartsWith("<mc:AlternateContent><mc:Choice Requires=\"wps\">", StringComparison.Ordinal), "c: " + repC);
        Assert.Contains("X3", repC);
        Assert.Contains("<mc:Fallback><w:r>" + FallbackPict + "</w:r></mc:Fallback></mc:AlternateContent>", repC, "fallback verbatim");

        // structure after re-parse
        var doc = SafeXml.Parse(outText);
        var paras = doc.Root!.Element(Ns.W + "body")!.Elements(Ns.W + "p").ToList();
        Assert.Equal(4, paras.Count);
        Assert.Equal(1, paras[0].Elements(Ns.M + "oMathPara").Count(), "a is m:oMathPara");
        Assert.SeqEqual(new[] { "r", "oMath", "r" }, paras[1].Elements().Select(e => e.Name.LocalName), "b order");
        Assert.Equal("Before ", paras[1].Elements(Ns.W + "r").First().Value);
        var ac = paras[2].Element(Ns.MC + "AlternateContent")!;
        Assert.Equal(1, ac.Element(Ns.MC + "Choice")!.Descendants(Ns.M + "oMath").Count());
        Assert.Equal(1, ac.Element(Ns.MC + "Fallback")!.Elements(Ns.W + "r").Elements(Ns.W + "pict").Count());

        // encoded bytes keep the BOM and the unchanged prefix byte-for-byte
        var outBytes = SafeXml.Encode(outText, scan.Encoding, scan.Preamble);
        var prefixBytes = SafeXml.Encode(seg0, scan.Encoding, scan.Preamble);
        Assert.BytesEqual(docBytes[..prefixBytes.Length], prefixBytes, "byte prefix");
        Assert.BytesEqual(docBytes[^Encoding.UTF8.GetByteCount(seg3)..], outBytes[^Encoding.UTF8.GetByteCount(seg3)..], "byte suffix");

        // re-inventory: only the fallback picture remains; three equations were added
        var after = Scan(Build("", documentBytes: outBytes)).Inventory;
        Assert.Equal(1, after.Pictures.Count, "pictures after");
        Assert.Equal(AltContentBranch.Fallback, after.Pictures[0].AltContent);
        Assert.Equal(3, after.NativeOMath, "m:oMath after");
        Assert.Equal(2, after.NativeOMathPara, "m:oMathPara after");
    }

    [Test]
    public static void Rewrite_SameRunTwice_Throws()
    {
        var scan = Scan(Build(Document(P(R(Inline("rId1")))))).Parts[0];
        var (info, el) = scan.Items[0];
        Assert.Throws<InvalidOperationException>(() => Rewriter.Rewrite(scan, new[]
        {
            new Rewriter.Replacement(info, el, Om("a")), new Rewriter.Replacement(info, el, Om("b"))
        }));
    }

    [Test]
    public static void Rewrite_NoEdits_ReturnsTextUnchanged()
    {
        var scan = Scan(Build(Document(P(R(Inline("rId1"))), NL))).Parts[0];
        Assert.Equal(scan.Text, Rewriter.Rewrite(scan, Array.Empty<Rewriter.Replacement>()));
    }

    [Test]
    public static void Rewrite_PictureInTableCellAndTextBox()
    {
        var body = "<w:tbl><w:tr><w:tc><w:tcPr/>" + P(R(Inline("rId1"))) + "</w:tc></w:tr></w:tbl>" +
                   P(T("box: ") + R(TextBox(P(R(Inline("rId2")))))) ;
        var zip = Build(Document(body, NL));
        var scan = Scan(zip).Parts[0];
        Assert.Equal(2, scan.Items.Count);
        var reps = scan.Items.Select((t, k) => new Rewriter.Replacement(t.Info, t.Element, Om("T" + k))).ToList();
        var outText = Rewriter.Rewrite(scan, reps);
        var doc = SafeXml.Parse(outText);
        Assert.Equal(1, doc.Descendants(Ns.W + "tc").Single().Descendants(Ns.M + "oMathPara").Count(), "cell paragraph became display math");
        Assert.Equal(1, doc.Descendants(Ns.W + "txbxContent").Single().Descendants(Ns.M + "oMathPara").Count(), "text box paragraph became display math");
        Assert.Equal(0, doc.Descendants(Ns.A + "blip").Count(), "no pictures left");
        Assert.Equal(1, doc.Descendants(Ns.W + "drawing").Count(), "text box shape kept");
    }

    [Test]
    public static void Rewrite_OleObjectRun()
    {
        var scan = Scan(Build(Document(P(T("see ") + R(Ole("rId1")) + T(" here"))))).Parts[0];
        var (info, el) = scan.Items.Single();
        Assert.Null(Pipeline.NotReplaceable(info, el));
        var outText = Rewriter.Rewrite(scan, new[] { new Rewriter.Replacement(info, el, Om("q")) });
        var p = SafeXml.Parse(outText).Descendants(Ns.W + "p").Single();
        Assert.SeqEqual(new[] { "r", "oMath", "r" }, p.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(0, p.Descendants(Ns.W + "object").Count());
    }
}
