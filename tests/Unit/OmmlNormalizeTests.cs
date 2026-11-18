using System.Xml.Linq;
using MathMorph.Engine;

namespace MathMorph.Tests;

/// <summary>Regression: Pandoc emits m:mcPr children as (mcJc, count); the OMML schema requires (count, mcJc).
/// Found by the Open XML validator on a converted real document (Gate 3).</summary>
public static class OmmlNormalizeTests
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    [Test]
    public static void Normalize_McPr_PutsCountBeforeMcJc()
    {
        var x = new XElement(M + "oMath",
            new XElement(M + "m", new XElement(M + "mPr",
                new XElement(M + "mcs", new XElement(M + "mc", new XElement(M + "mcPr",
                    new XElement(M + "mcJc", new XAttribute(M + "val", "center")),
                    new XElement(M + "count", new XAttribute(M + "val", "1")))))),
                new XElement(M + "mr", new XElement(M + "e", new XElement(M + "r", new XElement(M + "t", "a"))))));
        OmmlConverter.Normalize(x);
        var names = x.Descendants(M + "mcPr").Single().Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal("count,mcJc", string.Join(",", names), "mcPr child order");
        OmmlConverter.Validate(x);
    }

    [Test]
    public static void Normalize_MPr_SchemaOrderAndStableUnknown()
    {
        var x = new XElement(M + "mPr", new XElement(M + "mcs"), new XElement(M + "plcHide"), new XElement(M + "baseJc"));
        OmmlConverter.Normalize(x);
        Assert.Equal("baseJc,plcHide,mcs", string.Join(",", x.Elements().Select(e => e.Name.LocalName)), "mPr order");
    }

    [Test]
    public static void Normalize_RPr_NorExcludesSty()
    {
        var x = new XElement(M + "oMath", new XElement(M + "r",
            new XElement(M + "rPr", new XElement(M + "nor"), new XElement(M + "sty", new XAttribute(M + "val", "p"))),
            new XElement(M + "t", "-th")));
        OmmlConverter.Normalize(x);
        Assert.Equal("nor", string.Join(",", x.Descendants(M + "rPr").Single().Elements().Select(e => e.Name.LocalName)), "rPr children");
    }

    [Test]
    public static void Convert_Text_HasNoStyWithNor()
    {
        var pandoc = Path.Combine(TestEnv.Root, "tools", "pandoc", "pandoc.exe");
        if (!File.Exists(pandoc)) return;
        var x = new OmmlConverter(pandoc, Path.Combine(TestEnv.Root, "out", "unit_tmp", "omml_norm")).Convert(@"n\text{-th}");
        foreach (var r in x.Descendants(M + "rPr"))
            Assert.False(r.Element(M + "nor") != null && r.Element(M + "sty") != null, "nor and sty together");
    }

    [Test]
    public static void Convert_Matrix_HasSchemaOrderedMcPr()
    {
        var pandoc = Path.Combine(TestEnv.Root, "tools", "pandoc", "pandoc.exe");
        if (!File.Exists(pandoc)) return;
        var c = new OmmlConverter(pandoc, Path.Combine(TestEnv.Root, "out", "unit_tmp", "omml_norm"));
        var x = c.Convert(@"\left\{\begin{array}{cc} a & b \\ c & d \end{array}\right.");
        foreach (var mcPr in x.Descendants(M + "mcPr"))
        {
            var n = mcPr.Elements().Select(e => e.Name.LocalName).ToList();
            int ic = n.IndexOf("count"), ij = n.IndexOf("mcJc");
            Assert.True(ic < 0 || ij < 0 || ic < ij, "count precedes mcJc");
        }
    }
}
