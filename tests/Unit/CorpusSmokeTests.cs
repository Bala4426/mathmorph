using System.Text.Json;
using MathMorph.Contracts;
using MathMorph.Engine;

namespace MathMorph.Tests;

/// <summary>Read-only smoke tests against tests\corpus (inventory only; no recognition).</summary>
public static class CorpusSmokeTests
{
    private static string Corpus => Path.Combine(TestEnv.Root, "tests", "corpus");

    [Test]
    public static void Corpus_InventoryMatchesExpected()
    {
        var exp = Path.Combine(Corpus, "expected.json");
        if (!File.Exists(exp)) { Console.WriteLine("  (corpus not found: skipped)"); return; }
        using var j = JsonDocument.Parse(File.ReadAllText(exp));
        var problems = new List<string>();
        int docs = 0;
        foreach (var d in j.RootElement.GetProperty("docs").EnumerateArray())
        {
            var file = d.GetProperty("file").GetString()!;
            var path = Path.Combine(Corpus, file);
            if (!File.Exists(path)) { problems.Add(file + ": missing"); continue; }
            docs++;
            var inv = Inventory.Scan(SafePackage.Open(path, new SafetyLimits()), file).Inventory;
            if (d.TryGetProperty("native_omath", out var no) && no.GetInt32() != inv.NativeOMath)
                problems.Add($"{file}: native_omath expected {no.GetInt32()} got {inv.NativeOMath}");
            if (d.TryGetProperty("native_omathpara", out var nop) && nop.GetInt32() != inv.NativeOMathPara)
                problems.Add($"{file}: native_omathpara expected {nop.GetInt32()} got {inv.NativeOMathPara}");

            var expected = new List<string>();
            foreach (var p in d.GetProperty("pictures").EnumerateArray())
            {
                var cat = p.GetProperty("expected_category").GetString()!;
                var reason = p.GetProperty("expected_reason").GetString() ?? "";
                // pixel-statistics exclusions happen later in the pipeline, not in the inventory
                if (reason.Contains("colorful", StringComparison.OrdinalIgnoreCase)) cat = "CANDIDATE";
                expected.Add($"{p.GetProperty("part").GetString()}|{p.GetProperty("para_index").GetInt32()}|{p.GetProperty("rid").GetString()}|{p.GetProperty("container").GetString()}|{cat}");
            }
            var actual = inv.Pictures.Select(p => $"{p.Part}|{p.ParaIndex}|{p.RelId}|{p.Container}|{Reports.CategoryName(p.Category)}").ToList();
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            if (expected.Count != actual.Count) problems.Add($"{file}: expected {expected.Count} pictures, got {actual.Count}");
            foreach (var m in missing) problems.Add($"{file}: missing {m}");
            foreach (var x in extra) problems.Add($"{file}: unexpected {x}");
        }
        Assert.True(docs >= 12, "corpus documents found: " + docs);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
