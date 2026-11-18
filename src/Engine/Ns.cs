using System.Xml.Linq;

namespace MathMorph.Engine;

public static class Ns
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    public static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    public static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    public static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string RelOfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string RelStrictOfficeDocument = "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument";
}
