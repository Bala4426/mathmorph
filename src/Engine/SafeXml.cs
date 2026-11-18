using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MathMorph.Engine;

/// <summary>Hardened XML loading: no DTDs, no external resolution, bounded size.</summary>
public static class SafeXml
{
    public static XmlReaderSettings Settings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 400_000_000,
        IgnoreWhitespace = false,
        CloseInput = true,
    };

    /// <summary>Decodes part bytes; returns the text plus how to re-encode it identically.</summary>
    public static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return (new UTF8Encoding(false, true).GetString(data, 3, data.Length - 3), new UTF8Encoding(false, true), new byte[] { 0xEF, 0xBB, 0xBF });
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return (new UnicodeEncoding(false, false, true).GetString(data, 2, data.Length - 2), new UnicodeEncoding(false, false, true), new byte[] { 0xFF, 0xFE });
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return (new UnicodeEncoding(true, false, true).GetString(data, 2, data.Length - 2), new UnicodeEncoding(true, false, true), new byte[] { 0xFE, 0xFF });
        // no BOM: XML spec default is UTF-8; reject any other declared encoding
        var head = Encoding.ASCII.GetString(data, 0, Math.Min(200, data.Length));
        var m = System.Text.RegularExpressions.Regex.Match(head, "encoding\\s*=\\s*[\"']([^\"']+)[\"']");
        if (m.Success && !m.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase) &&
            !m.Groups[1].Value.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("unsupported XML encoding " + m.Groups[1].Value);
        return (new UTF8Encoding(false, true).GetString(data), new UTF8Encoding(false, true), Array.Empty<byte>());
    }

    public static byte[] Encode(string text, Encoding enc, byte[] preamble)
    {
        var body = enc.GetBytes(text);
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    public static XDocument Parse(string text, bool lineInfo = true)
    {
        using var sr = new StringReader(text);
        using var xr = XmlReader.Create(sr, Settings);
        return XDocument.Load(xr, (lineInfo ? LoadOptions.SetLineInfo : LoadOptions.None) | LoadOptions.PreserveWhitespace);
    }

    public static XDocument Parse(byte[] data, bool lineInfo = false) => Parse(Decode(data).Text, lineInfo);
}
