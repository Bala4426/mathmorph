using System.Diagnostics;

namespace MathMorph.Gui;

/// <summary>Runs MathMorph.exe (all conversion work happens there) and streams its output lines.</summary>
internal static class CliRunner
{
    public static string? FindCli()
    {
        var here = AppContext.BaseDirectory;
        foreach (var c in new[]
                 {
                     Path.Combine(here, "MathMorph.exe"), Path.Combine(here, "..", "bin", "MathMorph.exe"),
                     Path.Combine(here, "..", "..", "bin", "MathMorph.exe"),
                 })
            if (File.Exists(c)) return Path.GetFullPath(c);
        var home = (Environment.GetEnvironmentVariable("MATHMORPH_HOME") ?? Environment.GetEnvironmentVariable("EQCONV_HOME"));
        if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", "MathMorph.exe"))) return Path.GetFullPath(Path.Combine(home, "bin", "MathMorph.exe"));
        return null;
    }

    /// <summary>The mathmorph project root: MATHMORPH_HOME, else the folder above bin\MathMorph.exe, else the app folder.</summary>
    public static string ProjectRoot()
    {
        var home = (Environment.GetEnvironmentVariable("MATHMORPH_HOME") ?? Environment.GetEnvironmentVariable("EQCONV_HOME"));
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) return Path.GetFullPath(home);
        var cli = FindCli();
        return cli != null ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cli)!, "..")) : AppContext.BaseDirectory;
    }

    /// <summary>Runs the CLI; each stdout/stderr line goes to <paramref name="onLine"/> (stderr prefixed "! ").
    /// Cancelling kills the process tree. Returns the exit code (-1 when cancelled).</summary>
    public static async Task<int> RunAsync(IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        var cli = FindCli() ?? throw new FileNotFoundException("MathMorph.exe not found next to the app or in ..\\bin.");
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine("! " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return -1;
        }
        return p.ExitCode;
    }

    /// <summary>Counts MathType objects in one document or folder ("MathMorph.exe mathtype-count"; reads only).</summary>
    public static async Task<int> CountMathTypeAsync(string input)
    {
        int objects = 0;
        try
        {
            await RunAsync(new[] { "mathtype-count", input }, line =>
            {
                var f = line.Trim().Split(' ');
                if (f.Length >= 3 && f[0] == "MATHTYPE" && int.TryParse(f[1], out var n)) objects = n;
            }, CancellationToken.None);
        }
        catch { }
        return objects;
    }
}
