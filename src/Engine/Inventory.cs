using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MathMorph.Contracts;

namespace MathMorph.Engine;

/// <summary>Finds every picture element in the package and assigns exactly one category (Gate 0).
/// Detection rules are general and size/format/position based; nothing is tuned to a specific file.</summary>
public static class Inventory
{
    public const long OneInchEmu = 914400;
    private static readonly Regex LogoWords = new(@"\b(?:logo|figure|chart|photo|diagram|graph|screenshot|banner)\b|\bfig\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed class PartScan
    {
        public required string Part { get; init; }
        public required string Container { get; init; }
        public required XDocument Doc { get; init; }
        public required string Text { get; init; }
        public required System.Text.Encoding Encoding { get; init; }
        public required byte[] Preamble { get; init; }
        public required Dictionary<string, Relationship> Rels { get; init; }
        public Dictionary<XElement, XElement> PictureElements { get; } = new(); // picture element -> run
        public List<(PictureInfo Info, XElement Element)> Items { get; } = new();
    }

    public sealed class Result
    {
        public required DocumentInventory Inventory { get; init; }
        public List<PartScan> Parts { get; } = new();
    }

    public static Result Scan(SafePackage pkg, string fileName)
    {
        var main = Relationships.MainDocumentPart(pkg) ?? throw new InvalidDataException("package has no main document part");
        var mainEntry = pkg.Get(main) ?? throw new InvalidDataException("main document part is missing: " + main);
        var mainRelsName = main;
        main = mainEntry.Name; // canonical zip entry name
        var inv = new DocumentInventory { File = fileName, MainPart = main };
        var res = new Result { Inventory = inv };
        var mainRels = Relationships.Load(pkg, mainRelsName);
        if (mainRels.Count == 0 && mainRelsName != main) mainRels = Relationships.Load(pkg, main);
        var parts = new List<(string Part, string Container)> { (main, "body") };
        foreach (var r in mainRels.Values.Where(r => !r.External))
        {
            var t = r.Type[(r.Type.LastIndexOf('/') + 1)..];
            string? c = t switch
            {
                "header" => "header", "footer" => "footer", "footnotes" => "footnote",
                "endnotes" => "endnote", "comments" => "comments", _ => null
            };
            if (c != null && pkg.Get(r.Target) is { } pe && !parts.Any(p => p.Part == pe.Name)) parts.Add((pe.Name, c));
        }
        foreach (var (part, container) in parts)
        {
            var (text, enc, pre) = SafeXml.Decode(pkg.Get(part)!.Data);
            var doc = SafeXml.Parse(text, lineInfo: true);
            var scan = new PartScan
            {
                Part = part, Container = container, Doc = doc, Text = text, Encoding = enc, Preamble = pre,
                Rels = part == main ? mainRels : RelsFor(pkg, mainRels, part)
            };
            ScanPart(pkg, scan, inv);
            res.Parts.Add(scan);
        }
        return res;
    }

    private static Dictionary<string, Relationship> RelsFor(SafePackage pkg, Dictionary<string, Relationship> mainRels, string part)
    {
        var rels = Relationships.Load(pkg, part);
        if (rels.Count > 0) return rels;
        // relationship file named after the target spelling (may differ in case/encoding from the zip item name)
        var alt = mainRels.Values.FirstOrDefault(r => !r.External && pkg.Get(r.Target)?.Name == part)?.Target;
        return alt != null && alt != part ? Relationships.Load(pkg, alt) : rels;
    }

    private static bool IsPictureElement(XElement e) =>
        e.Name == Ns.W + "drawing" || e.Name == Ns.W + "pict" || e.Name == Ns.W + "object";

    /// <summary>Descendants that belong to this element itself, not to nested text-box content.</summary>
    public static IEnumerable<XElement> OwnDescendants(XElement e)
    {
        foreach (var c in e.Elements())
        {
            if (c.Name == Ns.W + "txbxContent") continue;
            yield return c;
            foreach (var d in OwnDescendants(c)) yield return d;
        }
    }

    private static void ScanPart(SafePackage pkg, PartScan scan, DocumentInventory inv)
    {
        var root = scan.Doc.Root!;
        var paraIndex = new Dictionary<XElement, int>();
        int pi = 0;
        foreach (var p in root.Descendants(Ns.W + "p")) paraIndex[p] = pi++;
        inv.NativeOMath += root.Descendants(Ns.M + "oMath").Count();
        inv.NativeOMathPara += root.Descendants(Ns.M + "oMathPara").Count();

        int ordinal = 0;
        foreach (var el in root.Descendants().Where(IsPictureElement).ToList())
        {
            // pictures nested in an OLE object are its preview; handled by the w:object element
            if (el.Name != Ns.W + "object" && el.Ancestors(Ns.W + "object").Any()) continue;
            string? rid = null;
            PictureKind kind;
            long cx = 0, cy = 0;
            string name = "", descr = "";
            if (el.Name == Ns.W + "drawing")
            {
                var blip = OwnDescendants(el).FirstOrDefault(d => d.Name == Ns.A + "blip");
                if (blip == null) continue; // shape / text box / chart: not a picture
                rid = (string?)blip.Attribute(Ns.R + "embed") ?? (string?)blip.Attribute(Ns.R + "link");
                var holder = el.Elements().FirstOrDefault(x => x.Name == Ns.WP + "inline" || x.Name == Ns.WP + "anchor");
                kind = holder?.Name == Ns.WP + "anchor" ? PictureKind.Anchor : PictureKind.Inline;
                var ext = holder?.Element(Ns.WP + "extent");
                cx = ParseLong((string?)ext?.Attribute("cx"));
                cy = ParseLong((string?)ext?.Attribute("cy"));
                var docPr = holder?.Element(Ns.WP + "docPr");
                name = (string?)docPr?.Attribute("name") ?? "";
                descr = ((string?)docPr?.Attribute("descr") ?? "") + " " + ((string?)docPr?.Attribute("title") ?? "");
            }
            else
            {
                var img = OwnDescendants(el).FirstOrDefault(d => d.Name == Ns.V + "imagedata");
                if (img == null && el.Name == Ns.W + "pict")
                {
                    // VML without image data (text box, line) is not a picture, unless it holds a DrawingML blip
                    continue;
                }
                rid = (string?)img?.Attribute(Ns.R + "id") ?? (string?)img?.Attribute(Ns.R + "pict") ?? (string?)img?.Attribute(Ns.O + "relid");
                var shape = img?.Parent;
                var style = (string?)shape?.Attribute("style") ?? "";
                (cx, cy) = VmlSize(style);
                if (el.Name == Ns.W + "object")
                {
                    kind = PictureKind.Ole;
                    if (cy == 0) cy = ParseLong((string?)el.Attribute(Ns.W + "dyaOrig")) * 635;
                    if (cx == 0) cx = ParseLong((string?)el.Attribute(Ns.W + "dxaOrig")) * 635;
                }
                else
                {
                    kind = Regex.IsMatch(style, @"position\s*:\s*absolute", RegexOptions.IgnoreCase) ? PictureKind.VmlFloating : PictureKind.Vml;
                }
                name = (string?)shape?.Attribute("id") ?? "";
                descr = ((string?)shape?.Attribute("alt") ?? "") + " " + ((string?)img?.Attribute(Ns.O + "title") ?? "");
            }

            var para = el.Ancestors(Ns.W + "p").FirstOrDefault();
            var run = el.Ancestors(Ns.W + "r").FirstOrDefault();
            var alt = AltContentBranch.None;
            if (el.Ancestors(Ns.MC + "Fallback").Any()) alt = AltContentBranch.Fallback;
            else if (el.Ancestors(Ns.MC + "Choice").Any()) alt = AltContentBranch.Choice;

            string container = scan.Container;
            if (container == "body")
            {
                if (el.Ancestors(Ns.W + "txbxContent").Any()) container = "textbox";
                else if (el.Ancestors(Ns.W + "tc").Any()) container = "table";
            }

            Relationship? rel = null;
            if (rid != null) scan.Rels.TryGetValue(rid, out rel);
            string? progId = null, oleTarget = null;
            if (el.Name == Ns.W + "object" && OwnDescendants(el).FirstOrDefault(x => x.Name == Ns.O + "OLEObject") is { } ole)
            {
                progId = (string?)ole.Attribute("ProgID");
                var oleRid = (string?)ole.Attribute(Ns.R + "id");
                if (oleRid != null && scan.Rels.TryGetValue(oleRid, out var oleRel) && !oleRel.External) oleTarget = oleRel.Target;
            }
            var info = new PictureInfo
            {
                Part = scan.Part, Container = container, ParaIndex = para != null ? paraIndex[para] : -1,
                Ordinal = ordinal++, Kind = kind, RelId = rid, Target = rel?.Target, ExternalLink = rel?.External ?? false,
                ExtentCx = cx, ExtentCy = cy, AltContent = alt, DocPrName = name, DocPrDescr = descr.Trim(),
                ParagraphHasOtherContent = para != null && ParagraphHasOtherContent(para, el),
                Run = BuildLocator(el, run, para),
                OleProgId = progId, OleTarget = oleTarget,
            };
            var entry = rel != null && !rel.External ? pkg.Get(rel.Target) : null;
            if (entry != null)
            {
                info.Format = DetectFormat(entry.Data, rel!.Target);
                info.Sha256 = Convert.ToHexString(SHA256.HashData(entry.Data)).ToLowerInvariant();
            }
            Classify(info, el, entry != null);
            scan.Items.Add((info, el));
            inv.Pictures.Add(info);
        }
    }

    /// <summary>Rules applied in fixed priority order. Exactly one category per picture.</summary>
    public static void Classify(PictureInfo p, XElement? el, bool hasImage)
    {
        if (el != null && el.Ancestors(Ns.M + "oMath").Any()) { Set(p, Category.Native, "picture inside native equation"); return; }
        if (p.Container is "header" or "footer" or "footnote" or "endnote" or "comments") { Set(p, Category.Excluded, "container:" + p.Container); return; }
        if (p.AltContent == AltContentBranch.Fallback) { Set(p, Category.Excluded, "mc:Fallback branch (left unchanged)"); return; }
        if (p.Kind is PictureKind.Anchor or PictureKind.VmlFloating) { Set(p, Category.Excluded, "anchored/floating"); return; }
        if (!hasImage) { Set(p, Category.Excluded, p.ExternalLink ? "no_image: external link" : "no_image: missing image part"); return; }
        if (p.Format is "emf" or "wmf") { Set(p, Category.UnsupportedFormat, p.Format.ToUpperInvariant() + " (no PNG preview in package)"); return; }
        if (p.ExtentCy > OneInchEmu) { Set(p, Category.Excluded, "taller than 1 inch"); return; }
        if (p.Format is not ("png" or "jpeg")) { Set(p, Category.UnsupportedFormat, "format " + p.Format); return; }
        if (LogoWords.IsMatch(p.DocPrName + " " + p.DocPrDescr)) { Set(p, Category.Excluded, "looks like logo/figure (name/alt text)"); return; }
        Set(p, p.Kind == PictureKind.Ole ? Category.OlePreview : Category.Candidate, p.Kind == PictureKind.Ole ? "OLE equation preview" : "candidate");
    }

    private static void Set(PictureInfo p, Category c, string reason) { p.Category = c; p.Reason = reason; }

    public static bool IsNoImage(PictureInfo p) => p.Reason.StartsWith("no_image", StringComparison.Ordinal);

    private static RunLocator? BuildLocator(XElement pic, XElement? run, XElement? para)
    {
        if (run == null || para == null) return null;
        // run is the nearest w:r ancestor, i.e. the run that directly owns the picture
        var li = (IXmlLineInfo)run;
        if (!li.HasLineInfo()) return null;
        var content = run.Elements().Where(e => e.Name != Ns.W + "rPr").ToList();
        bool runAc = false;
        string? requires = null;
        bool only;
        if (content.Count == 1 && content[0] == pic) only = true;
        else if (content.Count == 1 && content[0].Name == Ns.MC + "AlternateContent")
        {
            var choice = content[0].Elements(Ns.MC + "Choice").ToList();
            only = choice.Count == 1 && choice[0].Elements().Count() == 1 && choice[0].Elements().First() == pic;
            runAc = only;
            requires = (string?)choice.FirstOrDefault()?.Attribute("Requires");
        }
        else only = false;
        var parent = run.Parent;
        bool parentIsP = parent?.Name == Ns.W + "p";
        bool paraOnly = !ParagraphHasOtherContent(para, pic);
        var qn = QualifiedName(run);
        return new RunLocator(li.LineNumber, li.LinePosition, qn, only, parentIsP, paraOnly, runAc, requires);
    }

    public static string QualifiedName(XElement e)
    {
        var prefix = e.GetPrefixOfNamespace(e.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? e.Name.LocalName : prefix + ":" + e.Name.LocalName;
    }

    /// <summary>True when the paragraph holds visible content other than <paramref name="pic"/>.</summary>
    public static bool ParagraphHasOtherContent(XElement para, XElement pic)
    {
        foreach (var d in OwnDescendants(para))
        {
            if (d == pic || d.Ancestors().Contains(pic)) continue;
            if (d.Name == Ns.W + "t" || d.Name == Ns.W + "delText" || d.Name == Ns.W + "instrText")
            {
                if (!string.IsNullOrWhiteSpace(d.Value)) return true;
            }
            else if (d.Name == Ns.W + "sym" || d.Name == Ns.W + "tab" || d.Name == Ns.W + "ptab" || d.Name == Ns.W + "br" ||
                     d.Name == Ns.M + "oMath" || IsPictureElement(d) || d.Name == Ns.W + "fldChar" || d.Name == Ns.W + "footnoteReference" ||
                     d.Name == Ns.W + "endnoteReference")
            {
                // a picture in mc:Fallback mirrors a Choice copy of the same object; it is not extra content
                if (IsPictureElement(d) && d.Ancestors(Ns.MC + "Fallback").Any() &&
                    pic.Ancestors(Ns.MC + "AlternateContent").FirstOrDefault() is { } ac && d.Ancestors().Contains(ac)) continue;
                return true;
            }
        }
        return false;
    }

    public static string DetectFormat(byte[] d, string name)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpeg";
        if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return "gif";
        if (d.Length >= 2 && d[0] == 'B' && d[1] == 'M') return "bmp";
        if (d.Length >= 44 && d[0] == 1 && d[1] == 0 && d[2] == 0 && d[3] == 0 && d[40] == 0x20 && d[41] == 0x45 && d[42] == 0x4D && d[43] == 0x46) return "emf";
        if (d.Length >= 4 && d[0] == 0xD7 && d[1] == 0xCD && d[2] == 0xC6 && d[3] == 0x9A) return "wmf";
        if (d.Length >= 4 && (d[0] == 1 || d[0] == 2) && d[1] == 0 && d[2] == 9 && d[3] == 0) return "wmf";
        if (d.Length >= 4 && ((d[0] == 'I' && d[1] == 'I') || (d[0] == 'M' && d[1] == 'M'))) return "tiff";
        var ext = Path.GetExtension(name).ToLowerInvariant().TrimStart('.');
        return ext switch { "jpg" or "jpeg" => "jpeg-unverified", "png" => "png-unverified", "emf" => "emf", "wmf" => "wmf", "svg" => "svg", "" => "unknown", _ => ext };
    }

    private static long ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static (long Cx, long Cy) VmlSize(string style)
    {
        long Get(string key)
        {
            var m = Regex.Match(style, @"(?:^|;)\s*" + key + @"\s*:\s*([0-9.]+)\s*(pt|in|px|cm|mm|pc|emu)?", RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
            double f = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "pt" => 12700, "in" => 914400, "cm" => 360000, "mm" => 36000, "pc" => 152400, "emu" => 1, "px" => 9525, _ => 9525
            };
            return (long)Math.Round(v * f);
        }
        return (Get("width"), Get("height"));
    }
}
