using System.Text;
using System.Xml;
using MathMorph.Engine;

namespace MathMorph.Tests;

public static class SafeXmlTests
{
    [Test]
    public static void Parse_RejectsInternalEntityBomb()
    {
        const string bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">" +
                            "<!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]><lolz>&lol3;</lolz>";
        Assert.Throws<XmlException>(() => SafeXml.Parse(bomb));
        Assert.Throws<XmlException>(() => SafeXml.Parse(Docx.U8(bomb)));
    }

    [Test]
    public static void Parse_RejectsExternalSystemEntity()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><foo>&xxe;</foo>";
        Assert.Throws<XmlException>(() => SafeXml.Parse(xxe));
        const string extDtd = "<?xml version=\"1.0\"?><!DOCTYPE foo SYSTEM \"http://127.0.0.1:1/x.dtd\"><foo/>";
        Assert.Throws<XmlException>(() => SafeXml.Parse(extDtd));
    }

    [Test]
    public static void Parse_KeepsWhitespaceAndLineInfo()
    {
        var doc = SafeXml.Parse("<a>\n  <b> x </b>\n</a>", lineInfo: true);
        var b = doc.Root!.Element("b")!;
        Assert.Equal(" x ", b.Value);
        var li = (IXmlLineInfo)b;
        Assert.Equal(2, li.LineNumber, "line");
        Assert.Equal(4, li.LinePosition, "column of the name");
    }

    [Test]
    public static void Decode_Encode_Utf8Bom_RoundTripsByteIdentical()
    {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<a>\u00e4\u03c0\u20ac\U0001F600</a>";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var (t, enc, pre) = SafeXml.Decode(bytes);
        Assert.Equal(text, t, "decoded text has no BOM char");
        Assert.BytesEqual(bytes, SafeXml.Encode(t, enc, pre));
        Assert.Equal("\u00e4\u03c0\u20ac\U0001F600", SafeXml.Parse(bytes).Root!.Value);
    }

    [Test]
    public static void Decode_Encode_Utf8NoBom_RoundTripsByteIdentical()
    {
        var bytes = Docx.U8("<?xml version='1.0' encoding='utf-8'?><a>\u00e4</a>");
        var (t, enc, pre) = SafeXml.Decode(bytes);
        Assert.Equal(0, pre.Length, "no preamble");
        Assert.BytesEqual(bytes, SafeXml.Encode(t, enc, pre));
    }

    [Test]
    public static void Decode_Encode_Utf16LeBom_RoundTripsByteIdentical()
    {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><a b=\"\u00e4\">\u20ac\U0001F600</a>";
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(new UnicodeEncoding(false, false).GetBytes(text)).ToArray();
        var (t, enc, pre) = SafeXml.Decode(bytes);
        Assert.Equal(text, t);
        Assert.BytesEqual(bytes, SafeXml.Encode(t, enc, pre));
        Assert.Equal("\u20ac\U0001F600", SafeXml.Parse(bytes).Root!.Value, "parses with encoding=UTF-16 declaration");
    }

    [Test]
    public static void Decode_Encode_Utf16BeBom_RoundTripsByteIdentical()
    {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><a>\u20ac</a>";
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(new UnicodeEncoding(true, false).GetBytes(text)).ToArray();
        var (t, enc, pre) = SafeXml.Decode(bytes);
        Assert.Equal(text, t);
        Assert.BytesEqual(bytes, SafeXml.Encode(t, enc, pre));
    }

    [Test]
    public static void Decode_RejectsNonUtf8DeclaredEncoding()
    {
        foreach (var e in new[] { "ISO-8859-1", "windows-1252", "Shift_JIS" })
        {
            var bytes = Encoding.ASCII.GetBytes($"<?xml version=\"1.0\" encoding=\"{e}\"?><a>x</a>");
            Assert.Throws<NotSupportedException>(() => SafeXml.Decode(bytes), e);
        }
        SafeXml.Decode(Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"UTF8\"?><a/>"));
        SafeXml.Decode(Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><a/>"));
    }

    [Test]
    public static void Decode_RejectsInvalidUtf8()
    {
        var bytes = Docx.U8("<a>x</a>");
        bytes[3] = 0xC3; // truncated two-byte sequence followed by '<'
        Assert.Throws<DecoderFallbackException>(() => SafeXml.Decode(bytes));
    }
}
