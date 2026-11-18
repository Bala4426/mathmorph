using System.Xml.Linq;
using MathMorph.Engine;

namespace MathMorph.Tests;

public static class XmlSplicerTests
{
    private const string A2 = "<a id=\"2\" t=\"x>y\" u='q\"/>'><b/><!-- <a> </a> <a/> --><![CDATA[</a><a>]]></a>";
    private const string A3 = "<a id=\"3\"/>";
    private const string A1 = "<a id=\"1\">\r\n    " + A2 + "\r\n    " + A3 + "\r\n  </a>";
    private const string A4 = "<a id='4' q=\"it's &gt; ok\"><?pi <a>?>text<c\tk=\"1\"\n/></a>";
    private const string A5 = "<a\n  id=\"5\"\r\n  >\t<a id=\"6\">x</a ></a>";
    private const string Text = "<?xml version=\"1.0\"?>\r\n<root>\r\n  " + A1 + "\r\n  " + A4 + "\n\t" + A5 + "\r\n</root>";

    [Test]
    public static void SpanOf_FindsExactSpans_NestedSelfClosingQuotedCommentsCdata()
    {
        var doc = SafeXml.Parse(Text, lineInfo: true);
        var sp = new XmlSplicer(Text);
        var expected = new Dictionary<string, string> { ["1"] = A1, ["2"] = A2, ["3"] = A3, ["4"] = A4, ["5"] = A5, ["6"] = "<a id=\"6\">x</a >" };
        foreach (var a in doc.Descendants("a"))
        {
            var id = (string)a.Attribute("id")!;
            var (s, e) = sp.SpanOf(a);
            Assert.Equal(expected[id], Text[s..e], "span of a#" + id);
        }
        var c = doc.Descendants("c").Single();
        var (cs, ce) = sp.SpanOf(c);
        Assert.Equal("<c\tk=\"1\"\n/>", Text[cs..ce], "self-closing with whitespace in tag");
        var root = sp.SpanOf(doc.Root!);
        Assert.Equal(Text.IndexOf("<root>", StringComparison.Ordinal), root.Start);
        Assert.Equal(Text.Length, root.End);
    }

    [Test]
    public static void SpanOf_WrongNameThrows()
    {
        var sp = new XmlSplicer("<x><w:rPr/><w:r>a</w:r></x>");
        Assert.Throws<InvalidOperationException>(() => sp.SpanOf(1, 5, "w:r"), "prefix of a longer name");
        Assert.Throws<InvalidOperationException>(() => sp.SpanOf(1, 5, "w:t"), "different name");
        Assert.Throws<InvalidOperationException>(() => sp.SpanOf(1, 6, "w:rPr"), "column not after '<'");
        Assert.Throws<InvalidOperationException>(() => sp.SpanOf(2, 1, "x"), "line out of range");
        Assert.Throws<InvalidOperationException>(() => sp.SpanOf(0, 1, "x"), "line zero");
        var (s, e) = sp.SpanOf(1, 5, "w:rPr");
        Assert.Equal((3, 11), (s, e));
        var (s2, e2) = sp.SpanOf(1, 13, "w:r");
        Assert.Equal("<w:r>a</w:r>", sp.Text[s2..e2]);
    }

    [Test]
    public static void SpanOf_UnterminatedThrows()
    {
        var sp = new XmlSplicer("<a><b>text");
        Assert.Throws<InvalidOperationException>(() => sp.SpanOf(1, 2, "a"));
    }

    [Test]
    public static void SpanOf_LineCountingMatchesXmlReader_CrLfAndLoneCr()
    {
        // XmlReader treats \r\n, \r and \n as line breaks; the splicer must count identically
        var text = "<r>\r<a/>\r\n<a/>\n<a/>\r\r\n\n<a k=\"v\r\nw\"/>\r<a/></r>";
        var doc = SafeXml.Parse(text, lineInfo: true);
        var sp = new XmlSplicer(text);
        foreach (var a in doc.Root!.Elements("a"))
        {
            var (s, e) = sp.SpanOf(a);
            Assert.True(text[s..e].StartsWith("<a") && text[s..e].EndsWith("/>"), "span " + text[s..e]);
        }
    }

    [Test]
    public static void Apply_ReplacesOnlyGivenSpans_UnsortedInput()
    {
        const string t = "0123456789";
        var r = XmlSplicer.Apply(t, new[] { (7, 9, "X"), (1, 3, "ab"), (3, 3, "+"), (5, 5, "") });
        Assert.Equal("0ab+3456X9", r);
        Assert.Equal(t, XmlSplicer.Apply(t, Array.Empty<(int, int, string)>()), "no edits");
        Assert.Equal("Z", XmlSplicer.Apply(t, new[] { (0, 10, "Z") }), "whole text");
    }

    [Test]
    public static void Apply_RejectsOverlappingEdits()
    {
        Assert.Throws<InvalidOperationException>(() => XmlSplicer.Apply("0123456789", new[] { (1, 5, "a"), (4, 6, "b") }));
        Assert.Throws<InvalidOperationException>(() => XmlSplicer.Apply("0123456789", new[] { (2, 8, "a"), (3, 4, "b") }), "nested");
        Assert.Throws<InvalidOperationException>(() => XmlSplicer.Apply("0123456789", new[] { (2, 4, "a"), (2, 4, "b") }), "identical");
    }

    [Test]
    public static void Apply_WithSpanOf_LeavesUntouchedTextByteIdentical()
    {
        var doc = SafeXml.Parse(Text, lineInfo: true);
        var sp = new XmlSplicer(Text);
        var a3 = doc.Descendants("a").Single(a => (string?)a.Attribute("id") == "3");
        var a6 = doc.Descendants("a").Single(a => (string?)a.Attribute("id") == "6");
        var (s3, e3) = sp.SpanOf(a3);
        var (s6, e6) = sp.SpanOf(a6);
        var r = XmlSplicer.Apply(Text, new[] { (s6, e6, "<n6/>"), (s3, e3, "<n3/>") });
        Assert.Equal(Text[..s3] + "<n3/>" + Text[e3..s6] + "<n6/>" + Text[e6..], r);
        var back = XDocument.Parse(r);
        Assert.Equal(1, back.Descendants("n3").Count());
    }
}
