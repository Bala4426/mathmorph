using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MathMorph.Recognition;

/// <summary>
/// A long-lived child process speaking one-JSON-line-per-request over stdin/stdout.
/// Requests are serialized (one in flight). stderr is drained asynchronously into a log file so the
/// pipe can never fill and block the child. Shutdown: empty line + close stdin, then wait, then kill.
/// </summary>
public class JsonLineWorker : IDisposable
{
    private readonly string _exe;
    private readonly string[] _args;
    private readonly Dictionary<string, string> _env;
    private readonly string _stderrLog;
    private readonly TimeSpan _startTimeout, _requestTimeout;
    private readonly object _gate = new();
    private Process? _proc;
    private StreamWriter? _logWriter;
    public JsonObject? ReadyInfo { get; private set; }

    public JsonLineWorker(string exe, string[] args, Dictionary<string, string> env, string stderrLog,
        TimeSpan startTimeout, TimeSpan requestTimeout)
    {
        _exe = exe; _args = args; _env = env; _stderrLog = stderrLog;
        _startTimeout = startTimeout; _requestTimeout = requestTimeout;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_proc is { HasExited: false }) return;
            var psi = new ProcessStartInfo(_exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in _args) psi.ArgumentList.Add(a);
            foreach (var kv in _env) psi.Environment[kv.Key] = kv.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(_stderrLog)!);
            _logWriter ??= new StreamWriter(new FileStream(_stderrLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var log = _logWriter;
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            p.Start();
            p.BeginErrorReadLine();
            _proc = p;
            var ready = ReadLine(_startTimeout) ?? throw new InvalidOperationException("worker did not start (see " + _stderrLog + ")");
            ReadyInfo = JsonNode.Parse(ready) as JsonObject;
            if (ReadyInfo?["ready"]?.GetValue<bool>() != true) throw new InvalidOperationException("worker sent no ready line");
        }
    }

    /// <summary>Sends one request line, returns the parsed response. On timeout/crash the worker is
    /// killed (and restarted on the next call) and an exception is thrown for this item only.</summary>
    public JsonObject Request(string line)
    {
        lock (_gate)
        {
            if (line.Contains('\n') || line.Contains('\r')) throw new ArgumentException("request must be a single line");
            if (_proc is null || _proc.HasExited) Start();
            _proc!.StandardInput.WriteLine(line);
            _proc.StandardInput.Flush();
            var resp = ReadLine(_requestTimeout);
            if (resp == null)
            {
                KillQuietly();
                throw new TimeoutException("worker timed out or exited");
            }
            return JsonNode.Parse(resp) as JsonObject ?? throw new InvalidOperationException("bad worker response");
        }
    }

    private string? ReadLine(TimeSpan timeout)
    {
        var t = _proc!.StandardOutput.ReadLineAsync();
        if (!t.Wait(timeout)) { KillQuietly(); return null; }
        return t.Result;
    }

    private void KillQuietly()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.WaitForExit(5000); } catch { }
        _proc?.Dispose();
        _proc = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_proc != null)
            {
                try
                {
                    if (!_proc.HasExited)
                    {
                        _proc.StandardInput.WriteLine();
                        _proc.StandardInput.Close();
                        if (!_proc.WaitForExit(15_000)) _proc.Kill(true);
                    }
                    _proc.WaitForExit(); // flushes async stderr handlers
                }
                catch { }
                _proc.Dispose();
                _proc = null;
            }
            _logWriter?.Dispose();
            _logWriter = null;
        }
        GC.SuppressFinalize(this);
    }
}
