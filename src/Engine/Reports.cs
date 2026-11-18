using System.Globalization;
using System.Net;
using System.Text;
using MathMorph.Contracts;

namespace MathMorph.Engine;

public static class Reports
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>CSV cell. Values starting with = + - @ get a leading apostrophe so spreadsheet programs
    /// do not evaluate them as formulas (CSV injection); the LaTeX in the .docx itself is unaffected.</summary>
    public static string Csv(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public static void WriteInventoryCsv(DocResult res, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("part,ordinal,paragraph_index,container,type,extent_cx_emu,extent_cy_emu,height_in,image_format,rel_id,target,paragraph_has_other_content,in_alternate_content,sha256,category,reason");
        foreach (var p in res.Inventory!.Pictures)
            sb.AppendLine(string.Join(",", Csv(p.Part), p.Ordinal, p.ParaIndex, Csv(p.Container), Csv(p.Kind.ToString()), p.ExtentCx, p.ExtentCy,
                (p.ExtentCy / 914400.0).ToString("F3", Inv), Csv(p.Format), Csv(p.RelId), Csv(p.Target), Csv(p.ParagraphHasOtherContent ? "yes" : "no"),
                Csv(p.AltContent.ToString().ToLowerInvariant()), Csv(p.Sha256), Csv(CategoryName(p.Category)), Csv(p.Reason)));
        sb.AppendLine($"# native equations: m:oMath={res.Inventory.NativeOMath}, m:oMathPara={res.Inventory.NativeOMathPara}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static string CategoryName(Category c) => c switch
    {
        Category.Candidate => "CANDIDATE", Category.OlePreview => "OLE_PREVIEW", Category.Excluded => "EXCLUDED",
        Category.UnsupportedFormat => "UNSUPPORTED_FORMAT", Category.Native => "NATIVE", _ => c.ToString()
    };

    public static void WriteReportCsv(DocResult res, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("document,part,paragraph_index,rel_id,image_sha256,category,status,score,latex,note");
        foreach (var d in res.Items)
        {
            var p = d.Picture;
            var status = d.Status.ToString();
            sb.AppendLine(string.Join(",", Csv(res.Name), Csv(p.Part), p.ParaIndex, Csv(p.RelId), Csv(p.Sha256), Csv(CategoryName(p.Category)),
                Csv(status), d.Score >= 0 ? d.Score.ToString("F4", Inv) : "", Csv(d.Latex), Csv(d.Note)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static void WriteDocumentReports(DocResult res, BatchServices svc, ConvertOptions opt)
    {
        Directory.CreateDirectory(res.OutDir);
        if (res.Inventory != null) WriteReportCsv(res, Path.Combine(res.OutDir, "report.csv"));
        File.WriteAllText(Path.Combine(res.OutDir, "FINAL_REPORT.md"), FinalReport(res, svc, opt), new UTF8Encoding(false));
    }

    public static List<double> Scores(IEnumerable<ItemDecision> items) => items.Where(i => i.Score >= 0).Select(i => i.Score).OrderBy(x => x).ToList();

    public static double Pct(List<double> s, double q)
    {
        if (s.Count == 0) return double.NaN;
        var pos = q * (s.Count - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    public static string SsimSummary(List<double> s)
    {
        if (s.Count == 0) return "no scored items";
        var sb = new StringBuilder();
        sb.AppendLine($"n={s.Count}; min={s[0]:F3}; p10={Pct(s, .1):F3}; p25={Pct(s, .25):F3}; median={Pct(s, .5):F3}; p75={Pct(s, .75):F3}; p90={Pct(s, .9):F3}; max={s[^1]:F3}");
        sb.AppendLine();
        sb.AppendLine("| SSIM bin | count |");
        sb.AppendLine("|---|---|");
        for (int b = 0; b < 10; b++)
        {
            double lo = b / 10.0, hi = (b + 1) / 10.0;
            int c = s.Count(x => x >= lo && (b == 9 ? x <= hi : x < hi));
            if (c > 0) sb.AppendLine($"| {lo:F1}–{hi:F1} | {c} |");
        }
        int neg = s.Count(x => x < 0);
        return sb.ToString();
    }

    private static string FinalReport(DocResult res, BatchServices svc, ConvertOptions opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Final report — {res.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Status: **{res.Status}**{(res.Error.Length > 0 ? " — " + res.Error : "")}");
        sb.AppendLine($"- Input: `{res.InputPath}` (read only, never modified)");
        sb.AppendLine($"- Output: `{res.OutputDocx ?? "(none)"}`");
        sb.AppendLine($"- Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, elapsed {res.Elapsed.TotalSeconds:F1} s, dry run: {opt.DryRun}");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine(svc.EnvironmentNote);
        sb.AppendLine($"- pix2text: {svc.Pix2TextVersion} (model mfr-1.5, ONNX, CPU); model load {svc.ModelLoadSeconds:F1} s");
        sb.AppendLine($"- Renderer for verification: {svc.Browser} (MathML from local Pandoc)");
        sb.AppendLine();
        if (res.Inventory == null)
        {
            sb.AppendLine("No inventory (document not processed).");
            return sb.ToString();
        }
        var inv = res.Inventory;
        sb.AppendLine("## Inventory");
        sb.AppendLine($"- Picture elements: {inv.Pictures.Count}");
        foreach (var g in inv.Pictures.GroupBy(p => CategoryName(p.Category)).OrderBy(g => g.Key))
            sb.AppendLine($"  - {g.Key}: {g.Count()}");
        foreach (var g in inv.Pictures.GroupBy(p => p.Container).OrderBy(g => g.Key))
            sb.AppendLine($"  - container {g.Key}: {g.Count()}");
        sb.AppendLine($"- Existing native equations: m:oMath={inv.NativeOMath}, m:oMathPara={inv.NativeOMathPara}");
        sb.AppendLine($"- Distinct candidate images (SHA-256): {inv.Pictures.Where(p => p.Category is Category.Candidate or Category.OlePreview).Select(p => p.Sha256).Distinct().Count()}");
        sb.AppendLine("- Per-picture detail: `inventory.csv`; per-item decisions: `report.csv`.");
        sb.AppendLine();
        sb.AppendLine("## Recognition (pix2text, local)");
        var rate = res.RecognitionAttempted == 0 ? double.NaN : 100.0 * res.RecognitionOk / res.RecognitionAttempted;
        sb.AppendLine($"- Attempted: {res.RecognitionAttempted}; returned LaTeX without error: {res.RecognitionOk} ({rate:F1}%). Measured: count of worker responses with ok=true over items sent (logo-like images excluded by content statistics are not counted as successes).");
        var times = res.Items.Where(i => i.RecognitionSeconds > 0).Select(i => i.RecognitionSeconds).OrderBy(x => x).ToList();
        if (times.Count > 0)
            sb.AppendLine($"- Time per image (worker wall clock, first occurrence of each distinct image; cache hits repeat it): median {Pct(times, .5):F2} s, max {times[^1]:F2} s, total {res.RecognitionSeconds:F1} s");
        sb.AppendLine();
        sb.AppendLine("## Round-trip score distribution (SSIM)");
        sb.AppendLine("Measured by rendering each LaTeX string locally (Pandoc MathML in headless Edge), cropping both images to ink, resampling to 32 px height with the original's aspect ratio, Gaussian blur σ=1.5, SSIM (scikit-image). Display and inline renderings are both tried; the higher score is kept.");
        sb.AppendLine();
        sb.AppendLine(SsimSummary(Scores(res.Items)));
        sb.AppendLine();
        sb.AppendLine("## Visual check");
        sb.AppendLine(VisualCheckSection(svc.Paths.Root, res.Name));
        sb.AppendLine();
        sb.AppendLine("## Threshold");
        sb.AppendLine($"- Auto-accept threshold: SSIM ≥ {res.Threshold:F3}");
        sb.AppendLine($"- Basis: {res.ThresholdBasis}");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine("| Status | Count |");
        sb.AppendLine("|---|---|");
        foreach (Status s in Enum.GetValues<Status>()) sb.AppendLine($"| {s} | {res.Count(s)} |");
        sb.AppendLine($"| (OLE previews among the above) | {res.Items.Count(i => i.Picture.Category == Category.OlePreview)} |");
        sb.AppendLine();
        sb.AppendLine("## Gates");
        if (res.Gates.Count == 0) sb.AppendLine("No per-document gates were run.");
        else
        {
            sb.AppendLine("| Gate | Result | Evidence |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in res.Gates) sb.AppendLine($"| {g.Name} | {g.Result} | {g.Evidence.Replace("|", "/")} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Decisions made without asking");
        sb.AppendLine("Project-wide decisions are in `docs\\DECISIONS.md` in the MathMorph folder. The ones that affect this document:");
        sb.AppendLine("- Only the run that holds a converted picture is replaced; every other zip entry is copied byte for byte. The image part and its relationship stay in the package, unused.");
        sb.AppendLine("- A paragraph that holds only the picture gets `m:oMathPara` (display). Otherwise the picture becomes an inline `m:oMath` at the same position.");
        sb.AppendLine("- Pictures inside tracked changes, in runs that also hold other content, or in unsupported parents are left as pictures (NEEDS_REVIEW).");
        sb.AppendLine("- OMML has no alt-text attribute, so the picture's original docPr name and description are not carried into the equation. The picture's relationship ID and SHA-256 are in report.csv.");
        foreach (var d in res.Decisions) sb.AppendLine("- " + d);
        sb.AppendLine();
        sb.AppendLine("## Risks");
        sb.AppendLine("- An SSIM score compares the shape of a re-rendered equation with the original picture. It does not prove that the LaTeX is semantically correct. Symbols that look alike (for example l/1, O/0, ν/v) can pass.");
        sb.AppendLine("- The renderer's font (Cambria Math) differs from the fonts in the original pictures, which lowers SSIM even for correct LaTeX.");
        foreach (var r in res.Risks) sb.AppendLine("- " + r);
        sb.AppendLine();
        sb.AppendLine("## Items needing human review (by paragraph)");
        var rev = res.Items.Where(i => i.Status == Status.NEEDS_REVIEW).OrderBy(i => i.Picture.Part).ThenBy(i => i.Picture.ParaIndex).ToList();
        if (rev.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Part | Paragraph | rId | Score | Reason | Recognized LaTeX |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var i in rev)
                sb.AppendLine($"| {i.Picture.Part} | {i.Picture.ParaIndex} | {i.Picture.RelId} | {(i.Score >= 0 ? i.Score.ToString("F3", Inv) : "—")} | {Md(i.Note)} | `{Md(i.Latex)}` |");
        }
        return sb.ToString();
    }

    private static string Md(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("`", "'");

    public static string VisualCheckSection(string root, string docName)
    {
        var f = Path.Combine(root, "out", "verify", "visual_check.csv");
        if (!File.Exists(f)) return "No visual check file (out\\verify\\visual_check.csv) yet.";
        var rows = File.ReadAllLines(f).Skip(1).Select(ParseCsvLine).Where(r => r.Count >= 6).ToList();
        var mine = rows.Where(r => r[1] == docName).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Automated check by the lead engineer (an AI model), not a human review: the original and rendered PNGs were compared side by side for a random sample (fixed seed). Batch sample size: {rows.Count}; items from this document: {mine.Count}.");
        foreach (var g in mine.GroupBy(r => r[5]).OrderBy(g => g.Key)) sb.AppendLine($"- {g.Key}: {g.Count()}");
        return sb.ToString();
    }

    public static List<string> ParseCsvLine(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }

    // ---------------------------------------------------------------- batch outputs

    public static string UniquePath(string path, bool force)
    {
        if (force || !File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(path));
    }

    public static string WriteBatchSummary(List<DocResult> docs, string outRoot, ConvertOptions opt, BatchServices svc, string thresholdBasis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Batch summary");
        sb.AppendLine();
        sb.AppendLine($"Run {DateTime.Now:yyyy-MM-dd HH:mm:ss}; output root `{outRoot}`; dry run: {opt.DryRun}; threshold SSIM ≥ {opt.MinScore:F3} ({thresholdBasis}).");
        sb.AppendLine();
        sb.AppendLine("| Document | Status | Pictures | Candidates | CONVERTED | NEEDS_REVIEW | UNSUPPORTED | EXCLUDED | NO_IMAGE | Threshold | Gates |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var d in docs)
        {
            int pics = d.Inventory?.Pictures.Count ?? 0;
            int cands = d.Inventory?.Pictures.Count(p => p.Category is Category.Candidate or Category.OlePreview) ?? 0;
            var gates = d.Gates.Count == 0 ? "—" : string.Join(" ", d.Gates.Select(g => g.Name.Replace("G3.", "") + ":" + g.Result));
            sb.AppendLine($"| {Md(d.Name)} | {d.Status}{(d.Error.Length > 0 ? " (" + Md(d.Error) + ")" : "")} | {pics} | {cands} | {d.Count(Status.CONVERTED)} | {d.Count(Status.NEEDS_REVIEW)} | {d.Count(Status.UNSUPPORTED_FORMAT)} | {d.Count(Status.EXCLUDED)} | {d.Count(Status.NO_IMAGE)} | {d.Threshold:F3} | {gates} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Totals");
        int ok = docs.Count(d => d.Status is "OK" or "DRY_RUN");
        sb.AppendLine($"- Documents: {docs.Count}; succeeded: {ok}; failed/skipped: {docs.Count - ok}");
        foreach (Status s in Enum.GetValues<Status>()) sb.AppendLine($"- {s}: {docs.Sum(d => d.Count(s))}");
        var all = Scores(docs.SelectMany(d => d.Items));
        sb.AppendLine($"- SSIM over all scored items: {SsimSummary(all).Split('\n')[0]}");
        int att = docs.Sum(d => d.RecognitionAttempted), rok = docs.Sum(d => d.RecognitionOk);
        sb.AppendLine($"- Recognition: {rok}/{att} returned LaTeX without error{(att > 0 ? $" ({100.0 * rok / att:F1}%)" : "")}");
        sb.AppendLine();
        sb.AppendLine("## Next steps");
        sb.AppendLine("1. Open `review\\index.html` and check each NEEDS_REVIEW item; fix those equations manually in Word.");
        sb.AppendLine("2. Spot-check CONVERTED equations in each output document against the original pictures, since SSIM does not prove semantic correctness.");
        sb.AppendLine("3. To convert more automatically, have a person label a larger sample, then re-derive the threshold with `--min-score`.");
        sb.AppendLine("4. Convert EMF/WMF equation pictures (UNSUPPORTED_FORMAT) manually, or export them to PNG and re-run.");
        var path = UniquePath(Path.Combine(outRoot, "BATCH_SUMMARY.md"), opt.Force);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    public static string WriteReviewIndex(List<DocResult> docs, string outRoot, ConvertOptions opt)
    {
        var dir = Path.Combine(outRoot, "review");
        var imgDir = Path.Combine(dir, "img");
        Directory.CreateDirectory(imgDir);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                  "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'\">" +
                  "<title>Equations to review</title><style>" +
                  ":root{--bg:#fff;--fg:#1b1b1b;--muted:#666;--line:#ddd;--card:#fafafa}" +
                  "@media (prefers-color-scheme:dark){:root{--bg:#161616;--fg:#eee;--muted:#aaa;--line:#333;--card:#1f1f1f}}" +
                  "body{background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,Segoe UI,sans-serif;margin:0;padding:16px;max-width:1100px}" +
                  "h1{font-size:20px}h2{font-size:16px;margin-top:28px}table{border-collapse:collapse;width:100%}" +
                  "td,th{border-bottom:1px solid var(--line);padding:6px;vertical-align:top;text-align:left}" +
                  "img{max-width:360px;background:#fff;border:1px solid var(--line)}code{white-space:pre-wrap;word-break:break-all}" +
                  ".m{color:var(--muted)}</style></head><body>");
        int total = docs.Sum(d => d.Count(Status.NEEDS_REVIEW));
        sb.Append($"<h1>Equations to review ({total})</h1><p class=\"m\">Generated {DateTime.Now:yyyy-MM-dd HH:mm}. These pictures were left unchanged. 'Render' shows the recognized LaTeX re-rendered locally; compare it with the original.</p>");
        foreach (var d in docs)
        {
            var items = d.Items.Where(i => i.Status == Status.NEEDS_REVIEW).OrderBy(i => i.Picture.ParaIndex).ToList();
            if (items.Count == 0) continue;
            sb.Append($"<h2>{WebUtility.HtmlEncode(d.Name)} — {items.Count}</h2><table><tr><th>Para</th><th>Original</th><th>Render</th><th>Score</th><th>Reason / LaTeX</th></tr>");
            foreach (var i in items)
            {
                string Copy(string? src, string suffix)
                {
                    if (src == null || !File.Exists(src)) return "";
                    var name = Pipeline.SafeName(Path.GetFileNameWithoutExtension(d.Name))[..Math.Min(40, Pipeline.SafeName(Path.GetFileNameWithoutExtension(d.Name)).Length)]
                               + "_" + Path.GetFileNameWithoutExtension(src) + suffix + Path.GetExtension(src);
                    File.Copy(src, Path.Combine(imgDir, name), true);
                    return $"<img alt=\"\" src=\"img/{Uri.EscapeDataString(name)}\">";
                }
                sb.Append($"<tr><td>{i.Picture.ParaIndex}<div class=\"m\">{WebUtility.HtmlEncode(i.Picture.RelId ?? "")}</div></td><td>{Copy(i.OriginalImagePath, "_o")}</td><td>{Copy(i.RenderPath, "_r")}</td>" +
                          $"<td>{(i.Score >= 0 ? i.Score.ToString("F3", Inv) : "—")}</td><td>{WebUtility.HtmlEncode(i.Note)}<br><code>{WebUtility.HtmlEncode(i.Latex)}</code></td></tr>");
            }
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        var path = UniquePath(Path.Combine(dir, "index.html"), opt.Force);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
