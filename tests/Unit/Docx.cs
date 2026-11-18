using System.IO.Compression;
using System.Text;
using MathMorph.Contracts;
using MathMorph.Engine;

namespace MathMorph.Tests;

/// <summary>Builds minimal .docx packages in memory from hand-written XML.</summary>
public static class Docx
{
    public const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string PicNs = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public const string McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public const string VNs = "urn:schemas-microsoft-com:vml";
    public const string ONs = "urn:schemas-microsoft-com:office:office";
    public const string WpsNs = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    public const string MNs = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    public const string PrNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    public const string NsDecl =
        "xmlns:w=\"" + WNs + "\" xmlns:r=\"" + RNs + "\" xmlns:wp=\"" + WpNs + "\" xmlns:a=\"" + ANs +
        "\" xmlns:pic=\"" + PicNs + "\" xmlns:mc=\"" + McNs + "\" xmlns:v=\"" + VNs + "\" xmlns:o=\"" + ONs +
        "\" xmlns:wps=\"" + WpsNs + "\"";

    public static string Document(string body, string nl = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + nl +
        "<w:document " + NsDecl + ">" + nl + "<w:body>" + nl + body + nl + "<w:sectPr/></w:body>" + nl + "</w:document>";

    public static string Part(string rootName, string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><" + rootName + " " + NsDecl + ">" + body + "</" + rootName + ">";

    public static string Graphic(string rid) =>
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr>" +
        "<pic:cNvPr id=\"0\" name=\"img\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"" + rid + "\"/>" +
        "<a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1\" cy=\"1\"/></a:xfrm>" +
        "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic>";

    public static string Inline(string rid, long cx = 457200, long cy = 228600, string name = "Picture 1", string descr = "", int id = 1) =>
        $"<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>" +
        $"<wp:docPr id=\"{id}\" name=\"{name}\" descr=\"{descr}\"/>{Graphic(rid)}</wp:inline></w:drawing>";

    public static string Anchor(string rid, long cx = 457200, long cy = 228600, string name = "Picture 9") =>
        "<w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"1\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>" +
        "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>" +
        $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:wrapNone/><wp:docPr id=\"9\" name=\"{name}\"/>{Graphic(rid)}</wp:anchor></w:drawing>";

    /// <summary>A DrawingML text box (no blip of its own) holding paragraphs.</summary>
    public static string TextBox(string innerParagraphs) =>
        "<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"1828800\" cy=\"914400\"/><wp:docPr id=\"5\" name=\"Text Box 5\"/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr/>" +
        "<wps:txbx><w:txbxContent>" + innerParagraphs + "</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing>";

    public static string Vml(string rid, string style = "width:30pt;height:15pt", string id = "_x0000_i1025") =>
        $"<w:pict><v:shape id=\"{id}\" type=\"#_x0000_t75\" style=\"{style}\"><v:imagedata r:id=\"{rid}\" o:title=\"\"/></v:shape></w:pict>";

    public static string Ole(string rid, string style = "width:30pt;height:15pt") =>
        "<w:object w:dxaOrig=\"600\" w:dyaOrig=\"300\">" +
        $"<v:shape id=\"_x0000_i1030\" type=\"#_x0000_t75\" style=\"{style}\" o:ole=\"\"><v:imagedata r:id=\"{rid}\" o:title=\"\"/></v:shape>" +
        "<o:OLEObject Type=\"Embed\" ProgID=\"Equation.3\" ShapeID=\"_x0000_i1030\" DrawAspect=\"Content\" ObjectID=\"_1\" r:id=\"rIdOle\"/></w:object>";

    public static string R(string inner) => "<w:r>" + inner + "</w:r>";
    public static string P(string inner) => "<w:p>" + inner + "</w:p>";
    public static string T(string text) => "<w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r>";

    public static string Rels(params (string Id, string Type, string Target)[] rels)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + PrNs + "\">");
        foreach (var (id, type, target) in rels)
        {
            var t = type.Contains("://") ? type : RelBase + type;
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{t}\" Target=\"{target}\"/>");
        }
        return sb.Append("</Relationships>").ToString();
    }

    // ------------------------------------------------------------- image payloads (magic numbers only)
    public static byte[] Png => Pad(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 64, 1);
    public static byte[] Jpeg => Pad(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F' }, 64, 2);
    public static byte[] Gif => Pad(Encoding.ASCII.GetBytes("GIF89a"), 64, 3);
    public static byte[] Bmp => Pad(Encoding.ASCII.GetBytes("BM"), 64, 4);
    public static byte[] Tiff => Pad(new byte[] { (byte)'I', (byte)'I', 42, 0 }, 64, 5);
    public static byte[] WmfPlaceable => Pad(new byte[] { 0xD7, 0xCD, 0xC6, 0x9A }, 64, 6);
    public static byte[] WmfStandard => Pad(new byte[] { 1, 0, 9, 0 }, 64, 7);
    public static byte[] Emf
    {
        get
        {
            var d = new byte[88];
            d[0] = 1;
            d[40] = 0x20; d[41] = 0x45; d[42] = 0x4D; d[43] = 0x46;
            return d;
        }
    }

    private static byte[] Pad(byte[] head, int len, byte fill)
    {
        var d = new byte[len];
        Array.Fill(d, fill);
        head.CopyTo(d, 0);
        return d;
    }

    /// <summary>Standard media referenced by rId1..rId5 in <see cref="StandardRels"/>.</summary>
    public static (string, byte[])[] StandardMedia => new[]
    {
        ("word/media/image1.png", Png),
        ("word/media/image2.jpeg", Jpeg),
        ("word/media/image3.emf", Emf),
        ("word/media/image4.wmf", WmfPlaceable),
        ("word/media/image5.gif", Gif),
        ("word/embeddings/oleObject1.bin", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
    };

    public static (string, string, string)[] StandardRels => new[]
    {
        ("rId1", "image", "media/image1.png"),
        ("rId2", "image", "media/image2.jpeg"),
        ("rId3", "image", "media/image3.emf"),
        ("rId4", "image", "media/image4.wmf"),
        ("rId5", "image", "media/image5.gif"),
        ("rIdOle", "oleObject", "embeddings/oleObject1.bin"),
    };

    public const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + PrNs + "\">" +
        "<Relationship Id=\"rId1\" Type=\"" + RelBase + "officeDocument\" Target=\"word/document.xml\"/></Relationships>";

    public const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";

    public static byte[] U8(string s) => new UTF8Encoding(false).GetBytes(s);

    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, level);
                e.LastWriteTime = new DateTimeOffset(2024, 1, 2, 3, 4, 6, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A complete minimal .docx: content types, root rels, document, document rels (standard + extra), media.</summary>
    public static byte[] Build(string documentXml, IEnumerable<(string, string, string)>? extraRels = null,
        IEnumerable<(string, byte[])>? extraParts = null, byte[]? documentBytes = null)
    {
        var rels = StandardRels.Concat(extraRels ?? Array.Empty<(string, string, string)>()).ToArray();
        var entries = new List<(string, byte[])>
        {
            ("[Content_Types].xml", U8(ContentTypes)),
            ("_rels/.rels", U8(RootRels)),
            ("word/document.xml", documentBytes ?? U8(documentXml)),
            ("word/_rels/document.xml.rels", U8(Rels(rels))),
        };
        entries.AddRange(StandardMedia);
        if (extraParts != null) entries.AddRange(extraParts);
        return Zip(entries);
    }

    public static SafePackage Open(byte[] zip, SafetyLimits? limits = null) =>
        SafePackage.Open(new MemoryStream(zip), limits ?? new SafetyLimits());

    public static Inventory.Result Scan(byte[] zip) => Inventory.Scan(Open(zip), "test.docx");

    public static Inventory.Result ScanBody(string body) => Scan(Build(Document(body)));
}
