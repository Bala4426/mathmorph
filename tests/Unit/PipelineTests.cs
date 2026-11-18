using MathMorph.Contracts;
using MathMorph.Engine;
using static MathMorph.Tests.Docx;

namespace MathMorph.Tests;

public static class PipelineTests
{
    private static string? Why(string body)
    {
        var scan = ScanBody(body).Parts[0];
        var (info, el) = scan.Items.Single();
        return Pipeline.NotReplaceable(info, el);
    }

    [Test]
    public static void NotReplaceable_PlainRunInParagraph_IsNull()
    {
        Assert.Null(Why(P(R(Inline("rId1")))));
        Assert.Null(Why(P(T("x ") + R("<w:rPr><w:b/></w:rPr>" + Inline("rId1")) + T(" y"))), "inline with rPr");
    }

    [Test]
    public static void NotReplaceable_InsideTrackedChange_HasReason()
    {
        var r = Why(P("<w:ins w:id=\"1\" w:author=\"a\" w:date=\"2024-01-01T00:00:00Z\">" + R(Inline("rId1")) + "</w:ins>"));
        Assert.NotNull(r);
        Assert.Contains("tracked change", r!);
        Assert.NotNull(Why(P("<w:del w:id=\"2\" w:author=\"a\">" + R(Inline("rId1")) + "</w:del>")), "w:del");
        Assert.NotNull(Why(P("<w:moveTo w:id=\"3\" w:author=\"a\">" + R(Inline("rId1")) + "</w:moveTo>")), "w:moveTo");
    }

    [Test]
    public static void NotReplaceable_RunWithTextAndPicture_HasReason()
    {
        var r = Why(P("<w:r><w:t>x=</w:t>" + Inline("rId1") + "</w:r>"));
        Assert.NotNull(r);
        Assert.Contains("other content", r!);
        Assert.NotNull(Why(P("<w:r>" + Inline("rId1") + "<w:tab/></w:r>")), "picture + tab in one run");
    }

    [Test]
    public static void NotReplaceable_SupportedAndUnsupportedParents()
    {
        Assert.Null(Why(P("<w:hyperlink r:id=\"rIdX\">" + R(Inline("rId1")) + "</w:hyperlink>")), "hyperlink");
        Assert.Null(Why(P("<w:sdt><w:sdtPr/><w:sdtContent>" + R(Inline("rId1")) + "</w:sdtContent></w:sdt>")), "run-level sdt");
        Assert.Null(Why("<w:tbl><w:tr><w:tc>" + P(R(Inline("rId1"))) + "</w:tc></w:tr></w:tbl>"), "table cell");
        var r = Why(P("<w:customXml w:element=\"x\"><w:smartTag w:element=\"y\">" + R(Inline("rId1")) + "</w:smartTag></w:customXml>"));
        Assert.Null(r, "smartTag");
        var dir = Why(P("<w:dir w:val=\"rtl\">" + R(Inline("rId1")) + "</w:dir>"));
        Assert.NotNull(dir, "w:dir is not a supported run parent");
        Assert.Contains("unsupported run parent", dir!);
    }

    [Test]
    public static void SafeName_And_PathsEqual()
    {
        Assert.Equal("a_b_c", Pipeline.SafeName("a:b*c"));
        Assert.Equal("name", Pipeline.SafeName(" name. "));
        Assert.True(Pipeline.PathsEqual(@"C:\X\y.docx", @"c:\x\Y.DOCX"));
        Assert.False(Pipeline.PathsEqual(@"C:\X\y.docx", @"C:\X\z.docx"));
        Assert.Equal(12, Pipeline.StableHash("abc").Length);
        Assert.Equal(Pipeline.StableHash("abc"), Pipeline.StableHash("abc"));
    }
}
