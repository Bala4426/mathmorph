using System.Globalization;
using System.Text.Json;
using MathMorph.Contracts;
using MathMorph.Engine;
using MathMorph.Recognition;

namespace MathMorph.Cli;

public static class Program
{
    /// <summary>Default folders live next to the project root (MATHMORPH_HOME or the exe's ancestors): &lt;root&gt;\input, &lt;root&gt;\output.</summary>
    public static string DefaultInput => Path.Combine(DefaultRoot(), "input");
    public static string DefaultOutput => Path.Combine(DefaultRoot(), "output");

    private static string DefaultRoot()
    {
        try { return EnginePaths.Discover().Root; }
        catch { return AppContext.BaseDirectory; }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "compare") return Compare(args);
            if (args.Length > 0 && args[0] == "inventory") return InventoryOnly(args);
            if (args.Length > 0 && args[0] == "mathtype-count") return MathTypeCount(args);
            if (args.Length > 0 && args[0] == "mathtype") return MathTypeDump(args);
            if (args.Length > 0 && args[0] == "preview") return Preview(args);
            if (args.Any(a => a is "-h" or "--help" or "/?")) { Usage(); return 0; }
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 3;
        }
    }

    /// <summary>preview &lt;original image&gt; &lt;latex .txt (UTF-8)&gt; &lt;render .png&gt;: checks one LaTeX the way a conversion would
    /// (Pandoc -> validated OMML) and renders it next to the original for a score. Prints one JSON line. Used by the GUI.</summary>
    private static int Preview(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("usage: preview <original image> <latex .txt> <render .png>");
        var latex = File.ReadAllText(args[2], System.Text.Encoding.UTF8).Replace("\r", " ").Replace("\n", " ").Trim();
        var paths = EnginePaths.Discover();
        string? convertError = null;
        try { new OmmlConverter(paths.Pandoc, Path.Combine(paths.Root, "out", "preview_tmp")).Convert(latex); }
        catch (Exception ex) { convertError = ex.Message; }
        VerifyResult v;
        if (File.Exists(args[1]))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
            using var svc = new BatchServices { Paths = paths, WorkRoot = Path.Combine(paths.Root, "out", "preview_tmp") };
            v = svc.Verify("preview", Path.GetFullPath(args[1]), latex, Path.GetFullPath(args[3]));
        }
        else v = new VerifyResult { Ok = false, Error = "original image not found" };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            convertible = convertError == null, convert_error = convertError ?? "",
            render_ok = v.Ok, ssim = v.Ssim, render_path = v.RenderPath ?? "", render_error = v.Error ?? "",
        }));
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("MathMorph.exe preview <image> <latex.txt> <out.png>  check + render one LaTeX (JSON line; used by the GUI)");
        Console.WriteLine("MathMorph.exe [<input .docx or folder>] [<output-folder>] [--min-score <v>] [--dry-run] [--force] [--no-gates] [--latex-overrides <csv: sha256,latex>] [--mathtype convert|skip]");
        Console.WriteLine($"  defaults: input {DefaultInput}, output {DefaultOutput}");
        Console.WriteLine("  --mathtype convert   convert MathType equation objects (Equation.DSMT*) to Word equations from their embedded data (default: convert; --mathtype skip leaves them unchanged)");
        Console.WriteLine("MathMorph.exe compare <a.docx> <b.docx>     entry-by-entry package comparison");
        Console.WriteLine("MathMorph.exe inventory <a.docx>            print inventory categories (no recognition)");
        Console.WriteLine("MathMorph.exe mathtype-count <.docx|folder> count MathType equation objects (prints: MATHTYPE <objects> <documents with MathType> <documents>)");
        Console.WriteLine("MathMorph.exe mathtype <a.docx>             print the LaTeX read from each MathType object (no output written)");
    }

    public static (double Value, string Basis) LoadThreshold(string root)
    {
        var f = Path.Combine(root, "config", "threshold.json");
        if (File.Exists(f))
        {
            using var j = JsonDocument.Parse(File.ReadAllText(f));
            return (j.RootElement.GetProperty("min_score").GetDouble(), j.RootElement.GetProperty("basis").GetString() ?? f);
        }
        return (0.95, "default 0.95 (no threshold derived from a visual check yet)");
    }

    private static int Run(string[] args)
    {
        var pos = new List<string>();
        var opt = new ConvertOptions();
        double? minScore = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--min-score":
                    if (i + 1 >= args.Length || !double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < -1 || v > 1)
                        throw new ArgumentException("--min-score needs a number between -1 and 1");
                    minScore = v; break;
                case "--dry-run": opt.DryRun = true; break;
                case "--force": opt.Force = true; break;
                case "--no-gates": opt.RunGateChecks = false; break;
                case "--mathtype":
                    if (i + 1 >= args.Length || args[i + 1] is not ("convert" or "skip")) throw new ArgumentException("--mathtype needs 'convert' or 'skip'");
                    opt.ConvertMathType = args[++i] == "convert"; break;
                case "--latex-overrides":
                    if (i + 1 >= args.Length) throw new ArgumentException("--latex-overrides needs a CSV path");
                    foreach (var line in File.ReadAllLines(args[++i]).Skip(1))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
                        var cells = Reports.ParseCsvLine(line);
                        if (cells.Count < 2 || cells[0].Trim().Length != 64) throw new ArgumentException("bad override row: " + line);
                        opt.LatexOverrides[cells[0].Trim()] = cells[1];
                    }
                    break;
                default:
                    if (args[i].StartsWith("--")) throw new ArgumentException("unknown option " + args[i]);
                    pos.Add(args[i]); break;
            }
        }
        if (pos.Count > 2) throw new ArgumentException("too many arguments");
        var input = Path.GetFullPath(pos.Count > 0 ? pos[0] : DefaultInput);
        var outRoot = Path.GetFullPath(pos.Count > 1 ? pos[1] : DefaultOutput);
        var paths = EnginePaths.Discover();
        var (thr, basis) = LoadThreshold(paths.Root);
        if (minScore.HasValue) { thr = minScore.Value; basis = "set on the command line (--min-score)"; }
        opt.MinScore = thr;

        List<string> files;
        if (Directory.Exists(input))
        {
            if (Pipeline.PathsEqual(input, outRoot) || IsInside(outRoot, input))
                throw new InvalidOperationException("output folder must not be the input folder or inside it");
            files = Directory.GetFiles(input, "*.docx", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else if (File.Exists(input))
        {
            if (!input.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("input must be a .docx file or a folder");
            if (Path.GetFileName(input).StartsWith("~$", StringComparison.Ordinal)) throw new ArgumentException("input is a Word lock file");
            if (Pipeline.PathsEqual(Path.GetDirectoryName(input)!, outRoot)) throw new InvalidOperationException("output folder must not be the input file's folder");
            files = new List<string> { input };
        }
        else throw new FileNotFoundException("input not found: " + input);

        Directory.CreateDirectory(outRoot);
        using var svc = new BatchServices { Paths = paths, WorkRoot = Path.Combine(outRoot, "_work") };
        svc.EnvironmentNote = EnvironmentNote(paths);
        Console.WriteLine($"MathMorph: {files.Count} document(s); threshold SSIM >= {thr:F3}; dry run: {opt.DryRun}; MathType: {(opt.ConvertMathType ? "convert" : "skip")}");
        var results = new List<DocResult>();
        foreach (var f in files)
        {
            Console.Write($"  {Path.GetFileName(f)} ... ");
            var r = Pipeline.ProcessDocument(f, outRoot, opt, svc, basis);
            results.Add(r);
            Console.WriteLine($"{r.Status} converted={r.Count(Status.CONVERTED)} review={r.Count(Status.NEEDS_REVIEW)} unsupported={r.Count(Status.UNSUPPORTED_FORMAT)} excluded={r.Count(Status.EXCLUDED)}" +
                              (r.MathTypeFound > 0 ? $" mathtype={r.MathTypeConverted}/{r.MathTypeFound}" : "") +
                              $" ({r.Elapsed.TotalSeconds:F0}s){(r.Error.Length > 0 ? " " + r.Error : "")}");
        }
        var summary = Reports.WriteBatchSummary(results, outRoot, opt, svc, basis);
        var review = Reports.WriteReviewIndex(results, outRoot, opt);
        File.WriteAllText(Path.Combine(outRoot, "_work", "batch_results.json"), JsonSerializer.Serialize(results.Select(r => new
        {
            r.Name, r.Status, r.Error, r.OutputDocx,
            converted = r.Count(Status.CONVERTED), review = r.Count(Status.NEEDS_REVIEW), unsupported = r.Count(Status.UNSUPPORTED_FORMAT),
            excluded = r.Count(Status.EXCLUDED), noImage = r.Count(Status.NO_IMAGE), pictures = r.Inventory?.Pictures.Count ?? 0,
            gates = r.Gates.Select(g => new { g.Name, g.Result, g.Evidence }),
        }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Summary: " + summary);
        Console.WriteLine("Review:  " + review);
        return results.All(r => r.Status is "OK" or "DRY_RUN") ? 0 : 2;
    }

    private static bool IsInside(string child, string parent)
    {
        var c = Path.GetFullPath(child).TrimEnd('\\') + "\\";
        var p = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnvironmentNote(EnginePaths paths)
    {
        string mml = new[] { @"C:\Program Files\Microsoft Office\root\Office16\MML2OMML.XSL", @"C:\Program Files (x86)\Microsoft Office\root\Office16\MML2OMML.XSL" }
            .FirstOrDefault(File.Exists) ?? "not found (Microsoft Office not installed)";
        var lo = Gates.LibreOfficeCandidates.FirstOrDefault(File.Exists) ?? "not found";
        return $"- OS: {Environment.OSVersion}; .NET runtime {Environment.Version}\n" +
               $"- Python: {paths.Python}\n- Pandoc (LaTeX→OMML and LaTeX→MathML): {paths.Pandoc}\n" +
               $"- MML2OMML.XSL: {mml}\n- LibreOffice: {lo}\n" +
               $"- Open XML validator: {(File.Exists(paths.Validator) ? paths.Validator : "not built")}";
    }

    private static int Compare(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("usage: compare <a.docx> <b.docx>");
        var (differ, onlyA, onlyB, order) = Gates.Compare(args[1], args[2], new SafetyLimits());
        Console.WriteLine(JsonSerializer.Serialize(new { differ, onlyA, onlyB, sameOrder = order, identical = differ.Count == 0 && onlyA.Count == 0 && onlyB.Count == 0 && order }));
        return differ.Count == 0 && onlyA.Count == 0 && onlyB.Count == 0 && order ? 0 : 1;
    }

    /// <summary>Fast pre-check used by the GUI: counts convertible MathType objects without converting anything.</summary>
    private static int MathTypeCount(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("usage: mathtype-count <.docx or folder>");
        var input = Path.GetFullPath(args[1]);
        List<string> files = Directory.Exists(input)
            ? Directory.GetFiles(input, "*.docx", SearchOption.TopDirectoryOnly).Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)).ToList()
            : File.Exists(input) ? new List<string> { input } : throw new FileNotFoundException("input not found: " + input);
        int objects = 0, docs = 0;
        foreach (var f in files)
        {
            try
            {
                var pkg = SafePackage.Open(f, new SafetyLimits());
                int n = Inventory.Scan(pkg, Path.GetFileName(f)).Inventory.Pictures.Count(Pipeline.IsConvertibleMathType);
                objects += n;
                if (n > 0) docs++;
            }
            catch (Exception ex) { Console.Error.WriteLine($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        Console.WriteLine($"MATHTYPE {objects} {docs} {files.Count}");
        return 0;
    }

    private static int MathTypeDump(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("usage: mathtype <a.docx>");
        var pkg = SafePackage.Open(args[1], new SafetyLimits());
        var inv = Inventory.Scan(pkg, Path.GetFileName(args[1])).Inventory;
        int ok = 0, bad = 0;
        foreach (var p in inv.Pictures.Where(Pipeline.IsConvertibleMathType))
        {
            try
            {
                var r = MathType.Convert(pkg.Get(p.OleTarget!)!.Data);
                if (r.Problems.Count == 0) ok++; else bad++;
                Console.WriteLine($"para={p.ParaIndex}\t{p.OleTarget}\t{(r.Problems.Count == 0 ? "OK" : "PROBLEM: " + string.Join("; ", r.Problems))}\t{r.Latex}");
            }
            catch (Exception ex) { bad++; Console.WriteLine($"para={p.ParaIndex}\t{p.OleTarget}\tERROR: {ex.Message}"); }
        }
        Console.WriteLine($"MathType objects: {ok + bad}; readable: {ok}; problems: {bad}");
        return bad == 0 ? 0 : 1;
    }

    private static int InventoryOnly(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("usage: inventory <a.docx>");
        var pkg = SafePackage.Open(args[1], new SafetyLimits());
        var inv = Inventory.Scan(pkg, Path.GetFileName(args[1])).Inventory;
        foreach (var p in inv.Pictures)
            Console.WriteLine($"{p.Part}\t#{p.Ordinal}\tpara={p.ParaIndex}\t{p.Container}\t{p.Kind}\t{p.Format}\tcy={p.ExtentCy}\t{p.RelId}\t{p.AltContent}\t{Reports.CategoryName(p.Category)}\t{p.Reason}\trun={(p.Run == null ? "-" : $"{p.Run.OnlyPictureInRun}/{p.Run.ParagraphOnlyThisPicture}/{p.Run.RunAltContentChoice}")}");
        Console.WriteLine($"native m:oMath={inv.NativeOMath} m:oMathPara={inv.NativeOMathPara}");
        return 0;
    }
}
