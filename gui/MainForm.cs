using System.Diagnostics;

namespace MathMorph.Gui;

/// <summary>Main window: header with navigation, two screens (Convert, Review), shared progress bar and log.
/// All conversion work is done by MathMorph.exe; this app only drives it and shows the results.</summary>
public sealed class MainForm : Form
{
    private readonly ConvertPage _convert;
    private readonly ReviewPage _review;
    private readonly StepNav _steps = new("Add documents", "Convert", "Review");
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly ProgressBar _progress = new() { Width = 260, Height = 8, Style = ProgressBarStyle.Continuous, Visible = false };
    private readonly Label _status = new() { AutoSize = true, Font = Ui.Base, ForeColor = Ui.Muted, Text = "Ready." };
    private readonly Button _cancel = Ui.Secondary("Cancel");
    private readonly LinkLabel _toggleLog = new() { Text = "Show log", AutoSize = true, Font = Ui.Base, LinkColor = Ui.Accent, ActiveLinkColor = Ui.AccentHover, AccessibleName = "Show or hide the log", TabStop = true };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f),
        BackColor = Color.FromArgb(250, 251, 252), BorderStyle = BorderStyle.None, WordWrap = false, AccessibleName = "Conversion log",
    };
    private readonly Panel _logPanel = new() { Dock = DockStyle.Bottom, Height = 170, Visible = false, Padding = new Padding(12, 6, 12, 6), BackColor = Color.FromArgb(250, 251, 252) };
    private CancellationTokenSource? _cts;

    public bool Busy { get; private set; }
    public string OutputRoot => _convert.OutputRoot;
    public List<string> ModeArgs() => _convert.ModeArgs();

    public MainForm()
    {
        Text = "MathMorph — Equation pictures to Word equations";
        Font = Ui.Base;
        BackColor = Ui.Bg;
        // all sizes below are logical (96 DPI); the form scales them to the monitor DPI when layout resumes
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 840);
        MinimumSize = new Size(1040, 700);
        KeyPreview = true;

        // header
        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Color.White, Padding = new Padding(20, 0, 20, 0) };
        header.Paint += (_, e) => { using var p = new Pen(Ui.Border); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };
        var logo = new LogoMark { Size = new Size(44, 44), Margin = new Padding(0, 0, 12, 0) };
        var appName = new Label { Text = "MathMorph", Font = Ui.Title, ForeColor = Ui.Text, AutoSize = true, BackColor = Color.White };
        var tagline = new Label { Text = "Equation pictures and MathType → editable Word equations · fully offline", Font = Ui.Small, ForeColor = Ui.Muted, AutoSize = true, BackColor = Color.White };
        var nav = _steps;
        header.Controls.AddRange(new Control[] { logo, appName, tagline, nav });
        void LayoutHeader()
        {
            // manual layout in device pixels: logo + title block on the left, steps on the right
            float s = DeviceDpi / 96f;
            int pad = (int)(20 * s), h = header.Height;
            logo.Size = new Size((int)(42 * s), (int)(42 * s));
            logo.Location = new Point(pad, (h - logo.Height) / 2);
            nav.Size = nav.GetPreferredSize(Size.Empty);
            nav.Location = new Point(header.Width - nav.Width - pad, (h - nav.Height) / 2);
            int tx = logo.Right + (int)(12 * s);
            tagline.Visible = tx + tagline.PreferredWidth + (int)(24 * s) < nav.Left;
            int block = appName.PreferredHeight + (tagline.Visible ? tagline.PreferredHeight : 0);
            appName.Location = new Point(tx - (int)(2 * s), (h - block) / 2);
            tagline.Location = new Point(tx, appName.Top + appName.PreferredHeight - (int)(2 * s));
            appName.Visible = tx + appName.PreferredWidth + (int)(16 * s) < nav.Left;
        }
        header.Resize += (_, _) => LayoutHeader();

        // status bar
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.White, Padding = new Padding(16, 0, 16, 0) };
        statusBar.Paint += (_, e) => { using var p = new Pen(Ui.Border); e.Graphics.DrawLine(p, 0, 0, statusBar.Width, 0); };
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
        _progress.Margin = new Padding(0, 6, 12, 0);
        left.Controls.AddRange(new Control[] { _progress, _status });
        var rightBar = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, BackColor = Color.White, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        _cancel.Visible = false;
        _toggleLog.Margin = new Padding(12, 8, 0, 0);
        rightBar.Controls.AddRange(new Control[] { _cancel, _toggleLog });
        statusBar.Controls.Add(left);
        statusBar.Controls.Add(rightBar);
        _status.AccessibleRole = AccessibleRole.StatusBar;
        _status.AccessibleName = "Status";
        _progress.AccessibleName = "Conversion progress";

        _logPanel.Controls.Add(_log);

        _convert = new ConvertPage(this);
        _review = new ReviewPage(this);
        _convert.Visible = true;
        _review.Visible = false;
        _host.Controls.Add(_review);
        _host.Controls.Add(_convert);
        Controls.Add(_host);
        Controls.Add(_logPanel);
        Controls.Add(statusBar);
        Controls.Add(header);

        _steps.StepClicked += i => { if (i == 2) ShowReview(OutputRoot, null); else ShowPage(_convert); };
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.D1) { ShowPage(_convert); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.D2) { ShowReview(OutputRoot, null); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && Busy) { _cts?.Cancel(); SetStatus("Cancelling…"); e.Handled = true; }
        };
        _cancel.Click += (_, _) => { _cts?.Cancel(); SetStatus("Cancelling…"); };
        _toggleLog.LinkClicked += (_, _) => { _logPanel.Visible = !_logPanel.Visible; _toggleLog.Text = _logPanel.Visible ? "Hide log" : "Show log"; };
        FormClosing += (_, e) =>
        {
            if (!Busy) return;
            if (MessageBox.Show(this, "A conversion is running. Stop it and close?", "MathMorph", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No) e.Cancel = true;
            else _cts?.Cancel();
        };
        ShowPage(_convert);
        ResumeLayout(true);
        Load += (_, _) =>
        {
            // keep the scaled window inside the screen it opens on
            var wa = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, wa.Width - 40), Math.Min(Height, wa.Height - 40));
            MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width - 40), Math.Min(MinimumSize.Height, wa.Height - 40));
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            LayoutHeader();
        };
        DpiChanged += (_, _) => BeginInvoke(LayoutHeader);
        if (CliRunner.FindCli() == null) SetStatus("MathMorph.exe was not found next to this app or in ..\\bin — conversion is unavailable.");
    }

    private void ShowPage(Control page)
    {
        _host.SuspendLayout();
        _convert.Visible = page == _convert;
        _review.Visible = page == _review;
        _host.ResumeLayout();
        UpdateStep();
    }

    /// <summary>Highlights the current step: Review when that screen is shown, else Convert once documents are added.</summary>
    public void UpdateStep()
    {
        if (_review == null || _convert == null) return;
        _steps.Current = _review.Visible ? 2 : _convert.DocumentCount > 0 ? 1 : 0;
    }

    public void ShowReview(string outRoot, string? stem)
    {
        _review.Reload(outRoot, stem);
        ShowPage(_review);
    }

    /// <summary>Runs a unit of work with the busy state, Cancel button and log wired up.</summary>
    public async Task RunBatchAsync(Func<CancellationToken, Action<string>, Task> work)
    {
        if (Busy) return;
        Busy = true;
        _cts = new CancellationTokenSource();
        _cancel.Visible = true;
        _progress.Visible = true;
        _convert.SetBusy(true);
        _review.SetBusy(true);
        _log.Clear();
        try { await work(_cts.Token, Log); }
        catch (Exception ex) { Log("! " + ex.Message); SetStatus("Error: " + ex.Message); }
        finally
        {
            Busy = false;
            _cancel.Visible = false;
            _progress.Visible = false;
            _convert.SetBusy(false);
            _review.SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    public void SetProgress(int done, int total, string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetProgress(done, total, text)); return; }
        _progress.Maximum = Math.Max(1, total);
        _progress.Value = Math.Min(done, _progress.Maximum);
        if (text.Length > 0) _status.Text = text;
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _status.Text = text;
    }

    private void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

/// <summary>Rounded-square app mark with a sigma.</summary>
internal sealed class LogoMark : Control
{
    public LogoMark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "MathMorph logo";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.FromArgb(99, 102, 241), Color.FromArgb(67, 56, 202), 45f))
        using (var p = Ui.Rounded(r, 10 * DeviceDpi / 96f))
            g.FillPath(b, p);
        using var f = new Font("Segoe UI", 18f, FontStyle.Bold);
        TextRenderer.DrawText(g, "Σ", f, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

internal static class GuiProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        // never vanish silently: log unexpected errors to <root>\out\gui_error.log and tell the user
        static void Log(Exception? ex)
        {
            try
            {
                var dir = Path.Combine(CliRunner.ProjectRoot(), "out");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "gui_error.log"), $"{DateTime.Now:s} {ex}\r\n\r\n");
            }
            catch { }
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log(e.Exception);
            MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails are in out\\gui_error.log.", "MathMorph", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        var form = new MainForm();
        // "--review" opens straight on the Review screen (handy for demos)
        if (args.Any(a => a.Equals("--review", StringComparison.OrdinalIgnoreCase))) form.Shown += (_, _) => form.ShowReview(form.OutputRoot, null);
        Application.Run(form);
    }
}
