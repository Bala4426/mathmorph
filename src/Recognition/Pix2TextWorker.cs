using System.Text.Json;
using System.Text.Json.Nodes;
using MathMorph.Contracts;

namespace MathMorph.Recognition;

/// <summary>Runs workers\pix2text_worker.py once (model loaded once) and recognizes images one at a time.</summary>
public sealed class Pix2TextWorker : IDisposable
{
    private readonly JsonLineWorker _w;
    public string Pix2TextVersion => _w.ReadyInfo?["pix2text"]?.ToString() ?? "unknown";
    public double LoadSeconds => _w.ReadyInfo?["load_seconds"]?.GetValue<double>() ?? -1;

    public Pix2TextWorker(EnginePaths paths, string logFile)
    {
        _w = new JsonLineWorker(paths.Python, new[] { "-I", Path.Combine(paths.Root, "workers", "pix2text_worker.py") },
            paths.WorkerEnv(), logFile, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3));
    }

    public void Start() => _w.Start();

    public RecognitionResult Recognize(string imagePath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var o = _w.Request(Path.GetFullPath(imagePath));
            return new RecognitionResult
            {
                Path = imagePath,
                Ok = o["ok"]?.GetValue<bool>() ?? false,
                Latex = o["latex"]?.ToString() ?? "",
                Error = o["error"]?.ToString() ?? "",
                Seconds = o["seconds"]?.GetValue<double>() ?? sw.Elapsed.TotalSeconds,
                ColorfulFraction = o["colorful_fraction"]?.GetValue<double>() ?? -1,
                MidtoneFraction = o["midtone_fraction"]?.GetValue<double>() ?? -1,
                Width = o["width"]?.GetValue<int>() ?? 0,
                Height = o["height"]?.GetValue<int>() ?? 0,
            };
        }
        catch (Exception ex)
        {
            return new RecognitionResult { Path = imagePath, Ok = false, Error = ex.GetType().Name + ": " + ex.Message, Seconds = sw.Elapsed.TotalSeconds };
        }
    }

    public void Dispose() => _w.Dispose();
}

/// <summary>Runs workers\verify_worker.py: renders LaTeX locally and scores it against the original (SSIM).</summary>
public sealed class VerifyWorker : IDisposable
{
    private readonly JsonLineWorker _w;
    public string Browser => _w.ReadyInfo?["browser"]?.ToString() ?? "";

    public VerifyWorker(EnginePaths paths, string logFile)
    {
        _w = new JsonLineWorker(paths.Python, new[] { "-I", Path.Combine(paths.Root, "workers", "verify_worker.py") },
            paths.WorkerEnv(), logFile, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
    }

    public void Start() => _w.Start();

    public VerifyResult Verify(string original, string latex, string renderOut)
    {
        try
        {
            var req = new JsonObject { ["orig"] = Path.GetFullPath(original), ["latex"] = latex, ["out"] = Path.GetFullPath(renderOut) };
            var o = _w.Request(req.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default }));
            return new VerifyResult
            {
                Ok = o["ok"]?.GetValue<bool>() ?? false,
                Ssim = o["ssim"]?.GetValue<double>() ?? -1,
                RenderPath = o["render_path"]?.ToString() ?? "",
                Error = o["error"]?.ToString() ?? "",
            };
        }
        catch (Exception ex)
        {
            return new VerifyResult { Ok = false, Error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    public void Dispose() => _w.Dispose();
}

/// <summary>Locations of the Python runtime, workers, tools and models.</summary>
public sealed class EnginePaths
{
    public required string Root { get; init; }
    public string Python => Path.Combine(Root, ".venv", "Scripts", "python.exe");
    public string Pandoc => Path.Combine(Root, "tools", "pandoc", "pandoc.exe");
    public string Validator => Path.Combine(Root, "tools", "OoxmlValidator", "OoxmlValidator.exe");
    public string Models => Path.Combine(Root, "tools", "models");

    public Dictionary<string, string> WorkerEnv() => new()
    {
        ["HF_HUB_OFFLINE"] = "1",
        ["TRANSFORMERS_OFFLINE"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["MATHMORPH_MODEL_ROOT"] = Models,
        ["MATHMORPH_PANDOC"] = Pandoc,
        ["MATHMORPH_EDGE_PROFILE"] = Path.Combine(Root, "out", "edge_profile"),
        ["YOLO_CONFIG_DIR"] = Path.Combine(Models, "ultralytics"),
        ["HF_HOME"] = Path.Combine(Models, "hf"),
        ["WANDB_MODE"] = "disabled",
        ["YOLO_OFFLINE"] = "1",
    };

    /// <summary>Finds the mathmorph root: MATHMORPH_HOME, else the first ancestor of the exe holding workers\pix2text_worker.py.</summary>
    public static EnginePaths Discover()
    {
        var env = (Environment.GetEnvironmentVariable("MATHMORPH_HOME") ?? Environment.GetEnvironmentVariable("EQCONV_HOME"));
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "workers", "pix2text_worker.py")))
            return new EnginePaths { Root = Path.GetFullPath(env) };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "workers", "pix2text_worker.py"))) return new EnginePaths { Root = dir.FullName };
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("cannot find mathmorph root (set MATHMORPH_HOME)");
    }
}
