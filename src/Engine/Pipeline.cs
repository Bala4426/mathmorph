using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using MathMorph.Contracts;
using MathMorph.Recognition;

namespace MathMorph.Engine;

public sealed class GateResult
{
    public string Name { get; init; } = "";
    public string Result { get; set; } = "SKIPPED";   // PASS, FAIL, SKIPPED, N/A
    public string Evidence { get; set; } = "";
}

public sealed class DocResult
{
    public required string InputPath { get; init; }
    public string Name => Path.GetFileName(InputPath);
    public string OutDir { get; set; } = "";
    public string? OutputDocx { get; set; }
    public string Status { get; set; } = "OK";        // OK, FAILED, SKIPPED_EXISTS, REJECTED_LIMITS, DRY_RUN, GATE3_FAILED
    public string Error { get; set; } = "";
    public DocumentInventory? Inventory { get; set; }
    public List<ItemDecision> Items { get; } = new();
    public List<GateResult> Gates { get; } = new();
    public List<string> Decisions { get; } = new();
    public List<string> Risks { get; } = new();
    public double Threshold { get; set; }
    public string ThresholdBasis { get; set; } = "";
    public double RecognitionSeconds { get; set; }
    public int RecognitionAttempted { get; set; }
    public int RecognitionOk { get; set; }
    public int MathTypeFound { get; set; }
    public int MathTypeConverted { get; set; }
    public HashSet<string> ChangedParts { get; } = new();
    public TimeSpan Elapsed { get; set; }
    public int Count(Status s) => Items.Count(i => i.Status == s);
}

/// <summary>Shared, lazily-started services for a batch run.</summary>
public sealed class BatchServices : IDisposable
{
    public required EnginePaths Paths { get; init; }
    public required string WorkRoot { get; init; }
    private Pix2TextWorker? _p2t;
    private VerifyWorker? _ver;
    private readonly Dictionary<string, RecognitionResult> _recCache = new();
    private readonly Dictionary<string, VerifyResult> _verCache = new();
    public string EnvironmentNote { get; set; } = "";
    public string Pix2TextVersion => _p2t?.Pix2TextVersion ?? "not started";
    public double ModelLoadSeconds => _p2t?.LoadSeconds ?? -1;
    public string Browser => _ver?.Browser ?? "not started";
    public string? WorkerStartError { get; private set; }
    public string? VerifierStartError { get; private set; }

    // Successful results are also kept on disk (<WorkRoot>\cache\*.jsonl), so a re-run on the same output folder
    // (e.g. "Apply corrections & re-convert") reuses them instead of recognising and rendering every picture again.
    private bool _diskLoaded;
    private string CacheDir => Path.Combine(WorkRoot, "cache");
    private string RecCacheFile => Path.Combine(CacheDir, "recognition.jsonl");
    private string VerCacheFile => Path.Combine(CacheDir, "verify.jsonl");
    private sealed record RecEntry(string Sha, RecognitionResult R);
    private sealed record VerEntry(string Key, VerifyResult R);

    private void LoadDiskCache()
    {
        if (_diskLoaded) return;
        _diskLoaded = true;
        try
        {
            if (File.Exists(RecCacheFile))
                foreach (var l in File.ReadLines(RecCacheFile))
                    try { var e = System.Text.Json.JsonSerializer.Deserialize<RecEntry>(l); if (e?.R is { Ok: true }) _recCache[e.Sha] = e.R; } catch { }
            if (File.Exists(VerCacheFile))
                foreach (var l in File.ReadLines(VerCacheFile))
                    try { var e = System.Text.Json.JsonSerializer.Deserialize<VerEntry>(l); if (e?.R is { Ok: true }) _verCache[e.Key] = e.R; } catch { }
        }
        catch { /* a damaged cache only costs time */ }
    }

    private void Append(string file, object entry)
    {
        try { Directory.CreateDirectory(CacheDir); File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(entry) + "\n"); }
        catch { }
    }

    public RecognitionResult Recognize(string sha, string imagePath)
    {
        LoadDiskCache();
        if (_recCache.TryGetValue(sha, out var c)) return c;
        if (_p2t == null && WorkerStartError == null)
        {
            try { _p2t = new Pix2TextWorker(Paths, Path.Combine(WorkRoot, "logs", "pix2text_worker.stderr.log")); _p2t.Start(); }
            catch (Exception ex) { WorkerStartError = ex.Message; _p2t?.Dispose(); _p2t = null; }
        }
        var r = _p2t == null ? new RecognitionResult { Path = imagePath, Ok = false, Error = "recognizer unavailable: " + WorkerStartError }
                             : _p2t.Recognize(imagePath);
        if (_p2t != null) { _recCache[sha] = r; if (r.Ok) Append(RecCacheFile, new RecEntry(sha, r)); }
        return r;
    }

    public VerifyResult Verify(string sha, string imagePath, string latex, string renderOut)
    {
        LoadDiskCache();
        var key = sha + "\n" + latex;
        if (_verCache.TryGetValue(key, out var c) && File.Exists(c.RenderPath))
        {
            if (!string.Equals(Path.GetFullPath(c.RenderPath), Path.GetFullPath(renderOut), StringComparison.OrdinalIgnoreCase))
            {
                // same picture in another document: give this document its own copy of the render
                try { Directory.CreateDirectory(Path.GetDirectoryName(renderOut)!); File.Copy(c.RenderPath, renderOut, true); return new VerifyResult { Ok = c.Ok, Ssim = c.Ssim, Error = c.Error, RenderPath = renderOut }; }
                catch { }
            }
            return c;
        }
        if (_ver == null && VerifierStartError == null)
        {
            try { _ver = new VerifyWorker(Paths, Path.Combine(WorkRoot, "logs", "verify_worker.stderr.log")); _ver.Start(); }
            catch (Exception ex) { VerifierStartError = ex.Message; _ver?.Dispose(); _ver = null; }
        }
        var r = _ver == null ? new VerifyResult { Ok = false, Error = "verifier unavailable: " + VerifierStartError } : _ver.Verify(imagePath, latex, renderOut);
        if (_ver != null) { _verCache[key] = r; if (r.Ok) Append(VerCacheFile, new VerEntry(key, r)); }
        return r;
    }

    public void Dispose() { _p2t?.Dispose(); _ver?.Dispose(); }
}

public static class Pipeline
{
    private static readonly HashSet<XName> InlineMathParents = new()
    {
        Ns.W + "p", Ns.W + "hyperlink", Ns.W + "sdtContent", Ns.W + "smartTag", Ns.W + "customXml", Ns.W + "fldSimple"
    };

    public static double ColorfulLimit = 0.05;   // > 5% saturated pixels => logo/figure
    public static double MidtoneLimit = 0.30;    // > 30% mid-gray pixels => photo/figure

    public static DocResult ProcessDocument(string input, string outRoot, ConvertOptions opt, BatchServices svc, string thresholdBasis)
    {
        var sw = Stopwatch.StartNew();
        var res = new DocResult { InputPath = Path.GetFullPath(input), Threshold = opt.MinScore, ThresholdBasis = thresholdBasis };
        var baseName = Path.GetFileNameWithoutExtension(input);
        res.OutDir = Path.Combine(outRoot, SafeName(baseName));
        var outDocx = Path.Combine(res.OutDir, Path.GetFileName(input));
        var work = Path.Combine(svc.WorkRoot, SafeName(baseName));
        try
        {
            if (Directory.Exists(res.OutDir) && Directory.EnumerateFileSystemEntries(res.OutDir).Any() && !opt.Force)
            {
                res.Status = "SKIPPED_EXISTS";
                res.Error = "output folder exists; use --force to overwrite";
                return res;
            }
            if (PathsEqual(Path.GetFullPath(outDocx), res.InputPath)) throw new InvalidOperationException("input and output paths match");
            Directory.CreateDirectory(res.OutDir);
            if (opt.Force) foreach (var f in Directory.GetFiles(res.OutDir)) File.Delete(f);
            Directory.CreateDirectory(work);

            SafePackage pkg;
            try { pkg = SafePackage.Open(input, opt.Limits); }
            catch (PackageLimitException ex)
            {
                res.Status = "REJECTED_LIMITS"; res.Error = ex.Message;
                Reports.WriteDocumentReports(res, svc, opt);
                return res;
            }
            var scan = Inventory.Scan(pkg, Path.GetFileName(input));
            res.Inventory = scan.Inventory;
            Reports.WriteInventoryCsv(res, Path.Combine(res.OutDir, "inventory.csv"));

            // extract candidate images (by hash) and recognize each distinct image once
            var imgDir = Path.Combine(work, "img");
            Directory.CreateDirectory(imgDir);
            var all = scan.Parts.SelectMany(p => p.Items.Select(i => (Scan: p, i.Info, i.Element))).ToList();
            var converter = new OmmlConverter(svc.Paths.Pandoc, Path.Combine(work, "tmp"));
            var replacements = new Dictionary<Inventory.PartScan, List<Rewriter.Replacement>>();
            foreach (var (pscan, pic, el) in all)
            {
                var d = new ItemDecision { Picture = pic };
                res.Items.Add(d);
                if (opt.ConvertMathType && IsConvertibleMathType(pic))
                {
                    // keep the object's preview picture (usually WMF/EMF) so Review can show the original next to the result
                    if (pic.Target != null && pic.Sha256 != null && pkg.Get(pic.Target) is { } preview)
                    {
                        var pv = Path.Combine(imgDir, pic.Sha256 + "." + (string.IsNullOrEmpty(pic.Format) ? "bin" : pic.Format));
                        if (!File.Exists(pv)) File.WriteAllBytes(pv, preview.Data);
                        d.OriginalImagePath = pv;
                    }
                    var mtOmml = ConvertMathTypeItem(pkg, pic, el, d, converter, res);
                    if (mtOmml != null)
                    {
                        if (!replacements.TryGetValue(pscan, out var lm)) replacements[pscan] = lm = new();
                        lm.Add(new Rewriter.Replacement(pic, el, mtOmml));
                    }
                    continue;
                }
                if (Inventory.IsNoImage(pic)) { d.Status = Status.NO_IMAGE; d.Note = pic.Reason; continue; }
                switch (pic.Category)
                {
                    case Category.Excluded: case Category.Native: d.Status = Status.EXCLUDED; d.Note = pic.Reason; continue;
                    case Category.UnsupportedFormat: d.Status = Status.UNSUPPORTED_FORMAT; d.Note = pic.Reason; continue;
                }
                var entry = pkg.Get(pic.Target!)!;
                var imgPath = Path.Combine(imgDir, pic.Sha256 + (pic.Format == "png" ? ".png" : ".jpg"));
                if (!File.Exists(imgPath)) File.WriteAllBytes(imgPath, entry.Data);
                d.OriginalImagePath = imgPath;

                if (opt.LatexOverrides.TryGetValue(pic.Sha256!, out var fixedLatex))
                {
                    // human-supplied LaTeX: no recognition, no score threshold; structure and OMML checks still apply
                    d.Latex = fixedLatex;
                    var vr = svc.Verify(pic.Sha256!, imgPath, fixedLatex, Path.Combine(work, "render", pic.Sha256 + "_" + StableHash(fixedLatex) + ".png"));
                    if (vr.Ok) { d.Score = vr.Ssim; d.RenderPath = vr.RenderPath; }
                    var whyO = NotReplaceable(pic, el);
                    if (whyO != null) { d.Status = Status.NEEDS_REVIEW; d.Note = "override: " + whyO; continue; }
                    try
                    {
                        var om = converter.Convert(fixedLatex);
                        d.Omml = om.ToString(SaveOptions.DisableFormatting);
                        d.Status = Status.CONVERTED;
                        d.Note = "LaTeX supplied by proofreader (override)";
                        if (!replacements.TryGetValue(pscan, out var l2)) replacements[pscan] = l2 = new();
                        l2.Add(new Rewriter.Replacement(pic, el, om));
                    }
                    catch (Exception ex) { d.Status = Status.NEEDS_REVIEW; d.Note = "override OMML conversion failed: " + ex.Message; }
                    continue;
                }

                var rec = svc.Recognize(pic.Sha256!, imgPath);
                res.RecognitionAttempted++;
                d.RecognitionSeconds = rec.Seconds;
                res.RecognitionSeconds += rec.Seconds;
                if (rec.ColorfulFraction > ColorfulLimit || rec.MidtoneFraction > MidtoneLimit)
                {
                    pic.Category = Category.Excluded;
                    pic.Reason = $"looks like logo/figure (colorful={rec.ColorfulFraction:F3}, midtone={rec.MidtoneFraction:F3})";
                    d.Status = Status.EXCLUDED; d.Note = pic.Reason;
                    res.RecognitionAttempted--; // not an equation: excluded from the recognition success rate
                    res.RecognitionSeconds -= rec.Seconds;
                    continue;
                }
                if (!rec.Ok) { d.Status = Status.NEEDS_REVIEW; d.Note = "recognition failed: " + rec.Error; continue; }
                res.RecognitionOk++;
                d.Latex = rec.Latex;

                var render = Path.Combine(work, "render", pic.Sha256 + "_" + StableHash(rec.Latex) + ".png");
                var v = svc.Verify(pic.Sha256!, imgPath, rec.Latex, render);
                if (!v.Ok) { d.Status = Status.NEEDS_REVIEW; d.Note = "verification render failed: " + v.Error; continue; }
                d.Score = v.Ssim;
                d.RenderPath = v.RenderPath;
                if (v.Ssim < opt.MinScore) { d.Status = Status.NEEDS_REVIEW; d.Note = $"score {v.Ssim:F4} below threshold {opt.MinScore:F2}"; continue; }

                // structural conditions for an in-place replacement
                var why = NotReplaceable(pic, el);
                if (why != null) { d.Status = Status.NEEDS_REVIEW; d.Note = why; continue; }
                XElement omml;
                try { omml = converter.Convert(rec.Latex); }
                catch (Exception ex) { d.Status = Status.NEEDS_REVIEW; d.Note = "OMML conversion failed: " + ex.Message; continue; }
                d.Omml = omml.ToString(SaveOptions.DisableFormatting);
                d.Status = Status.CONVERTED;
                d.Note = pic.Kind == PictureKind.Ole ? "OLE preview replaced" : "";
                if (!replacements.TryGetValue(pscan, out var list)) replacements[pscan] = list = new();
                list.Add(new Rewriter.Replacement(pic, el, omml));
            }

            var newParts = new Dictionary<string, byte[]>();
            foreach (var (pscan, list) in replacements)
            {
                try
                {
                    var text = Rewriter.Rewrite(pscan, list);
                    newParts[pscan.Part] = SafeXml.Encode(text, pscan.Encoding, pscan.Preamble);
                }
                catch (Exception ex)
                {
                    // never write a half-edited part: all items of this part go back to review
                    foreach (var r in list)
                    {
                        var d = res.Items.First(i => i.Picture == r.Picture);
                        d.Status = Status.NEEDS_REVIEW; d.Note = "rewrite failed: " + ex.Message; d.Omml = null;
                    }
                }
            }
            res.ChangedParts.UnionWith(newParts.Keys);

            if (opt.DryRun)
            {
                res.Status = "DRY_RUN";
                res.Gates.Add(new GateResult { Name = "Gate 3", Result = "N/A", Evidence = "dry run: no output written" });
            }
            else
            {
                pkg.Save(outDocx, newParts);
                res.OutputDocx = outDocx;
                if (opt.RunGateChecks) Gates.RunGate3(res, input, outDocx, svc, opt, work);
                if (res.Gates.Any(g => g.Result == "FAIL"))
                {
                    res.Status = "GATE3_FAILED";
                    var failed = Path.Combine(res.OutDir, Path.GetFileNameWithoutExtension(input) + ".GATE3_FAILED.docx");
                    File.Move(outDocx, failed, true);
                    res.OutputDocx = failed;
                }
            }
        }
        catch (Exception ex)
        {
            res.Status = "FAILED";
            res.Error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            res.Elapsed = sw.Elapsed;
            // never touch an existing output folder that was skipped for lack of --force
            if (res.Status != "SKIPPED_EXISTS")
            try { Directory.CreateDirectory(res.OutDir); Reports.WriteDocumentReports(res, svc, opt); }
            catch (Exception ex) { res.Error += " | report write failed: " + ex.Message; }
        }
        return res;
    }

    /// <summary>A MathType OLE object in the body, a table or a text box (never header/footer/notes, never an mc:Fallback copy).</summary>
    public static bool IsConvertibleMathType(PictureInfo pic) =>
        pic.Kind == PictureKind.Ole && MathType.IsMathTypeProgId(pic.OleProgId) && pic.OleTarget != null &&
        pic.Container is "body" or "table" or "textbox" && pic.AltContent != AltContentBranch.Fallback && pic.Category != Category.Native;

    /// <summary>Converts one MathType object from its embedded MTEF data. Exact: no recognition and no score threshold.
    /// Any part of the equation that cannot be represented leaves the item as NEEDS_REVIEW (picture kept).</summary>
    private static XElement? ConvertMathTypeItem(SafePackage pkg, PictureInfo pic, XElement el, ItemDecision d, OmmlConverter converter, DocResult res)
    {
        res.MathTypeFound++;
        var bin = pkg.Get(pic.OleTarget!);
        if (bin == null) { d.Status = Status.NEEDS_REVIEW; d.Note = "MathType: embedded object part missing (" + pic.OleTarget + ")"; return null; }
        MathType.Result mt;
        try { mt = MathType.Convert(bin.Data); }
        catch (Exception ex) { d.Status = Status.NEEDS_REVIEW; d.Note = "MathType: could not read equation data: " + ex.Message; return null; }
        d.Latex = mt.Latex;
        if (mt.Problems.Count > 0) { d.Status = Status.NEEDS_REVIEW; d.Note = "MathType: " + string.Join("; ", mt.Problems); return null; }
        var why = NotReplaceable(pic, el);
        if (why != null) { d.Status = Status.NEEDS_REVIEW; d.Note = "MathType: " + why; return null; }
        try
        {
            var omml = converter.Convert(mt.Latex);
            MathType.WrapLimitFunctions(omml);
            OmmlConverter.Validate(omml);
            d.Omml = omml.ToString(SaveOptions.DisableFormatting);
            d.Status = Status.CONVERTED;
            d.Note = $"MathType equation converted from its embedded data ({pic.OleProgId}, MTEF {mt.Version}, {mt.Application})";
            res.MathTypeConverted++;
            return omml;
        }
        catch (Exception ex) { d.Status = Status.NEEDS_REVIEW; d.Note = "MathType: OMML conversion failed: " + ex.Message; return null; }
    }

    /// <summary>Returns why a picture's run cannot be replaced in place, or null if it can.</summary>
    public static string? NotReplaceable(PictureInfo pic, XElement el)
    {
        var loc = pic.Run;
        if (loc == null) return "picture is not inside a run";
        if (!loc.OnlyPictureInRun) return "run holds other content besides the picture";
        var run = el.Ancestors(Ns.W + "r").First();
        if (run.Ancestors().Any(a => a.Name == Ns.W + "ins" || a.Name == Ns.W + "del" || a.Name == Ns.W + "moveFrom" || a.Name == Ns.W + "moveTo"))
            return "picture is inside a tracked change (w:ins/w:del)";
        var parent = run.Parent!;
        if (InlineMathParents.Contains(parent.Name)) return null;
        if (parent.Name == Ns.MC + "Choice" && parent.Parent?.Parent?.Name == Ns.W + "p" && !loc.RunAltContentChoice) return null;
        return "unsupported run parent " + parent.Name.LocalName;
    }

    public static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().TrimEnd('.');
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string StableHash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
}
