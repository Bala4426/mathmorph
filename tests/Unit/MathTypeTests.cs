using System.Buffers.Binary;
using System.Text;
using MathMorph.Contracts;
using MathMorph.Engine;

namespace MathMorph.Tests;

/// <summary>MathType (MTEF v5) reading: compound-file extraction, record parsing and LaTeX output.</summary>
public static class MathTypeTests
{
    // ---- MTEF v5 builders (typeface byte = 128 + style: 1 text, 2 function, 3 variable, 4 lc greek, 6 symbol)
    private const int Text = 129, Func = 130, Var = 131, Greek = 132, Sym = 134;

    private static byte[] Mtef(params byte[][] body)
    {
        var h = new List<byte> { 5, 1, 0, 7, 8 };
        h.AddRange(Encoding.ASCII.GetBytes("DSMT7")); h.Add(0); h.Add(0);
        h.Add(10); // FULL
        foreach (var b in body) h.AddRange(b);
        h.Add(0);
        return h.ToArray();
    }
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Line(params byte[][] items) => Cat(new byte[] { 1, 0 }, Cat(items), new byte[] { 0 });
    private static byte[] NullLine() => new byte[] { 1, 1 };
    private static byte[] Ch(int typeface, int code, int opt = 0) => new byte[] { 2, (byte)opt, (byte)typeface, (byte)(code & 0xFF), (byte)(code >> 8) };
    private static byte[] Chars(int typeface, string s) => Cat(s.Select(c => Ch(typeface, c)).ToArray());
    private static byte[] Tmpl(int sel, int variation, params byte[][] objs) => Cat(new byte[] { 3, 0, (byte)sel, (byte)variation, 0 }, Cat(objs), new byte[] { 0 });
    private static byte[] Pile(int halign, params byte[][] lines) => Cat(new byte[] { 4, 0, (byte)halign, 1 }, Cat(lines), new byte[] { 0 });
    private static byte[] Sub(params byte[][] items) => Tmpl(27, 0, Line(items), NullLine());
    private static byte[] Sup(params byte[][] items) => Tmpl(28, 0, NullLine(), Line(items));

    private static string Tex(byte[] mtef)
    {
        var r = MathType.ToLatex(mtef);
        Assert.Equal(0, r.Problems.Count, "problems: " + string.Join("; ", r.Problems));
        return r.Latex;
    }

    [Test]
    public static void Subscript_And_Superscript()
    {
        Assert.Equal("x_{n}", Tex(Mtef(Line(Ch(Var, 'x'), Sub(Ch(Var, 'n'))))));
        Assert.Equal("x^{2}", Tex(Mtef(Line(Ch(Var, 'x'), Sup(Ch(Var, '2'))))));
        Assert.Equal("S_{x}^{k}", Tex(Mtef(Line(Ch(Var, 'S'), Tmpl(29, 0, Line(Ch(Var, 'x')), Line(Ch(Var, 'k')))))));
    }

    [Test]
    public static void ConsecutiveScripts_AreMerged_NotHungOnEmptyBase()
    {
        // alpha_{k,} followed by a second subscript template _{m}: MathType draws alpha_{k,m}
        var tex = Tex(Mtef(Line(Ch(Greek, 0x03B1), Sub(Ch(Var, 'k'), Ch(Sym, ',')), Sub(Ch(Var, 'm')))));
        Assert.Equal("\\alpha_{k , m}", tex);
        Assert.NotContains("{}", tex);
    }

    [Test]
    public static void Prime_Then_Superscript()
    {
        // char with a prime embellishment (EMBELL type 5), then x^{2}
        var primed = Cat(new byte[] { 2, 1, Var, (byte)'f', 0 }, new byte[] { 6, 0, 5, 0 });
        Assert.Equal("f'^{2}", Tex(Mtef(Line(primed, Sup(Ch(Var, '2'))))));
    }

    [Test]
    public static void Fraction_Root_Integral_Sum()
    {
        Assert.Equal("\\frac{a}{\\sqrt{b}}", Tex(Mtef(Line(Tmpl(11, 0, Line(Ch(Var, 'a')), Line(Tmpl(10, 0, Line(Ch(Var, 'b')), NullLine())))))));
        Assert.Equal("\\sqrt[3]{x}", Tex(Mtef(Line(Tmpl(10, 1, Line(Ch(Var, 'x')), Line(Ch(Var, '3')))))));
        var integral = Tmpl(15, 0x31, Line(Ch(Var, 'f')), Line(Ch(Var, '0')), Line(Ch(Var, '1')), Ch(Sym, 0x222B));
        Assert.Equal("\\int_{0}^{1} {f}", Tex(Mtef(Line(integral))));
        var sum = Tmpl(16, 0x70, Line(Ch(Var, 'a')), Line(Ch(Var, 'k')), Line(Ch(Var, 'N')), Ch(Sym, 0x2211));
        Assert.Equal("\\sum\\limits_{k}^{N} {a}", Tex(Mtef(Line(sum))));
    }

    [Test]
    public static void Limit_PutsLimitUnderneath()
    {
        var lim = Tmpl(23, 0, Line(Chars(Func, "lim")), Line(Ch(Var, 'N'), Ch(Sym, 0x2192), Ch(Sym, 0x221E)), NullLine());
        Assert.Equal("\\mathop{\\lim}\\limits_{N \\rightarrow \\infty}", Tex(Mtef(Line(lim))));
    }

    [Test]
    public static void WrapLimitFunctions_BuildsWordFuncStructure()
    {
        var m = Docx.MNs;
        var x = System.Xml.Linq.XElement.Parse($"<m:oMath xmlns:m=\"{m}\"><m:limLow><m:e><m:r><m:t>lim</m:t></m:r></m:e><m:lim><m:r><m:t>N</m:t></m:r></m:lim></m:limLow>" +
                                               "<m:f><m:num><m:r><m:t>1</m:t></m:r></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f>" +
                                               "<m:limLow><m:e><m:r><m:t>x</m:t></m:r></m:e><m:lim><m:r><m:t>y</m:t></m:r></m:lim></m:limLow><m:r><m:t>z</m:t></m:r></m:oMath>");
        MathType.WrapLimitFunctions(x);
        OmmlConverter.Validate(x);
        var func = x.Element((System.Xml.Linq.XNamespace)m + "func");
        Assert.NotNull(func, "m:func created");
        Assert.NotNull(func!.Element((System.Xml.Linq.XNamespace)m + "fName")?.Element((System.Xml.Linq.XNamespace)m + "limLow"), "limit in fName");
        Assert.NotNull(func.Element((System.Xml.Linq.XNamespace)m + "e")?.Element((System.Xml.Linq.XNamespace)m + "f"), "fraction is the argument");
        Assert.Equal(1, x.Elements((System.Xml.Linq.XNamespace)m + "limLow").Count(), "non-function limLow (x under y) left alone");
    }

    [Test]
    public static void Cases_LeftBraceOnly_WithPile()
    {
        var pile = Pile(1, Line(Ch(Var, 'a')), Line(Ch(Var, '0')));
        var brace = Tmpl(2, 1, Line(pile), Ch(Sym, '{'));
        Assert.Equal("\\left\\{ \\begin{array}{l} a \\\\ 0 \\end{array} \\right.", Tex(Mtef(Line(brace))));
    }

    [Test]
    public static void Functions_TextWords_And_TextSymbols()
    {
        Assert.Equal("\\sin x", Tex(Mtef(Line(Chars(Func, "sin"), Ch(Var, 'x')))));
        Assert.Equal("\\operatorname{sgn} x", Tex(Mtef(Line(Chars(Func, "sgn"), Ch(Var, 'x')))));
        Assert.Equal("\\text{if} x", Tex(Mtef(Line(Chars(Text, "if"), Ch(Var, 'x')))));
        // symbols typed in Text style stay math symbols (no \text{=\{})
        Assert.Equal("B = \\{ B_{1} \\}", Tex(Mtef(Line(Ch(Var, 'B'), Ch(Text, '='), Ch(Text, '{'), Ch(Var, 'B'), Sub(Ch(Var, '1')), Ch(Text, '}')))));
    }

    [Test]
    public static void Unknown_Content_Is_Reported_Not_Guessed()
    {
        var r = MathType.ToLatex(Mtef(Line(Tmpl(26, 0, Line(Ch(Var, 'a')), Line(Ch(Var, 'b'))))));
        Assert.True(r.Problems.Any(p => p.Contains("template 26")), "unsupported template reported");
        var pua = MathType.ToLatex(Mtef(Line(Ch(Sym, 0xE9FF))));
        Assert.True(pua.Problems.Any(p => p.Contains("U+E9FF")), "unmapped private-use symbol reported");
        var emb = MathType.ToLatex(Mtef(Line(Cat(new byte[] { 2, 1, Var, (byte)'x', 0 }, new byte[] { 6, 0, 99, 0 }))));
        Assert.True(emb.Problems.Any(p => p.Contains("embellishment 99")), "unknown embellishment reported");
    }

    [Test]
    public static void LineWithRuler_Parses()
    {
        // LINE option 0x02: inline ruler [n_stops][type][offset16] before the object list
        var line = Cat(new byte[] { 1, 2, 1, 0, 0xC8, 0x1F }, Ch(Var, 'x'), new byte[] { 0 });
        Assert.Equal("x", Tex(Mtef(line)));
    }

    [Test]
    public static void Truncated_Or_Unsupported_Data_Throws()
    {
        var full = Mtef(Line(Ch(Var, 'x'), Sub(Ch(Var, 'n'))));
        Assert.Throws<InvalidDataException>(() => MathType.ToLatex(full[..^6]));
        var v3 = (byte[])full.Clone(); v3[0] = 3;
        Assert.Throws<NotSupportedException>(() => MathType.ToLatex(v3));
        var bad = Mtef(new byte[] { 77 });
        Assert.Throws<InvalidDataException>(() => MathType.ToLatex(bad));
    }

    // ---- compound file (OLE) builder: header | FAT | directory | mini stream | mini FAT, 512-byte sectors
    internal static byte[] OleBin(byte[] mtef, bool loopDirectory = false)
    {
        var native = new byte[28 + mtef.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(native, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(2), 0x00020000);
        BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(8), (uint)mtef.Length);
        mtef.CopyTo(native, 28);
        if (native.Length > 512) throw new ArgumentException("test stream too large");
        var f = new byte[512 * 5];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(f, 0);
        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(o), (ushort)v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(o), v);
        U16(0x18, 0x3E); U16(0x1A, 3); U16(0x1C, 0xFFFE); U16(0x1E, 9); U16(0x20, 6);
        U32(0x2C, 1); U32(0x30, 1); U32(0x38, 4096); U32(0x3C, 3); U32(0x40, 1); U32(0x44, 0xFFFFFFFE);
        for (int i = 0; i < 109; i++) U32(0x4C + i * 4, 0xFFFFFFFF);
        U32(0x4C, 0); // FAT in sector 0
        int S(int n) => 512 * (n + 1);
        for (int i = 0; i < 128; i++) U32(S(0) + i * 4, 0xFFFFFFFF);
        U32(S(0), 0xFFFFFFFD); U32(S(0) + 4, loopDirectory ? 1u : 0xFFFFFFFE); U32(S(0) + 8, 0xFFFFFFFE); U32(S(0) + 12, 0xFFFFFFFE);
        void Dir(int idx, string name, byte type, uint start, uint size)
        {
            int o = S(1) + idx * 128;
            var n = Encoding.Unicode.GetBytes(name);
            n.CopyTo(f, o);
            U16(o + 0x40, n.Length + 2); f[o + 0x42] = type; f[o + 0x43] = 1;
            U32(o + 0x44, 0xFFFFFFFF); U32(o + 0x48, 0xFFFFFFFF); U32(o + 0x4C, type == 5 ? 1u : 0xFFFFFFFF);
            U32(o + 0x74, start); U32(o + 0x78, size);
        }
        int miniSectors = (native.Length + 63) / 64;
        Dir(0, "Root Entry", 5, 2, (uint)(miniSectors * 64));
        Dir(1, "Equation Native", 2, 0, (uint)native.Length);
        native.CopyTo(f, S(2));
        for (int i = 0; i < 128; i++) U32(S(3) + i * 4, 0xFFFFFFFF);
        for (int i = 0; i < miniSectors; i++) U32(S(3) + i * 4, i == miniSectors - 1 ? 0xFFFFFFFE : (uint)(i + 1));
        return f;
    }

    [Test]
    public static void CompoundFile_ReadsEquationNative_EndToEnd()
    {
        var mtef = Mtef(Line(Ch(Var, 'x'), Sub(Ch(Var, 'n'))));
        var bin = OleBin(mtef);
        Assert.BytesEqual(mtef, MathType.ExtractMtef(bin), "MTEF bytes");
        Assert.Equal("x_{n}", MathType.Convert(bin).Latex);
        Assert.Null(CompoundFile.ReadStream(bin, "Missing Stream"));
    }

    [Test]
    public static void CompoundFile_RejectsLoopsAndNonOle()
    {
        Assert.Throws<InvalidDataException>(() => CompoundFile.ReadStream(OleBin(Mtef(Line(Ch(Var, 'x'))), loopDirectory: true), "Equation Native"));
        Assert.Throws<InvalidDataException>(() => CompoundFile.ReadStream(new byte[600], "Equation Native"));
    }

    [Test]
    public static void Inventory_RecordsMathTypeProgId()
    {
        var ole = Docx.Ole("rIdImg").Replace("ProgID=\"Equation.3\"", "ProgID=\"Equation.DSMT4\"");
        var pic = new PictureInfo
        {
            Part = "word/document.xml", Container = "body", ParaIndex = 0, Ordinal = 0, Kind = PictureKind.Ole,
            OleProgId = "Equation.DSMT4", OleTarget = "word/embeddings/oleObject1.bin",
        };
        Assert.True(Pipeline.IsConvertibleMathType(pic), "DSMT4 in body");
        Assert.False(Pipeline.IsConvertibleMathType(new PictureInfo { Part = "p", Container = "header", ParaIndex = 0, Ordinal = 0, Kind = PictureKind.Ole, OleProgId = "Equation.DSMT4", OleTarget = "x" }), "header excluded");
        Assert.False(Pipeline.IsConvertibleMathType(new PictureInfo { Part = "p", Container = "body", ParaIndex = 0, Ordinal = 0, Kind = PictureKind.Ole, OleProgId = "Equation.3", OleTarget = "x" }), "Equation.3 not MathType");
        Assert.Contains("Equation.DSMT4", ole);
    }
}
