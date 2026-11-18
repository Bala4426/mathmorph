using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace MathMorph.Tests;

/// <summary>Marks a static, parameterless method as a unit test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool cond, string what = "condition")
    {
        if (!cond) throw new AssertException("expected true: " + what);
    }

    public static void False(bool cond, string what = "condition")
    {
        if (cond) throw new AssertException("expected false: " + what);
    }

    public static void Equal<T>(T expected, T actual, string what = "value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{what}: expected <{expected}> but was <{actual}>");
    }

    public static void NotNull(object? o, string what = "value")
    {
        if (o == null) throw new AssertException(what + ": expected non-null");
    }

    public static void Null(object? o, string what = "value")
    {
        if (o != null) throw new AssertException($"{what}: expected null but was <{o}>");
    }

    public static void Contains(string needle, string hay, string what = "text")
    {
        if (!hay.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{what}: expected to contain <{needle}> but was <{Clip(hay)}>");
    }

    public static void NotContains(string needle, string hay, string what = "text")
    {
        if (hay.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{what}: expected not to contain <{needle}> but was <{Clip(hay)}>");
    }

    public static void BytesEqual(byte[] expected, byte[] actual, string what = "bytes")
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new AssertException($"{what}: byte arrays differ (expected {expected.Length} bytes, got {actual.Length})");
    }

    public static void SeqEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what = "sequence")
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new AssertException($"{what}: expected [{string.Join(", ", e)}] but was [{string.Join(", ", a)}]");
    }

    public static TEx Throws<TEx>(Action a, string what = "action") where TEx : Exception
    {
        try { a(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{what}: expected {typeof(TEx).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{what}: expected {typeof(TEx).Name} but nothing was thrown");
    }

    public static string Clip(string s) => s.Length > 400 ? s[..400] + "..." : s;
}

public static class Program
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .OrderBy(m => m.DeclaringType!.Name, StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        if (filter != null) tests = tests.Where(m => (m.DeclaringType!.Name + "." + m.Name).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        int passed = 0;
        var failures = new List<object>();
        var total = Stopwatch.StartNew();
        foreach (var m in tests)
        {
            var name = m.DeclaringType!.Name + "." + m.Name;
            var sw = Stopwatch.StartNew();
            try
            {
                if (m.GetParameters().Length != 0 || !m.IsStatic) throw new InvalidOperationException("test must be static and parameterless");
                m.Invoke(null, null);
                passed++;
                Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                var msg = inner is AssertException ? inner.Message : inner.GetType().Name + ": " + inner.Message;
                failures.Add(new { name, message = msg });
                Console.WriteLine($"FAIL {name} ({sw.ElapsedMilliseconds} ms): {msg}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed, {total.Elapsed.TotalSeconds:F1} s");

        var result = new
        {
            passed,
            failed = failures.Count,
            timestamp = DateTimeOffset.Now.ToString("o"),
            failures,
        };
        var path = Path.Combine(TestEnv.Root, "tests", "unit_result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("result: " + path);
        return failures.Count == 0 ? 0 : 1;
    }
}

public static class TestEnv
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "src", "MathMorph.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("mathmorph root (src\\MathMorph.csproj) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Fresh, empty directory under out\unit_tmp.</summary>
    public static string TempDir(string name)
    {
        var p = Path.Combine(Root, "out", "unit_tmp", name);
        if (Directory.Exists(p)) Directory.Delete(p, true);
        Directory.CreateDirectory(p);
        return p;
    }
}
