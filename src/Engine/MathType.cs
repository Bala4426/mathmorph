using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MathMorph.Engine;

/// <summary>
/// Reads the exact equation stored in a MathType OLE object (ProgID Equation.DSMT*): the "Equation Native"
/// stream holds MTEF (MathType Equation Format) data. MTEF v5 is parsed into a small tree and written out
/// as LaTeX, which the existing LaTeX -> OMML converter turns into a Word equation. Nothing is recognised
/// from pictures, so the result is exact. Anything the converter does not understand is reported as a
/// problem and the item is left for review instead of being guessed.
/// </summary>
public static class MathType
{
    public static bool IsMathTypeProgId(string? progId) =>
        progId != null && progId.StartsWith("Equation.DSMT", StringComparison.OrdinalIgnoreCase);

    public sealed class Result
    {
        public string Latex { get; init; } = "";
        public List<string> Problems { get; } = new();
        public int Version { get; init; }
        public string Application { get; init; } = "";
    }

    /// <summary>OLE .bin -> MTEF bytes (strips the 28-byte EQNOLEFILEHDR).</summary>
    public static byte[] ExtractMtef(byte[] oleBin)
    {
        var native = CompoundFile.ReadStream(oleBin, "Equation Native") ?? throw new InvalidDataException("no 'Equation Native' stream");
        if (native.Length < 12) throw new InvalidDataException("Equation Native stream too short");
        int cbHdr = BinaryPrimitives.ReadUInt16LittleEndian(native);
        uint cbObject = BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(8));
        if (cbHdr < 12 || cbHdr > native.Length) throw new InvalidDataException("bad Equation Native header");
        long len = Math.Min(cbObject, (uint)(native.Length - cbHdr));
        return native.AsSpan(cbHdr, (int)len).ToArray();
    }

    public static Result Convert(byte[] oleBin) => ToLatex(ExtractMtef(oleBin));

    public static Result ToLatex(byte[] mtef)
    {
        if (mtef.Length < 5) throw new InvalidDataException("MTEF data too short");
        int version = mtef[0];
        if (version != 5) throw new NotSupportedException($"MTEF version {version} (only version 5, MathType 4 and later, is supported)");
        var p = new Parser(mtef);
        p.Pos = 5;
        var app = p.CString();
        p.Byte(); // equation options
        var top = p.ObjectList(topLevel: true);
        var w = new Writer();
        var latex = w.Body(top).Trim();
        var res = new Result { Latex = latex, Version = version, Application = app };
        res.Problems.AddRange(w.Problems.Distinct());
        if (latex.Length == 0) res.Problems.Add("equation is empty");
        return res;
    }

    private static readonly System.Xml.Linq.XNamespace MNs = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    /// <summary>
    /// Rewrites "lim/max/min/... with a limit underneath" (m:limLow) followed by its argument into Word's own
    /// structure for it, m:func(fName = m:limLow, e = argument). Word writes lim this way itself; it also keeps
    /// other renderers (LibreOffice) from pulling the operator into a neighbouring fraction.
    /// </summary>
    public static void WrapLimitFunctions(System.Xml.Linq.XElement omath)
    {
        foreach (var ll in omath.Descendants(MNs + "limLow").ToList())
        {
            if (ll.Parent == null || ll.Parent.Name == MNs + "fName") continue;
            var name = string.Concat(ll.Element(MNs + "e")?.Descendants(MNs + "t").Select(t => t.Value) ?? Array.Empty<string>()).Trim();
            if (!KnownFunctions.Contains(name)) continue;
            var next = ll.ElementsAfterSelf().FirstOrDefault();
            if (next == null) continue; // nothing to apply it to: leave as is (an empty argument would show a placeholder)
            next.Remove();
            ll.ReplaceWith(new System.Xml.Linq.XElement(MNs + "func",
                new System.Xml.Linq.XElement(MNs + "fName", new System.Xml.Linq.XElement(ll)),
                new System.Xml.Linq.XElement(MNs + "e", next)));
        }
    }

    // ---------------------------------------------------------------- model
    private abstract class Node { }
    private sealed class LineNode : Node { public List<Node> Items = new(); public bool Null; }
    private sealed class CharNode : Node { public int Typeface; public int Code; public bool FuncStart; public List<int> Embells = new(); }
    private sealed class TmplNode : Node { public int Selector; public int Variation; public List<Node> Objects = new(); }
    private sealed class PileNode : Node { public int HAlign; public List<Node> Lines = new(); }
    private sealed class MatrixNode : Node { public int Rows, Cols; public List<Node> Cells = new(); }
    private sealed class EmbellNode : Node { public int Type; }

    // ---------------------------------------------------------------- parser (MTEF v5)
    private sealed class Parser(byte[] d)
    {
        public int Pos;
        private int _depth;

        public int Byte()
        {
            if (Pos >= d.Length) throw new InvalidDataException("unexpected end of MTEF data");
            return d[Pos++];
        }
        public int UInt16() { int lo = Byte(); int hi = Byte(); return lo | (hi << 8); }
        public string CString()
        {
            int start = Pos;
            while (Byte() != 0) { }
            return Encoding.Latin1.GetString(d, start, Pos - start - 1);
        }
        private void Nudge(int opt)
        {
            if ((opt & 0x08) == 0) return;
            int dx = Byte(), dy = Byte();
            if (dx == 128 && dy == 128) { UInt16(); UInt16(); }
        }
        /// <summary>Ruler data attached to a LINE or PILE (option 0x02): [n_stops][type, offset16]... with no record tag.</summary>
        private void Ruler()
        {
            int n = Byte();
            for (int i = 0; i < n; i++) { Byte(); UInt16(); }
        }
        private void Dimensions()
        {
            int count = Byte();
            int done = 0;
            while (done < count)
            {
                int b = Byte();
                if ((b >> 4) == 0xF) done++;
                if (done < count && (b & 0xF) == 0xF) done++;
            }
        }

        /// <summary>Reads records up to and including END. Returns the content nodes.</summary>
        public List<Node> ObjectList(bool topLevel = false)
        {
            if (++_depth > 200) throw new InvalidDataException("MTEF nesting too deep");
            var list = new List<Node>();
            while (true)
            {
                if (topLevel && Pos >= d.Length) break;
                int tag = Byte();
                if (tag == 0) break;
                var n = Record(tag);
                if (n != null) list.Add(n);
            }
            _depth--;
            return list;
        }

        private Node? Record(int tag)
        {
            switch (tag)
            {
                case 1: // LINE
                {
                    int opt = Byte(); Nudge(opt);
                    if ((opt & 0x04) != 0) UInt16();      // line spacing
                    if ((opt & 0x02) != 0) Ruler();
                    var line = new LineNode { Null = (opt & 0x01) != 0 };
                    if (!line.Null) line.Items = ObjectList();
                    return line;
                }
                case 2: // CHAR
                {
                    int opt = Byte(); Nudge(opt);
                    var c = new CharNode { Typeface = Byte(), FuncStart = (opt & 0x02) != 0, Code = -1 };
                    if ((opt & 0x20) == 0) c.Code = UInt16();   // MTCode (Unicode / MathType private use)
                    if ((opt & 0x04) != 0) Byte();               // 8-bit font position
                    if ((opt & 0x10) != 0) UInt16();             // 16-bit font position
                    if ((opt & 0x01) != 0) c.Embells = ObjectList().OfType<EmbellNode>().Select(e => e.Type).ToList();
                    return c;
                }
                case 3: // TMPL
                {
                    int opt = Byte(); Nudge(opt);
                    var t = new TmplNode { Selector = Byte() };
                    int v = Byte();
                    if ((v & 0x80) != 0) v = (v & 0x7F) | (Byte() << 8);
                    t.Variation = v;
                    Byte(); // template-specific options
                    t.Objects = ObjectList();
                    return t;
                }
                case 4: // PILE
                {
                    int opt = Byte(); Nudge(opt);
                    var pile = new PileNode { HAlign = Byte() };
                    Byte(); // valign
                    if ((opt & 0x02) != 0) Ruler();
                    pile.Lines = ObjectList();
                    return pile;
                }
                case 5: // MATRIX
                {
                    int opt = Byte(); Nudge(opt);
                    Byte(); Byte(); Byte(); // valign, h_just, v_just
                    var m = new MatrixNode { Rows = Byte(), Cols = Byte() };
                    Pos += ((m.Rows + 1) * 2 + 7) / 8;  // row partition lines
                    Pos += ((m.Cols + 1) * 2 + 7) / 8;  // column partition lines
                    if (Pos > d.Length) throw new InvalidDataException("unexpected end of MTEF data");
                    m.Cells = ObjectList();
                    return m;
                }
                case 6: // EMBELL
                {
                    int opt = Byte(); Nudge(opt);
                    return new EmbellNode { Type = Byte() };
                }
                case 7: { int n = Byte(); for (int i = 0; i < n; i++) { Byte(); UInt16(); } return null; } // RULER
                case 8: Byte(); Byte(); return null;                       // FONT_STYLE_DEF
                case 9: // SIZE
                {
                    int lsize = Byte();
                    if (lsize == 101) UInt16();
                    else if (lsize == 100) { Byte(); UInt16(); }
                    else Byte();
                    return null;
                }
                case 10: case 11: case 12: case 13: case 14: return null; // FULL, SUB, SUB2, SYM, SUBSYM
                case 15: Byte(); return null;                              // COLOR
                case 16: // COLOR_DEF
                {
                    int opt = Byte();
                    int n = (opt & 0x01) != 0 ? 4 : 3;
                    for (int i = 0; i < n; i++) UInt16();
                    if ((opt & 0x04) != 0) CString();
                    return null;
                }
                case 17: Byte(); CString(); return null;                  // FONT_DEF
                case 18: // EQN_PREFS
                {
                    Byte();
                    Dimensions(); // sizes
                    Dimensions(); // spaces
                    int styles = Byte();
                    for (int i = 0; i < styles; i++) if (Byte() != 0) Byte();
                    return null;
                }
                case 19: CString(); return null;                           // ENCODING_DEF
                default:
                    if (tag >= 100) { int len = UInt16(); Pos += len; if (Pos > d.Length) throw new InvalidDataException("unexpected end of MTEF data"); return null; }
                    throw new InvalidDataException($"unknown MTEF record {tag} at offset {Pos - 1}");
            }
        }
    }

    // ---------------------------------------------------------------- LaTeX writer
    private const int FnText = 1, FnFunction = 2, FnVariable = 3, FnLcGreek = 4, FnUcGreek = 5, FnSymbol = 6, FnVector = 7,
        FnNumber = 8, FnMtExtra = 11, FnTextFe = 12, FnExpand = 22, FnMarker = 23, FnSpace = 24;

    private static readonly HashSet<string> KnownFunctions = new(StringComparer.Ordinal)
    {
        "sin","cos","tan","cot","sec","csc","arcsin","arccos","arctan","sinh","cosh","tanh","coth","exp","log","ln","lg",
        "lim","max","min","sup","inf","det","dim","ker","deg","arg","gcd","hom","Pr","liminf","limsup",
    };

    private static readonly Dictionary<int, string> Symbols = new()
    {
        [0x03B1] = "\\alpha", [0x03B2] = "\\beta", [0x03B3] = "\\gamma", [0x03B4] = "\\delta", [0x03B5] = "\\varepsilon", [0x03F5] = "\\epsilon",
        [0x03B6] = "\\zeta", [0x03B7] = "\\eta", [0x03B8] = "\\theta", [0x03D1] = "\\vartheta", [0x03B9] = "\\iota", [0x03BA] = "\\kappa",
        [0x03BB] = "\\lambda", [0x03BC] = "\\mu", [0x03BD] = "\\nu", [0x03BE] = "\\xi", [0x03BF] = "o", [0x03C0] = "\\pi", [0x03D6] = "\\varpi",
        [0x03C1] = "\\rho", [0x03F1] = "\\varrho", [0x03C3] = "\\sigma", [0x03C2] = "\\varsigma", [0x03C4] = "\\tau", [0x03C5] = "\\upsilon",
        [0x03C6] = "\\varphi", [0x03D5] = "\\phi", [0x03C7] = "\\chi", [0x03C8] = "\\psi", [0x03C9] = "\\omega",
        [0x0393] = "\\Gamma", [0x0394] = "\\Delta", [0x0398] = "\\Theta", [0x039B] = "\\Lambda", [0x039E] = "\\Xi", [0x03A0] = "\\Pi",
        [0x03A3] = "\\Sigma", [0x03A5] = "\\Upsilon", [0x03A6] = "\\Phi", [0x03A8] = "\\Psi", [0x03A9] = "\\Omega",
        [0x2212] = "-", [0x00B1] = "\\pm", [0x2213] = "\\mp", [0x00D7] = "\\times", [0x00F7] = "\\div", [0x22C5] = "\\cdot", [0x00B7] = "\\cdot",
        [0x2219] = "\\cdot", [0x2217] = "*", [0x2264] = "\\le", [0x2265] = "\\ge", [0x2260] = "\\ne", [0x2248] = "\\approx", [0x2261] = "\\equiv",
        [0x2245] = "\\cong", [0x223C] = "\\sim", [0x2243] = "\\simeq", [0x221D] = "\\propto", [0x226A] = "\\ll", [0x226B] = "\\gg",
        [0x221E] = "\\infty", [0x2202] = "\\partial", [0x2207] = "\\nabla", [0x2208] = "\\in", [0x2209] = "\\notin", [0x220B] = "\\ni",
        [0x2282] = "\\subset", [0x2283] = "\\supset", [0x2286] = "\\subseteq", [0x2287] = "\\supseteq", [0x222A] = "\\cup", [0x2229] = "\\cap",
        [0x2205] = "\\emptyset", [0x2200] = "\\forall", [0x2203] = "\\exists", [0x00AC] = "\\neg", [0x2227] = "\\wedge", [0x2228] = "\\vee",
        [0x2192] = "\\rightarrow", [0x2190] = "\\leftarrow", [0x2194] = "\\leftrightarrow", [0x21D2] = "\\Rightarrow", [0x21D0] = "\\Leftarrow",
        [0x21D4] = "\\Leftrightarrow", [0x2191] = "\\uparrow", [0x2193] = "\\downarrow", [0x21A6] = "\\mapsto",
        [0x2026] = "\\ldots", [0x22EF] = "\\cdots", [0x22EE] = "\\vdots", [0x22F1] = "\\ddots", [0x2032] = "'", [0x2033] = "''",
        [0x2220] = "\\angle", [0x22A5] = "\\perp", [0x2225] = "\\parallel", [0x2016] = "\\|", [0x2297] = "\\otimes", [0x2295] = "\\oplus",
        [0x2299] = "\\odot", [0x2218] = "\\circ", [0x2216] = "\\setminus", [0x210F] = "\\hbar", [0x2113] = "\\ell", [0x211C] = "\\Re", [0x2111] = "\\Im",
        [0x2135] = "\\aleph", [0x2223] = "\\mid", [0x2211] = "\\sum", [0x220F] = "\\prod", [0x2210] = "\\coprod", [0x222B] = "\\int",
        [0x222C] = "\\iint", [0x222D] = "\\iiint", [0x222E] = "\\oint", [0x22C3] = "\\bigcup", [0x22C2] = "\\bigcap", [0x221A] = "\\surd",
        [0x2329] = "\\langle", [0x232A] = "\\rangle", [0x27E8] = "\\langle", [0x27E9] = "\\rangle", [0x230A] = "\\lfloor", [0x230B] = "\\rfloor",
        [0x2308] = "\\lceil", [0x2309] = "\\rceil", [0x2234] = "\\therefore", [0x2235] = "\\because", [0x225C] = "\\triangleq", [0x2261] = "\\equiv",
        [0x226E] = "\\nless", [0x2270] = "\\nleq", [0x2271] = "\\ngeq", [0x2282] = "\\subset", [0x22A2] = "\\vdash", [0x2245] = "\\cong",
        [0x00B0] = "^{\\circ}",
    };

    // MathType private-use codes seen in MT Extra / spacing typefaces
    private static readonly Dictionary<int, string> PrivateUse = new()
    {
        [0xEB00] = "", [0xEB01] = "", [0xEB02] = "", [0xEB03] = "", // MathType alignment / filler marks
        [0xEC0B] = "\\cdots", [0xEC0C] = "\\ddots", [0xEC0D] = "\\vdots", [0xEC0E] = "\\ldots",
    };

    private static readonly HashSet<string> Relations = new(StringComparer.Ordinal)
    { "=", "<", ">", "\\le", "\\ge", "\\ne", "\\approx", "\\equiv", "\\cong", "\\sim", "\\simeq", "\\propto", "\\triangleq", "\\rightarrow", "\\Rightarrow", "\\Leftrightarrow", "\\in", "\\subset", "\\subseteq" };

    private sealed class Writer
    {
        public List<string> Problems { get; } = new();

        /// <summary>Top level: lines and piles of the equation, in order.</summary>
        public string Body(List<Node> top)
        {
            var parts = new List<string>();
            foreach (var n in top)
            {
                if (n is LineNode l) parts.Add(Seq(l.Items));
                else if (n is PileNode p) parts.Add(Pile(p));
                else if (n is MatrixNode m) parts.Add(Matrix(m));
                else if (n is TmplNode or CharNode) parts.Add(Seq(new List<Node> { n }));
            }
            return string.Join(" ", parts.Where(s => s.Length > 0));
        }

        /// <summary>One base with its primes and scripts. Scripts are kept apart from the base so that a second
        /// subscript/superscript template after the same base is merged into the first (MathType draws them side by
        /// side) instead of being hung on an empty base.</summary>
        private sealed class Atom
        {
            public string Tex = "";
            public string Primes = "";
            public string? Sub, Sup;
            public string Render() =>
                Tex + Primes + (Sub != null ? "_{" + Sub + "}" : "") + (Sup != null ? "^{" + Sup + "}" : "");
        }

        private string Seq(List<Node> items)
        {
            var atoms = new List<Atom>();
            for (int i = 0; i < items.Count; i++)
            {
                var n = items[i];
                switch (n)
                {
                    case CharNode c when Fn(c) is FnText or FnTextFe && IsTextWordChar(c.Code) && c.Embells.Count == 0:
                    {
                        // words typed in MathType's Text style; symbols in Text style stay math symbols
                        var sb = new StringBuilder();
                        int j = i;
                        for (; j < items.Count && items[j] is CharNode t && Fn(t) is FnText or FnTextFe && IsTextWordChar(t.Code) && t.Embells.Count == 0; j++)
                            sb.Append(TextChar(t.Code));
                        i = j - 1;
                        var txt = sb.ToString();
                        atoms.Add(new Atom { Tex = txt.Trim().Length == 0 ? "\\ " : "\\text{" + txt + "}" });
                        continue;
                    }
                    case CharNode c when Fn(c) == FnFunction && IsLetter(c.Code):
                    {
                        var sb = new StringBuilder();
                        sb.Append((char)c.Code);
                        int j = i + 1;
                        for (; j < items.Count && items[j] is CharNode t && Fn(t) == FnFunction && IsLetter(t.Code) && !t.FuncStart && t.Embells.Count == 0; j++)
                            sb.Append((char)t.Code);
                        i = j - 1;
                        var word = sb.ToString();
                        var tex = KnownFunctions.Contains(word) ? "\\" + word : "\\operatorname{" + word + "}";
                        var a = new Atom();
                        (a.Tex, a.Primes) = Embellish(tex, c.Embells);
                        atoms.Add(a);
                        continue;
                    }
                    case CharNode c:
                    {
                        var tex = CharTex(c);
                        if (tex.Length == 0) continue;
                        var a = new Atom();
                        (a.Tex, a.Primes) = Embellish(tex, c.Embells);
                        atoms.Add(a);
                        continue;
                    }
                    case TmplNode t when t.Selector is 27 or 28 or 29:
                    {
                        var slots = Slots(t);
                        string sub = slots.Count > 0 ? slots[0] : "", sup = slots.Count > 1 ? slots[1] : "";
                        if (t.Selector == 27) sup = "";
                        if (t.Selector == 28 && slots.Count == 1) sup = slots[0];
                        if (t.Selector == 28) sub = "";
                        if ((t.Variation & 0x01) != 0)
                        {
                            // pre-scripts (left of the base): the scripts precede the next element
                            atoms.Add(new Atom { Tex = "{}", Sub = sub.Length > 0 ? sub : null, Sup = sup.Length > 0 ? sup : null });
                            continue;
                        }
                        if (atoms.Count == 0) atoms.Add(new Atom { Tex = "{}" });
                        var last = atoms[^1];
                        // a second script on the same base continues the first one, as MathType draws it
                        if (sub.Length > 0) last.Sub = last.Sub == null ? sub : last.Sub + " " + sub;
                        if (sup.Length > 0) last.Sup = last.Sup == null ? sup : last.Sup + " " + sup;
                        continue;
                    }
                    case TmplNode t: atoms.Add(new Atom { Tex = Template(t) }); continue;
                    case PileNode p: atoms.Add(new Atom { Tex = Pile(p) }); continue;
                    case MatrixNode m: atoms.Add(new Atom { Tex = Matrix(m) }); continue;
                    case LineNode l: if (!l.Null) atoms.Add(new Atom { Tex = "{" + Seq(l.Items) + "}" }); continue;
                }
            }
            return string.Join(" ", atoms.Select(a => a.Render()).Where(s => s.Length > 0));
        }

        private static int Fn(CharNode c) => c.Typeface >= 128 ? c.Typeface - 128 : -1;
        private static bool IsLetter(int code) => code is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        private static bool IsTextWordChar(int code) =>
            code > 0 && code < 0xE000 && (char.IsLetterOrDigit((char)code) || code is ' ' or 0x00A0 or '.' or '-' or '\'');

        private static string TextChar(int code) => code switch
        {
            '\\' => "\\textbackslash{}", '{' => "\\{", '}' => "\\}", '$' => "\\$", '&' => "\\&", '#' => "\\#",
            '%' => "\\%", '_' => "\\_", '^' => "\\^{}", '~' => "\\~{}",
            < 0 => "", _ => char.ConvertFromUtf32(code is >= 0xD800 and <= 0xDFFF ? 0xFFFD : code),
        };

        private string CharTex(CharNode c)
        {
            int code = c.Code, fn = Fn(c);
            if (code < 0) { Problems.Add("character without a Unicode code (font-only encoding)"); return ""; }
            if (fn == FnMarker) return "";
            if (fn == FnSpace || code is 0x2009 or 0x2005 or 0x2003 or 0x2002 or 0x200A or 0x200B or 0x2061 or 0x2062 or 0x2063 or 0x00A0)
                return code switch
                {
                    0x200B or 0x2061 or 0x2062 or 0x2063 or 0x200A => "",
                    0x2005 => "\\;", 0x2003 => "\\quad", 0x2002 => "\\;", 0x00A0 or 0x0020 => "\\ ",
                    _ => fn == FnSpace && code >= 0xE000 ? SpaceFromPrivate(code) : "\\,",
                };
            if (code == ' ') return "";
            if (code is >= 0xE000 and <= 0xF8FF)
            {
                if (PrivateUse.TryGetValue(code, out var pu)) return pu;
                Problems.Add($"unmapped MathType symbol U+{code:X4} (typeface {fn})");
                return "";
            }
            if (IsLetter(code))
            {
                var s = ((char)code).ToString();
                return fn == FnVector ? "\\mathbf{" + s + "}" : fn == FnFunction ? "\\mathrm{" + s + "}" : s;
            }
            if (code is >= '0' and <= '9') return ((char)code).ToString();
            if (Symbols.TryGetValue(code, out var sym)) return fn == FnVector && sym.StartsWith('\\') && code < 0x2000 ? "\\boldsymbol{" + sym + "}" : sym;
            return code switch
            {
                '{' => "\\{", '}' => "\\}", '\\' => "\\backslash", '#' => "\\#", '$' => "\\$", '%' => "\\%", '&' => "\\&", '_' => "\\_",
                '^' => "\\hat{}", '~' => "\\sim",
                _ => char.ConvertFromUtf32(code is >= 0xD800 and <= 0xDFFF ? 0xFFFD : code),
            };
        }

        private static string SpaceFromPrivate(int code) => code switch
        {
            0xEF00 or 0xEF08 => "", 0xEF01 or 0xEF04 => "\\,", 0xEF02 or 0xEF05 => "\\;", 0xEF03 or 0xEF06 => "\\quad", 0xEF07 => "\\qquad",
            _ => "\\,",
        };

        /// <summary>Applies embellishments; primes are returned separately so scripts can follow them.</summary>
        private (string Tex, string Primes) Embellish(string tex, List<int> embells)
        {
            string primes = "";
            foreach (var e in embells)
            {
                switch (e)
                {
                    case 2: tex = "\\dot{" + tex + "}"; break;
                    case 3: tex = "\\ddot{" + tex + "}"; break;
                    case 4: tex = "\\dddot{" + tex + "}"; break;
                    case 5: primes += "'"; break;
                    case 6: primes += "''"; break;
                    case 7: tex = "{}^{\\backprime}" + tex; break;
                    case 8: tex = "\\tilde{" + tex + "}"; break;
                    case 9: tex = "\\hat{" + tex + "}"; break;
                    case 10: tex = "\\not{" + tex + "}"; break;
                    case 11: case 14: tex = "\\vec{" + tex + "}"; break;
                    case 12: case 15: tex = "\\overleftarrow{" + tex + "}"; break;
                    case 13: tex = "\\overleftrightarrow{" + tex + "}"; break;
                    case 17: tex = "\\bar{" + tex + "}"; break;
                    case 18: primes += "'''"; break;
                    case 19: tex = "\\overset{\\frown}{" + tex + "}"; break;
                    case 20: tex = "\\overset{\\smile}{" + tex + "}"; break;
                    case 29: tex = "\\underline{" + tex + "}"; break;
                    case 30: tex = "\\underset{\\sim}{" + tex + "}"; break;
                    default: Problems.Add($"unsupported embellishment {e}"); break;
                }
            }
            return (tex, primes);
        }

        private List<string> Slots(TmplNode t) =>
            t.Objects.Where(o => o is LineNode or PileNode or MatrixNode).Select(o => o switch
            {
                LineNode l => l.Null ? "" : Seq(l.Items),
                PileNode p => Pile(p),
                MatrixNode m => Matrix(m),
                _ => "",
            }).ToList();

        private static List<CharNode> Chars(TmplNode t) => t.Objects.OfType<CharNode>().ToList();

        private static string Delim(int code, string fallback) => code switch
        {
            '(' => "(", ')' => ")", '[' => "[", ']' => "]", '{' => "\\{", '}' => "\\}", '|' => "|",
            0x2016 or 0x2225 => "\\|", 0x2329 or 0x27E8 or 0x3008 or '<' => "\\langle", 0x232A or 0x27E9 or 0x3009 or '>' => "\\rangle",
            0x230A => "\\lfloor", 0x230B => "\\rfloor", 0x2308 => "\\lceil", 0x2309 => "\\rceil", 0x27E6 => "[", 0x27E7 => "]",
            _ => fallback,
        };

        private static readonly (string L, string R)[] FenceDefaults =
        {
            ("\\langle", "\\rangle"), ("(", ")"), ("\\{", "\\}"), ("[", "]"), ("|", "|"), ("\\|", "\\|"),
            ("\\lfloor", "\\rfloor"), ("\\lceil", "\\rceil"), ("[", "]"), ("(", ")"),
        };

        private string Template(TmplNode t)
        {
            var s = Slots(t);
            string S(int i) => i < s.Count ? s[i] : "";
            var chars = Chars(t);
            int v = t.Variation;
            switch (t.Selector)
            {
                case <= 9: // fences
                {
                    var (dl, dr) = FenceDefaults[t.Selector];
                    bool hasL = true, hasR = true;
                    if (t.Selector == 9)
                    {
                        dl = (v & 0x03) switch { 0 => "(", 1 => ")", 2 => "[", _ => "]" };
                        dr = ((v >> 4) & 0x03) switch { 0 => "(", 1 => ")", 2 => "[", _ => "]" };
                        if (chars.Count >= 2) { dl = Delim(chars[0].Code, dl); dr = Delim(chars[1].Code, dr); }
                    }
                    else
                    {
                        if ((v & 0x03) != 0) { hasL = (v & 0x01) != 0; hasR = (v & 0x02) != 0; }
                        int ci = 0;
                        if (hasL && ci < chars.Count) dl = Delim(chars[ci++].Code, dl);
                        if (hasR && ci < chars.Count) dr = Delim(chars[ci].Code, dr);
                    }
                    return "\\left" + (hasL ? dl : ".") + " " + S(0) + " \\right" + (hasR ? dr : ".");
                }
                case 10: // root
                    return S(1).Length > 0 ? "\\sqrt[" + S(1) + "]{" + S(0) + "}" : "\\sqrt{" + S(0) + "}";
                case 11: // fraction
                    return (v & 0x02) != 0 ? "{" + S(0) + "}/{" + S(1) + "}" : "\\frac{" + S(0) + "}{" + S(1) + "}";
                case 12: return (v & 0x01) != 0 ? "\\underline{\\underline{" + S(0) + "}}" : "\\underline{" + S(0) + "}";
                case 13: return (v & 0x01) != 0 ? "\\overline{\\overline{" + S(0) + "}}" : "\\overline{" + S(0) + "}";
                case 14: // arrow with text
                {
                    string arrow = chars.Count > 0 && Symbols.TryGetValue(chars[0].Code, out var a) ? a
                        : (v & 0x01) != 0 ? "\\Leftrightarrow" : (v & 0x10) != 0 && (v & 0x20) == 0 ? "\\leftarrow" : "\\rightarrow";
                    string top = S(0), bot = S(1);
                    var x = arrow;
                    if (bot.Length > 0) x = "\\underset{" + bot + "}{" + x + "}";
                    if (top.Length > 0) x = "\\overset{" + top + "}{" + x + "}";
                    return x;
                }
                case >= 15 and <= 22: // integrals and big operators: main, lower, upper + operator character
                {
                    string op = chars.Count > 0 && Symbols.TryGetValue(chars[^1].Code, out var o) && o.StartsWith('\\') ? o : t.Selector switch
                    {
                        15 or 21 => (v & 0x03) switch { 2 => "\\iint", 3 => "\\iiint", _ => (v & 0x0C) != 0 ? "\\oint" : "\\int" },
                        16 or 22 => "\\sum", 17 => "\\prod", 18 => "\\coprod", 19 => "\\bigcup", 20 => "\\bigcap", _ => "\\sum",
                    };
                    bool isInt = op is "\\int" or "\\iint" or "\\iiint" or "\\oint";
                    string lim = isInt ? ((v & 0x40) != 0 ? "\\limits" : "") : ((v & 0x40) != 0 || t.Selector is 16 or 17 or 18 or 19 or 20 ? "\\limits" : "\\nolimits");
                    var x = op + lim;
                    if (S(1).Length > 0) x += "_{" + S(1) + "}";
                    if (S(2).Length > 0) x += "^{" + S(2) + "}";
                    return x + " {" + S(0) + "}";
                }
                case 23: // limit: main (e.g. lim, max, min), lower, upper; limits go under/over, as MathType draws them
                {
                    var x = "\\mathop{" + S(0).Trim() + "}\\limits";
                    if (S(1).Length > 0) x += "_{" + S(1) + "}";
                    if (S(2).Length > 0) x += "^{" + S(2) + "}";
                    return x;
                }
                case 24: case 25: // horizontal brace / bracket with label
                    return (v & 0x01) != 0 ? "\\overbrace{" + S(0) + "}^{" + S(1) + "}" : "\\underbrace{" + S(0) + "}_{" + S(1) + "}";
                case 30: // Dirac bra-ket
                {
                    bool bra = (v & 0x01) != 0, ket = (v & 0x02) != 0;
                    return (bra ? "\\langle " + S(0) : "") + (bra && ket ? " | " : bra ? " |" : "| ") + (ket ? S(1) + " \\rangle" : "");
                }
                case 31: // over/under arrow
                {
                    bool under = (v & 0x04) != 0, left = (v & 0x01) != 0, right = (v & 0x02) != 0;
                    var arrow = left && right ? "\\leftrightarrow" : left ? "\\leftarrow" : "\\rightarrow";
                    if (under) return "\\underset{" + arrow + "}{" + S(0) + "}";
                    return (left && right ? "\\overleftrightarrow{" : left ? "\\overleftarrow{" : "\\overrightarrow{") + S(0) + "}";
                }
                case 32: return "\\widetilde{" + S(0) + "}";
                case 33: return "\\widehat{" + S(0) + "}";
                case 34: return "\\overset{\\frown}{" + S(0) + "}";
                case 37: return "\\boxed{" + S(0) + "}";
                default:
                    Problems.Add($"unsupported MathType template {t.Selector} (variation {v})");
                    return S(0);
            }
        }

        private string Pile(PileNode p)
        {
            var lines = p.Lines.OfType<LineNode>().Select(l => l.Null ? "" : Seq(l.Items)).ToList();
            if (lines.Count == 0) return "";
            if (lines.Count == 1) return lines[0];
            string col = p.HAlign switch { 1 => "l", 3 => "r", _ => "c" };
            if (p.HAlign is 4 or 5)
            {
                // relational alignment: split each line before its first top-level relation
                var rows = p.Lines.OfType<LineNode>().Select(SplitAtRelation).ToList();
                return "\\begin{array}{rl} " + string.Join(" \\\\ ", rows.Select(r => r.Left + " & " + r.Right)) + " \\end{array}";
            }
            return "\\begin{array}{" + col + "} " + string.Join(" \\\\ ", lines) + " \\end{array}";
        }

        private (string Left, string Right) SplitAtRelation(LineNode l)
        {
            if (l.Null) return ("", "");
            int at = l.Items.FindIndex(n => n is CharNode c && Relations.Contains(CharTexNoProblems(c)));
            if (at < 0) return ("", Seq(l.Items));
            return (Seq(l.Items.Take(at).ToList()), Seq(l.Items.Skip(at).ToList()));
        }

        private string CharTexNoProblems(CharNode c)
        {
            int before = Problems.Count;
            var s = CharTex(c);
            if (Problems.Count > before) Problems.RemoveRange(before, Problems.Count - before);
            return s;
        }

        private string Matrix(MatrixNode m)
        {
            var cells = m.Cells.Where(o => o is LineNode or PileNode or MatrixNode).Select(o => o switch
            {
                LineNode l => l.Null ? "" : Seq(l.Items),
                PileNode p => Pile(p),
                MatrixNode mm => Matrix(mm),
                _ => "",
            }).ToList();
            if (m.Rows <= 0 || m.Cols <= 0) return "";
            if (cells.Count != m.Rows * m.Cols) Problems.Add($"matrix has {cells.Count} cells, expected {m.Rows}x{m.Cols}");
            var rows = new List<string>();
            for (int r = 0; r < m.Rows; r++)
                rows.Add(string.Join(" & ", Enumerable.Range(0, m.Cols).Select(c => r * m.Cols + c < cells.Count ? cells[r * m.Cols + c] : "")));
            return "\\begin{matrix} " + string.Join(" \\\\ ", rows) + " \\end{matrix}";
        }
    }
}
