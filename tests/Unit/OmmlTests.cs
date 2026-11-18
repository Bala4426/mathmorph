using System.Xml.Linq;
using MathMorph.Engine;

namespace MathMorph.Tests;

public static class OmmlTests
{
    private const string M = Docx.MNs;
    private static XElement X(string inner, string extraNs = "") => XElement.Parse($"<m:oMath xmlns:m=\"{M}\"{extraNs}>{inner}</m:oMath>");

    internal static XElement Frac() => X("<m:f><m:fPr><m:type m:val=\"bar\"/></m:fPr><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f>");

    [Test]
    public static void Validate_AcceptsValidOMath()
    {
        OmmlConverter.Validate(Frac());
        OmmlConverter.Validate(X("<m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup>"));
        // an n-ary operator with only a character property is not empty
        OmmlConverter.Validate(X("<m:nary><m:naryPr><m:chr m:val=\"∑\"/></m:naryPr><m:sub/><m:sup/><m:e/></m:nary>"));
    }

    [Test]
    public static void Validate_RejectsNonMathRoot()
    {
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(XElement.Parse($"<m:oMathPara xmlns:m=\"{M}\"><m:oMath><m:r><m:t>x</m:t></m:r></m:oMath></m:oMathPara>")));
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(XElement.Parse($"<w:p xmlns:w=\"{Docx.WNs}\"/>")));
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(XElement.Parse("<oMath><r><t>x</t></r></oMath>")), "no namespace");
    }

    [Test]
    public static void Validate_RejectsForeignElementsAndAttributes()
    {
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("<m:r><w:rPr/><m:t>x</m:t></m:r>", $" xmlns:w=\"{Docx.WNs}\"")), "w:rPr");
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("<m:r><m:t>x</m:t></m:r><script xmlns=\"http://www.w3.org/1999/xhtml\"/>")), "html");
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("<m:r w:foo=\"1\"><m:t>x</m:t></m:r>", $" xmlns:w=\"{Docx.WNs}\"")), "foreign attribute");
    }

    [Test]
    public static void Validate_RejectsUnknownMathElements()
    {
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("<m:bogus/><m:r><m:t>x</m:t></m:r>")));
    }

    [Test]
    public static void Validate_RejectsEmptyEquation()
    {
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("")));
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("<m:r><m:t></m:t></m:r>")));
        Assert.Throws<InvalidOperationException>(() => OmmlConverter.Validate(X("<m:f><m:num/><m:den/></m:f>")));
    }

    [Test]
    public static void Serialize_Display_YieldsOMathParaWithLocalNamespace()
    {
        var s = OmmlConverter.Serialize(Frac(), display: true);
        Assert.True(s.StartsWith($"<m:oMathPara xmlns:m=\"{M}\">", StringComparison.Ordinal), "starts with oMathPara + xmlns:m: " + s);
        Assert.Equal(1, CountOf(s, "xmlns"), "exactly one namespace declaration");
        var e = XElement.Parse(s);
        Assert.Equal(XName.Get("oMathPara", M), e.Name);
        Assert.Equal(XName.Get("oMath", M), e.Elements().Single().Name);
        Assert.Equal(1, e.Descendants(XName.Get("f", M)).Count());
    }

    [Test]
    public static void Serialize_Inline_YieldsOMathWithLocalNamespace_FromNestedSource()
    {
        // element taken from a document whose root declares the namespace (and others)
        var doc = XDocument.Parse($"<w:document xmlns:w=\"{Docx.WNs}\" xmlns:m=\"{M}\"><w:p><m:oMath><m:r><m:t>y</m:t></m:r></m:oMath></w:p></w:document>");
        var om = doc.Descendants(XName.Get("oMath", M)).Single();
        var s = OmmlConverter.Serialize(om, display: false);
        Assert.True(s.StartsWith($"<m:oMath xmlns:m=\"{M}\">", StringComparison.Ordinal), s);
        Assert.Equal(1, CountOf(s, "xmlns"));
        Assert.NotContains("xmlns:w", s);

        // default-namespace input is re-prefixed as m:
        var dflt = XElement.Parse($"<oMath xmlns=\"{M}\"><r><t>z</t></r></oMath>");
        var s2 = OmmlConverter.Serialize(dflt, display: false);
        Assert.True(s2.StartsWith("<m:oMath xmlns:m=", StringComparison.Ordinal), s2);
        Assert.Contains("<m:t>z</m:t>", s2);
    }

    private static int CountOf(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static string Pandoc => Path.Combine(TestEnv.Root, "tools", "pandoc", "pandoc.exe");

    [Test]
    public static void Convert_Frac_WithPandoc()
    {
        if (!File.Exists(Pandoc)) { Console.WriteLine("  (pandoc not found: skipped)"); return; }
        var conv = new OmmlConverter(Pandoc, TestEnv.TempDir("pandoc_frac"));
        var om = conv.Convert("\\frac{a}{b}");
        Assert.Equal(XName.Get("oMath", M), om.Name);
        Assert.Equal(1, om.Descendants(XName.Get("f", M)).Count(), "one m:f");
        OmmlConverter.Validate(om);
        Assert.Equal(0, Directory.GetFiles(TestEnv.Root + @"\out\unit_tmp\pandoc_frac").Length, "temporary docx deleted");
    }

    [Test]
    public static void Convert_TrailingControlSpace_WithPandoc()
    {
        if (!File.Exists(Pandoc)) { Console.WriteLine("  (pandoc not found: skipped)"); return; }
        var conv = new OmmlConverter(Pandoc, TestEnv.TempDir("pandoc_ctlspace"));
        // MathType writes a trailing non-breaking space as "\ "; trimmed to "x \" it escaped the closing '$'
        var om = conv.Convert("x \\ ");
        Assert.Contains("x", string.Concat(om.Descendants(XName.Get("t", M)).Select(t => t.Value)), "equation text");
    }

    [Test]
    public static void Convert_Garbage_Throws()
    {
        if (!File.Exists(Pandoc)) { Console.WriteLine("  (pandoc not found: skipped)"); return; }
        var conv = new OmmlConverter(Pandoc, TestEnv.TempDir("pandoc_bad"));
        Assert.True(Throws(() => conv.Convert("\\unknowncommandxyz{")), "unparseable LaTeX must throw");
        Assert.True(Throws(() => conv.Convert("   ")), "empty LaTeX must throw");
        Assert.True(Throws(() => conv.Convert(new string('x', 4001))), "too long LaTeX must throw");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }
}
