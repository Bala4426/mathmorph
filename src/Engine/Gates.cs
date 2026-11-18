using System.Diagnostics;
using System.Text.Json;
using MathMorph.Contracts;

namespace MathMorph.Engine;

public static class Gates
{
    public static readonly string[] LibreOfficeCandidates =
    {
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
    };

    /// <summary>Entry-by-entry comparison of two packages (uncompressed bytes). Returns names that differ,
    /// plus names missing on either side, plus whether entry order matches.</summary>
    public static (List<string> Differ, List<string> OnlyA, List<string> OnlyB, bool SameOrder) Compare(string a, string b, SafetyLimits lim)
    {
        var pa = SafePackage.Open(a, lim);
        var pb = SafePackage.Open(b, lim);
        var differ = new List<string>();
        foreach (var e in pa.Entries)
        {
            var o = pb.Get(e.Name);
            if (o != null && !e.Data.AsSpan().SequenceEqual(o.Data)) differ.Add(e.Name);
        }
        var onlyA = pa.Entries.Where(e => pb.Get(e.Name) == null).Select(e => e.Name).ToList();
        var onlyB = pb.Entries.Where(e => pa.Get(e.Name) == null).Select(e => e.Name).ToList();
        bool order = pa.Entries.Select(e => e.Name).SequenceEqual(pb.Entries.Select(e => e.Name));
        return (differ, onlyA, onlyB, order);
    }

    public static void RunGate3(DocResult res, string input, string output, BatchServices svc, ConvertOptions opt, string work)
    {
        // 1. unit tests (result file written by tests\Unit runner)
        var ut = new GateResult { Name = "G3.unit_tests" };
        var utFile = Path.Combine(svc.Paths.Root, "tests", "unit_result.json");
        if (File.Exists(utFile))
        {
            try
            {
                using var j = JsonDocument.Parse(File.ReadAllText(utFile));
                int passed = j.RootElement.GetProperty("passed").GetInt32(), failed = j.RootElement.GetProperty("failed").GetInt32();
                var when = j.RootElement.TryGetProperty("timestamp", out var t) ? t.GetString() : "?";
                ut.Result = failed == 0 && passed > 0 ? "PASS" : "FAIL";
                ut.Evidence = $"{passed} passed, {failed} failed (run {when}, {utFile})";
            }
            catch (Exception ex) { ut.Result = "FAIL"; ut.Evidence = "unreadable unit test result: " + ex.Message; }
        }
        else { ut.Result = "SKIPPED"; ut.Evidence = "no unit test result file at " + utFile; }
        res.Gates.Add(ut);

        // 2. byte identity: every entry identical except the changed parts
        var bi = new GateResult { Name = "G3.byte_identity" };
        try
        {
            var (differ, onlyA, onlyB, order) = Compare(input, output, opt.Limits);
            var unexpected = differ.Where(d => !res.ChangedParts.Contains(d)).ToList();
            var missingChange = res.ChangedParts.Where(c => !differ.Contains(c)).ToList();
            bool ok = unexpected.Count == 0 && onlyA.Count == 0 && onlyB.Count == 0 && order && missingChange.Count == 0;
            bi.Result = ok ? "PASS" : "FAIL";
            var total = SafePackage.Open(input, opt.Limits).Entries.Count;
            bi.Evidence = $"{total} entries; changed: [{string.Join(", ", differ)}]; expected changed: [{string.Join(", ", res.ChangedParts)}]" +
                          (unexpected.Count > 0 ? $"; UNEXPECTED: [{string.Join(", ", unexpected)}]" : "") +
                          (onlyA.Count + onlyB.Count > 0 ? $"; missing/added entries: {onlyA.Count}/{onlyB.Count}" : "") +
                          (order ? "; entry order identical" : "; ENTRY ORDER DIFFERS");
        }
        catch (Exception ex) { bi.Result = "FAIL"; bi.Evidence = ex.Message; }
        res.Gates.Add(bi);

        // 3. structural re-check: output parses and the converted pictures are gone, nothing else
        var rc = new GateResult { Name = "G3.reinventory" };
        try
        {
            var outPkg = SafePackage.Open(output, opt.Limits);
            var after = Inventory.Scan(outPkg, Path.GetFileName(output)).Inventory;
            int converted = res.Count(Status.CONVERTED);
            int before = res.Inventory!.Pictures.Count;
            int expectedAfter = before - converted;
            int fallbackKept = 0; // pictures in mc:Fallback stay; they are counted in both
            bool ok = after.Pictures.Count == expectedAfter + fallbackKept;
            int addedMath = after.NativeOMath - res.Inventory.NativeOMath;
            ok &= addedMath == converted;
            rc.Result = ok ? "PASS" : "FAIL";
            rc.Evidence = $"pictures before={before}, converted={converted}, after={after.Pictures.Count} (expected {expectedAfter}); m:oMath before={res.Inventory.NativeOMath}, after={after.NativeOMath}";
        }
        catch (Exception ex) { rc.Result = "FAIL"; rc.Evidence = ex.Message; }
        res.Gates.Add(rc);

        // 4. LibreOffice opens the output (headless conversion to PDF succeeds)
        var lo = new GateResult { Name = "G3.libreoffice_open" };
        var soffice = LibreOfficeCandidates.FirstOrDefault(File.Exists);
        if (soffice == null) { lo.Result = "SKIPPED"; lo.Evidence = "LibreOffice not installed"; }
        else
        {
            try
            {
                var pdfDir = Path.Combine(work, "lo");
                Directory.CreateDirectory(pdfDir);
                var pdf = Path.Combine(pdfDir, Path.GetFileNameWithoutExtension(output) + ".pdf");
                if (File.Exists(pdf)) File.Delete(pdf);
                var profile = "file:///" + Path.Combine(svc.Paths.Root, "out", "lo_profile").Replace('\\', '/');
                var (code, _, err) = Run(soffice, new[] { "-env:UserInstallation=" + profile, "--headless", "--norestore", "--convert-to", "pdf", "--outdir", pdfDir, output }, 300);
                var len = File.Exists(pdf) ? new FileInfo(pdf).Length : 0;
                lo.Result = len > 0 ? "PASS" : "FAIL";
                lo.Evidence = len > 0 ? $"soffice --convert-to pdf produced {len} bytes (exit {code})" : $"no PDF produced (exit {code}) {err}";
            }
            catch (Exception ex) { lo.Result = "FAIL"; lo.Evidence = ex.Message; }
        }
        res.Gates.Add(lo);

        // 5. Open XML validator (tools\OoxmlValidator, Open XML SDK). Pass = no errors that the input did not already have.
        var ov = new GateResult { Name = "G3.openxml_validator" };
        if (!File.Exists(svc.Paths.Validator)) { ov.Result = "SKIPPED"; ov.Evidence = "validator not built at " + svc.Paths.Validator; }
        else
        {
            try
            {
                var a = Validate(svc.Paths.Validator, input);
                var b = Validate(svc.Paths.Validator, output);
                var newErrs = b.Except(a).ToList();
                ov.Result = newErrs.Count == 0 ? "PASS" : "FAIL";
                ov.Evidence = $"input errors={a.Count}, output errors={b.Count}, new errors={newErrs.Count}" +
                              (newErrs.Count > 0 ? ": " + string.Join(" | ", newErrs.Take(5)) : "");
            }
            catch (Exception ex) { ov.Result = "FAIL"; ov.Evidence = ex.Message; }
        }
        res.Gates.Add(ov);
    }

    /// <summary>Runs the validator; returns error signatures (part|path|id) so input and output can be compared.</summary>
    public static List<string> Validate(string validator, string docx)
    {
        var (code, stdout, err) = Run(validator, new[] { docx }, 300);
        if (code is not (0 or 1)) throw new InvalidOperationException($"validator exit {code}: {err}");
        var list = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            using var j = JsonDocument.Parse(line);
            var r = j.RootElement;
            list.Add($"{r.GetProperty("part").GetString()}|{r.GetProperty("id").GetString()}|{r.GetProperty("description").GetString()}");
        }
        return list;
    }

    public static (int Code, string Out, string Err) Run(string exe, string[] args, int timeoutSec)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return (-1, "", "timeout"); }
        p.WaitForExit();
        return (p.ExitCode, o.Result, e.Result);
    }
}
