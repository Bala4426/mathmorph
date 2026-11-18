using System.Diagnostics;
using System.Xml.Linq;

namespace MathMorph.Engine;

/// <summary>
/// LaTeX -> OMML. MML2OMML.XSL (Office) is not available on this machine, so the portable Pandoc in
/// tools\pandoc is used: it converts TeX math to OMML (via texmath) when writing .docx. The m:oMath
/// element is taken from that temporary .docx and validated.
/// </summary>
public sealed class OmmlConverter(string pandocExe, string tempDir)
{
    public string PandocExe { get; } = pandocExe;

    public bool Available => File.Exists(PandocExe);

    /// <summary>Returns a validated m:oMath element (math namespace only) or throws.</summary>
    public XElement Convert(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) throw new ArgumentException("empty LaTeX");
        if (latex.Length > 4000) throw new ArgumentException("LaTeX too long");
        Directory.CreateDirectory(tempDir);
        var outFile = Path.Combine(tempDir, "omml_" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var tex = latex.Replace("\r", " ").Replace("\n", " ").Trim();
            // a trailing control space ("\ ") loses its space to Trim and would escape the closing '$'
            if ((tex.Length - tex.TrimEnd('\\').Length) % 2 == 1) tex += " ";
            var src = "$" + tex + "$\n";
            var psi = new ProcessStartInfo(PandocExe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
            };
            foreach (var a in new[] { "-f", "markdown-smart", "-t", "docx", "--sandbox", "-o", outFile }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            p.StandardInput.Write(src);
            p.StandardInput.Close();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("pandoc timed out"); }
            var err = errTask.Result; _ = outTask.Result;
            if (p.ExitCode != 0 || !File.Exists(outFile)) throw new InvalidOperationException("pandoc failed: " + Trim(err));
            var pkg = SafePackage.Open(outFile, new Contracts.SafetyLimits());
            var doc = SafeXml.Parse(pkg.Get("word/document.xml")!.Data);
            var body = doc.Root!.Element(Ns.W + "body")!;
            var paras = body.Elements(Ns.W + "p").ToList();
            var maths = body.Descendants(Ns.M + "oMath").ToList();
            if (maths.Count != 1) throw new InvalidOperationException("LaTeX was not converted to a single equation");
            // pandoc emits unparseable TeX as plain text runs: the paragraph must hold only the equation
            if (paras.Count != 1 || paras[0].Elements(Ns.W + "r").Any()) throw new InvalidOperationException("LaTeX not fully parsed by converter");
            var omml = new XElement(maths[0]);
            Normalize(omml);
            Validate(omml);
            return omml;
        }
        finally { try { File.Delete(outFile); } catch { } }
    }

    private static readonly HashSet<string> AllowedMath = new(StringComparer.Ordinal)
    {
        "oMath","r","t","rPr","sty","scr","nor","lit","brk","aln","f","fPr","type","num","den","sSup","sSub","sSubSup","sPre",
        "e","sup","sub","nary","naryPr","chr","limLoc","subHide","supHide","grow","rad","radPr","degHide","deg","d","dPr",
        "begChr","endChr","sepChr","shp","func","funcPr","fName","acc","accPr","bar","barPr","pos","groupChr","groupChrPr",
        "vertJc","limLow","limLowPr","lim","limUpp","limUppPr","m","mPr","mr","mcs","mc","mcPr","count","mcJc","baseJc",
        "plcHide","rSp","rSpRule","cGp","cGpRule","cSp","eqArr","eqArrPr","maxDist","objDist","box","boxPr","opEmu","noBreak",
        "diff","borderBox","borderBoxPr","hideTop","hideBot","hideLeft","hideRight","strikeH","strikeV","strikeBLTR","strikeTLBR",
        "phant","phantPr","show","zeroWid","zeroAsc","zeroDesc","transp","ctrlPr","argPr","argSz","sSubPr","sSupPr","sSubSupPr",
        "sPrePr","alnScr","oMathPara","oMathParaPr","jc"
    };

    /// <summary>Schema child order for property elements that Pandoc (texmath) is known to emit out of order.
    /// Found by the Open XML validator: Pandoc writes m:mcPr as (mcJc, count); the schema requires (count, mcJc).</summary>
    private static readonly Dictionary<string, string[]> ChildOrder = new()
    {
        ["mcPr"] = new[] { "count", "mcJc" },
        ["rPr"] = new[] { "lit", "nor", "scr", "sty", "brk", "aln" },
        ["mPr"] = new[] { "baseJc", "plcHide", "rSpRule", "cGpRule", "rSp", "cSp", "cGp", "mcs", "ctrlPr" },
    };

    /// <summary>Reorders children of known property elements into schema order (stable for unknown names).</summary>
    public static void Normalize(XElement omml)
    {
        foreach (var e in omml.DescendantsAndSelf().ToList())
        {
            if (e.Name.Namespace != Ns.M || !ChildOrder.TryGetValue(e.Name.LocalName, out var order)) continue;
            // m:rPr allows m:nor OR (m:scr, m:sty), not both; Pandoc emits both for \text{...}. nor wins (plain text).
            if (e.Name.LocalName == "rPr" && e.Element(Ns.M + "nor") != null)
            {
                e.Elements(Ns.M + "scr").Remove();
                e.Elements(Ns.M + "sty").Remove();
            }
            var kids = e.Elements().ToList();
            var sorted = kids.Select((k, i) => (k, i))
                .OrderBy(t => { int p = Array.IndexOf(order, t.k.Name.LocalName); return p < 0 ? int.MaxValue : p; })
                .ThenBy(t => t.i).Select(t => t.k).ToList();
            if (sorted.SequenceEqual(kids)) continue;
            foreach (var k in kids) k.Remove();
            e.Add(sorted);
        }
    }

    /// <summary>OMML validation: well-formed, root m:oMath, only known math elements, non-empty content.</summary>
    public static void Validate(XElement omml)
    {
        if (omml.Name != Ns.M + "oMath") throw new InvalidOperationException("root is not m:oMath");
        foreach (var d in omml.DescendantsAndSelf())
        {
            if (d.Name.Namespace != Ns.M) throw new InvalidOperationException("non-math element " + d.Name.LocalName);
            if (!AllowedMath.Contains(d.Name.LocalName)) throw new InvalidOperationException("unknown math element m:" + d.Name.LocalName);
            foreach (var a in d.Attributes())
                if (!a.IsNamespaceDeclaration && a.Name.Namespace != Ns.M) throw new InvalidOperationException("foreign attribute " + a.Name);
        }
        if (!omml.Descendants(Ns.M + "t").Any(t => t.Value.Length > 0) && !omml.Descendants(Ns.M + "chr").Any())
            throw new InvalidOperationException("empty equation");
        // round-trip through the hardened parser to prove well-formedness of the serialized form
        SafeXml.Parse(omml.ToString(SaveOptions.DisableFormatting), lineInfo: false);
    }

    /// <summary>Serializes OMML for insertion. Declares the math namespace locally on the outer element
    /// so the rest of the part (including the root element) stays untouched.</summary>
    public static string Serialize(XElement omath, bool display)
    {
        XElement outer = display ? new XElement(Ns.M + "oMathPara", new XElement(omath)) : new XElement(omath);
        StripNamespaceDecls(outer);
        // always declare xmlns:m locally (redundant if the root already binds it; keeps the fragment self-contained)
        outer.Add(new XAttribute(XNamespace.Xmlns + "m", Ns.M.NamespaceName));
        return outer.ToString(SaveOptions.DisableFormatting | SaveOptions.OmitDuplicateNamespaces);
    }

    private static void StripNamespaceDecls(XElement e)
    {
        foreach (var d in e.DescendantsAndSelf()) d.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s.Trim();
}
