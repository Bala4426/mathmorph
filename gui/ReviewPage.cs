using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MathMorph.Gui;

/// <summary>Screen 2: proofread a converted document. Shows each equation picture next to the rendering of the
/// recognised LaTeX, lets the user correct LaTeX, and re-converts with the corrections (--latex-overrides).</summary>
internal sealed class ReviewPage : UserControl
{
    private sealed class Row
    {
        public int Para; public string Part = ""; public string Sha = ""; public string Status = ""; public string Category = "";
        public double Score = -1; public string Latex = ""; public string Note = ""; public int Uses = 1;
        public string Shown => Correction ?? Latex;
        public string? Correction;
        // a saved fix (also "accept as is" on an item that needs review) shows as pending until it is applied
        public string DisplayStatus => Correction != null && !(Correction == Latex && Status == "CONVERTED") ? "CORRECTED" : Status;
    }

    private readonly MainForm _main;
    private readonly ComboBox _doc = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Font = Ui.Base };
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Font = Ui.Base };
    private readonly TextBox _search = new() { Width = 200, Font = Ui.Base, PlaceholderText = "Search LaTeX or paragraph" };
    private readonly DataGridView _grid = new();
    private readonly Label _detailTitle = Ui.Label("Select an equation", Ui.CardTitle);
    private readonly Label _detailSub = Ui.Label("", Ui.Small, Ui.Muted);
    private readonly PictureBox _orig = Pic();
    private readonly PictureBox _render = Pic();
    private readonly Label _renderCaption = Ui.Label("Converted equation (rendered from the LaTeX below)", Ui.Small, Ui.Muted);
    private readonly TextBox _latex = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Ui.Mono, AcceptsReturn = false };
    private readonly Button _save = Ui.Primary("Save && check");
    private readonly Button _check = Ui.Secondary("Check LaTeX");
    private readonly Label _checkResult = Ui.Label("", Ui.Small, Ui.Muted);
    private int _previewVersion;
    private readonly Button _undo = Ui.Secondary("Undo correction");
    private readonly Button _next = Ui.Secondary("Next to review  ↓");
    private readonly Label _pending = Ui.Label("", Ui.Base, Ui.Muted);
    private readonly Button _apply = Ui.Primary("Apply corrections && re-convert");
    private readonly Button _openDoc = Ui.Secondary("Open document");
    private readonly Button _openFolder = Ui.Secondary("Open folder");
    private readonly Label _empty = new()
    {
        Text = "No results yet\r\n\r\nConvert a document first (step 2), then come back here to proofread each equation.", Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.Muted, Font = Ui.EmptyTitle, BackColor = Ui.Bg,
    };

    private List<Row> _rows = new();
    private string _outRoot = "";
    private string _stem = "";
    private Dictionary<string, string> _corrections = new(StringComparer.OrdinalIgnoreCase);

    public ReviewPage(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Ui.Bg;
        Padding = new Padding(8);

        // --- toolbar
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 10, 8, 0), BackColor = Ui.Bg, WrapContents = false };
        _doc.AccessibleName = "Document";
        _filter.AccessibleName = "Show";
        _search.AccessibleName = "Search LaTeX or paragraph number";
        _latex.AccessibleName = "LaTeX of the selected equation";
        _orig.AccessibleName = "Original equation picture";
        _render.AccessibleName = "Converted equation preview";
        var refresh = Ui.Secondary("Refresh");
        tools.Controls.AddRange(new Control[] { Ui.Label("Document", Ui.Bold), _doc, Ui.Label("Show", Ui.Bold), _filter, _search, refresh });
        foreach (Control c in tools.Controls) c.Margin = c is Label ? new Padding(6, 9, 4, 0) : c is Button ? new Padding(2, 0, 8, 0) : new Padding(2, 5, 8, 0);
        _filter.Items.AddRange(new object[] { "All equations", "Needs review", "Converted", "Fixed – apply pending", "Everything incl. figures" });
        _filter.SelectedIndex = 0;

        // --- grid card (left)
        var (listCard, listBody) = Ui.MakeCard("Equations", "Select a row to compare it with the original. F8 jumps to the next one that needs review.");
        StyleGrid();
        listBody.Controls.Add(_grid);

        // --- detail card (right)
        var (detCard, detBody) = Ui.MakeCard("Proofread");
        // fixed row heights inside a scrollable host: never overlaps on small screens, grows pictures on large ones
        var det = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 10, BackColor = Ui.Card, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        det.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        det.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        det.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        det.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var detScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Card };
        detScroll.Resize += (_, _) =>
        {
            // give the two pictures whatever height is left, but at least 110 px each
            float s = detScroll.DeviceDpi / 96f;
            _detailSub.MaximumSize = new Size(Math.Max(200, detScroll.ClientSize.Width - (int)(24 * s)), 0);
            int spare = detScroll.ClientSize.Height - (int)(380 * s);
            int h = Math.Max((int)(110 * s), Math.Min((int)(240 * s), spare / 2));
            det.RowStyles[3].Height = det.RowStyles[5].Height = h;
        };
        det.Controls.Add(_detailTitle, 0, 0);
        det.Controls.Add(_detailSub, 0, 1);
        det.Controls.Add(Ui.Label("Original picture", Ui.Small, Ui.Muted), 0, 2);
        det.Controls.Add(Framed(_orig), 0, 3);
        det.Controls.Add(_renderCaption, 0, 4);
        det.Controls.Add(Framed(_render), 0, 5);
        det.Controls.Add(Ui.Label("LaTeX — edit to correct, then press Enter or Ctrl+S to save", Ui.Small, Ui.Muted), 0, 6);
        det.Controls.Add(_latex, 0, 7);
        var detButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), BackColor = Ui.Card };
        detButtons.Controls.AddRange(new Control[] { _save, _check, _undo, _next });
        _next.Text = "Next to review  (F8)";
        _next.AccessibleName = "Next equation that needs review";
        det.Controls.Add(detButtons, 0, 8);
        det.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _checkResult.Margin = new Padding(0, 4, 0, 0);
        det.Controls.Add(_checkResult, 0, 9);
        _check.AccessibleName = "Check that this LaTeX converts to a Word equation and preview it";
        detScroll.Controls.Add(det);
        detBody.Controls.Add(detScroll);

        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Ui.Bg, SplitterWidth = 6, FixedPanel = FixedPanel.None };
        split.Panel1.Controls.Add(listCard);
        split.Panel2.Controls.Add(detCard);
        split.Panel1.BackColor = split.Panel2.BackColor = Ui.Bg;

        // --- bottom action bar
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 12, 8, 0), BackColor = Ui.Bg };
        _pending.Margin = new Padding(0, 9, 16, 0);
        _apply.Margin = new Padding(0);
        bar.Controls.AddRange(new Control[] { _apply, _openDoc, _openFolder, _pending });

        Controls.Add(split);
        Controls.Add(bar);
        Controls.Add(tools);
        Controls.Add(_empty);
        _empty.BringToFront();
        HandleCreated += (_, _) => split.SplitterDistance = (int)(split.Width * 0.55);
        split.SizeChanged += (_, _) => { if (split.Width > 400) split.SplitterDistance = Math.Max(300, (int)(split.Width * 0.55)); };

        // --- behaviour
        _doc.SelectedIndexChanged += (_, _) => { if (_doc.SelectedItem is string s) LoadDocument(s); };
        _filter.SelectedIndexChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        refresh.Click += (_, _) => Reload(_outRoot.Length > 0 ? _outRoot : _main.OutputRoot, _stem);
        _grid.SelectionChanged += (_, _) => ShowSelected();
        _save.Click += (_, _) => SaveCorrection();
        _undo.Click += (_, _) => UndoCorrection();
        _check.Click += async (_, _) => { var r = Selected; if (r != null) await PreviewAsync(r, _latex.Text.Replace("\r", " ").Replace("\n", " ").Trim()); };
        _next.Click += (_, _) => SelectNext();
        _latex.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.S) { SaveCorrection(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Enter) { SaveCorrection(); e.SuppressKeyPress = true; }
        };
        _apply.Click += async (_, _) => await ApplyAsync();
        _openDoc.Click += (_, _) => { var f = OutputDocx(); if (f != null) MainForm.OpenFile(f); };
        _openFolder.Click += (_, _) => MainForm.OpenFolder(Path.Combine(_outRoot, _stem));
        UpdateButtons();
    }

    /// <summary>F8 = next equation needing review, Ctrl+F = search, Alt+Up/Down = previous/next row (works while editing LaTeX).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F8: SelectNext(); return true;
            case Keys.Control | Keys.F: _search.Focus(); _search.SelectAll(); return true;
            case Keys.Alt | Keys.Down: MoveRow(+1); return true;
            case Keys.Alt | Keys.Up: MoveRow(-1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveRow(int delta)
    {
        if (_grid.Rows.Count == 0) return;
        int cur = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1;
        int i = Math.Clamp(cur + delta, 0, _grid.Rows.Count - 1);
        _grid.ClearSelection();
        _grid.Rows[i].Selected = true;
        _grid.CurrentCell = _grid.Rows[i].Cells[0];
    }

    private static PictureBox Pic() => new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };

    private static Control Framed(Control c)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Ui.Border, Margin = new Padding(0, 2, 0, 8) };
        var inner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
        inner.Controls.Add(c);
        p.Controls.Add(inner);
        return p;
    }

    private void StyleGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill; g.BackgroundColor = Ui.Card; g.BorderStyle = BorderStyle.None; g.RowHeadersVisible = false;
        g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false; g.ReadOnly = true;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; g.GridColor = Color.FromArgb(236, 239, 243);
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Ui.Card, ForeColor = Ui.Muted, Font = Ui.Bold, SelectionBackColor = Ui.Card, Padding = new Padding(4, 0, 0, 0) };
        g.ColumnHeadersHeight = 34; g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.DefaultCellStyle = new DataGridViewCellStyle { Font = Ui.Base, ForeColor = Ui.Text, BackColor = Ui.Card, SelectionBackColor = Ui.AccentSoft, SelectionForeColor = Ui.Text, Padding = new Padding(4, 0, 4, 0) };
        g.RowTemplate.Height = 34;
        g.ColumnHeadersDefaultCellStyle.Font = Ui.SmallBold;
        g.ColumnHeadersHeight = 36;
        g.AccessibleName = "Equations in this document";
        g.HandleCreated += (_, _) =>
        {
            float s = g.DeviceDpi / 96f;
            if (s <= 1.01f) return;
            g.RowTemplate.Height = (int)(34 * s);
            g.ColumnHeadersHeight = (int)(36 * s);
            g.Columns["para"]!.Width = (int)(60 * s);
            g.Columns["status"]!.Width = (int)(160 * s);
            g.Columns["score"]!.Width = (int)(78 * s);
        };
        g.StandardTab = true;
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _latex.Enabled) { _latex.Focus(); _latex.SelectAll(); e.SuppressKeyPress = true; }
        };
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "para", HeaderText = "Para", Width = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Status", Width = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "score", HeaderText = "Score", Width = 78, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "latex", HeaderText = "LaTeX", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9.5f) } });
        g.CellPainting += (_, e) =>
        {
            // status as a coloured pill
            if (e.RowIndex < 0 || e.ColumnIndex != 1 || e.Graphics == null) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var raw = (string?)g.Rows[e.RowIndex].Tag ?? "";
            var (fore, back) = Ui.StatusColors(raw);
            var text = Ui.Friendly(raw);
            Ui.DrawPill(e.Graphics, e.CellBounds, text, fore, back, g.DeviceDpi / 96f);
            e.Handled = true;
        };
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Lists documents with results under <paramref name="outRoot"/> and selects <paramref name="stem"/>.</summary>
    public void Reload(string outRoot, string? stem)
    {
        _outRoot = outRoot;
        _doc.Items.Clear();
        if (Directory.Exists(outRoot))
            foreach (var d in Directory.GetDirectories(outRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (File.Exists(Path.Combine(d, "report.csv"))) _doc.Items.Add(Path.GetFileName(d));
        _empty.Visible = _doc.Items.Count == 0;
        if (_doc.Items.Count == 0) return;
        int idx = stem != null ? _doc.Items.IndexOf(stem) : -1;
        _doc.SelectedIndex = idx >= 0 ? idx : 0;
        if (_doc.SelectedItem is string s && s == _stem) LoadDocument(s);
    }

    private string CorrectionsFile => Path.Combine(_outRoot, "_corrections", _stem + ".overrides.csv");

    private void LoadDocument(string stem)
    {
        _stem = stem;
        var report = Path.Combine(_outRoot, stem, "report.csv");
        var lines = File.ReadAllLines(report, Encoding.UTF8).Skip(1).Where(l => l.Length > 0).ToList();
        _rows = new List<Row>();
        foreach (var l in lines)
        {
            var c = ParseCsv(l);
            if (c.Count < 10) continue;
            _rows.Add(new Row
            {
                Part = c[1], Para = int.TryParse(c[2], out var p) ? p : -1, Sha = c[4], Category = c[5], Status = c[6],
                Score = double.TryParse(c[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : -1,
                Latex = Unguard(c[8]), Note = c[9],
            });
        }
        foreach (var g in _rows.Where(r => r.Sha.Length > 0).GroupBy(r => r.Sha)) foreach (var r in g) r.Uses = g.Count();
        _corrections = LoadCorrections();
        foreach (var r in _rows) r.Correction = _corrections.TryGetValue(r.Sha, out var v) ? v : null;
        Fill();
    }

    private Dictionary<string, string> LoadCorrections()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(CorrectionsFile)) return d;
        foreach (var l in File.ReadAllLines(CorrectionsFile, Encoding.UTF8).Skip(1))
        {
            var c = ParseCsv(l);
            if (c.Count >= 2 && c[0].Length == 64) d[c[0]] = c[1];
        }
        return d;
    }

    private void SaveCorrections()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CorrectionsFile)!);
        var sb = new StringBuilder("sha256,latex\r\n");
        foreach (var kv in _corrections) sb.Append(kv.Key).Append(",\"").Append(kv.Value.Replace("\"", "\"\"")).Append("\"\r\n");
        File.WriteAllText(CorrectionsFile, sb.ToString(), new UTF8Encoding(false));
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        IEnumerable<Row> rows = _rows;
        rows = _filter.SelectedIndex switch
        {
            0 => rows.Where(IsEquation),
            1 => rows.Where(r => r.DisplayStatus == "NEEDS_REVIEW"),
            2 => rows.Where(r => r.DisplayStatus == "CONVERTED"),
            3 => rows.Where(r => r.DisplayStatus == "CORRECTED"),
            _ => rows,
        };
        if (q.Length > 0) rows = rows.Where(r => r.Shown.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Para.ToString(CultureInfo.InvariantCulture) == q);
        var keep = Selected?.Sha + "|" + Selected?.Para;
        _grid.Rows.Clear();
        foreach (var r in rows.OrderBy(r => r.Part != "word/document.xml").ThenBy(r => r.Para))
        {
            int i = _grid.Rows.Add(r.Para, Ui.Friendly(r.DisplayStatus), r.Score >= 0 ? r.Score.ToString("0.00", CultureInfo.InvariantCulture) : "—", r.Shown);
            _grid.Rows[i].Tag = r.DisplayStatus;
            _grid.Rows[i].Cells[0].Tag = r;
            if (r.Sha + "|" + r.Para == keep) _grid.Rows[i].Selected = true;
        }
        if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
        UpdateButtons();
        ShowSelected();
    }

    private static bool IsEquation(Row r) => r.Status is "CONVERTED" or "NEEDS_REVIEW" or "OLE_PREVIEW" || r.Category is "CANDIDATE" or "OLE_PREVIEW" or "MATHTYPE";

    private Row? Selected => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Cells[0].Tag as Row : null;

    private void ShowSelected()
    {
        var r = Selected;
        _orig.Image?.Dispose(); _orig.Image = null;
        _render.Image?.Dispose(); _render.Image = null;
        if (r == null)
        {
            _detailTitle.Text = "Select an equation"; _detailSub.Text = ""; _latex.Text = ""; _latex.Enabled = false;
            UpdateButtons();
            return;
        }
        _latex.Enabled = IsEquation(r);
        _detailTitle.Text = $"Paragraph {r.Para}" + (r.Part != "word/document.xml" ? $" ({r.Part})" : "");
        var uses = r.Uses > 1 ? $" · same picture used {r.Uses}× (a correction applies to all)" : "";
        _detailSub.Text = Ui.Friendly(r.DisplayStatus) + (r.Score >= 0 ? $" · score {r.Score:0.00}" : "") + uses + (r.Note.Length > 0 ? " · " + r.Note : "");
        _detailSub.MaximumSize = new Size(Math.Max(200, _latex.Width), 0);
        var work = Path.Combine(_outRoot, "_work", _stem);
        _orig.Image = Ui.LoadImage(OriginalImage(r));
        var renderFile = Path.Combine(work, "render", r.Sha + "_" + StableHash(r.Shown) + ".png");
        _render.Image = Ui.LoadImage(renderFile);
        _renderCaption.Text = _render.Image != null ? "Converted equation (rendered from the LaTeX below)"
            : r.Note.StartsWith("verification render failed", StringComparison.Ordinal) ? "No preview: this LaTeX could not be rendered. Correct it below and press Check LaTeX"
            : r.Note.StartsWith("recognition failed", StringComparison.Ordinal) ? "No preview: the picture could not be read. Type the LaTeX below and press Check LaTeX"
            : "No preview yet. Press Check LaTeX to render it";
        _renderCaption.ForeColor = _render.Image != null ? Ui.Muted : Ui.Warn;
        _checkResult.Text = "";
        _latex.Text = r.Shown;
        // no render yet (e.g. MathType objects, which are converted without one): make it now, once per LaTeX
        if (_render.Image == null && IsEquation(r) && r.Shown.Length > 0 && !_previewRunning && _autoPreviewed.Add(r.Sha + "\n" + r.Shown))
            _ = PreviewAsync(r, r.Shown);
        UpdateButtons();
    }

    // ------------------------------------------------------------------ editing

    private void SaveCorrection()
    {
        var r = Selected;
        if (r == null || !IsEquation(r)) return;
        var text = _latex.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) { MessageBox.Show(this, "LaTeX cannot be empty.", "MathMorph"); return; }
        if (text == r.Latex && r.Status == "CONVERTED") { _corrections.Remove(r.Sha); }
        else _corrections[r.Sha] = text;
        SaveCorrections();
        foreach (var x in _rows.Where(x => x.Sha == r.Sha)) x.Correction = _corrections.TryGetValue(r.Sha, out var v) ? v : null;
        Fill();
        _main.SetStatus(_corrections.Count > 0
            ? $"Saved. {_corrections.Count} fix(es) are waiting: press \"Apply corrections & re-convert\" to write them into the Word document."
            : "Saved.");
        _ = PreviewAsync(r, text);   // check + render in the background; the row stays selected so the result is visible
    }

    /// <summary>Runs "MathMorph.exe preview": checks the LaTeX with the same converter a conversion uses (Pandoc → Word
    /// equation) and renders it into the render folder, where ShowSelected finds it. MathJax accepts a different
    /// LaTeX dialect, so this — not a web demo — is the check that matters for the Word document.</summary>
    private async Task PreviewAsync(Row r, string latex)
    {
        if (latex.Length == 0) return;
        int version = ++_previewVersion;
        var work = Path.Combine(_outRoot, "_work", _stem);
        var img = OriginalImage(r) ?? "";
        var renderFile = Path.Combine(work, "render", r.Sha + "_" + StableHash(latex) + ".png");
        var tmp = Path.Combine(Path.GetTempPath(), "mathmorph_preview_" + Environment.ProcessId + ".txt");
        File.WriteAllText(tmp, latex, new UTF8Encoding(false));
        _checkResult.ForeColor = Ui.Muted;
        _checkResult.Text = "Checking LaTeX and rendering a preview…";
        _check.Enabled = false;
        _previewRunning = true;
        string json = "";
        try { await CliRunner.RunAsync(new[] { "preview", img.Length > 0 ? img : "-", tmp, renderFile }, l => { if (l.StartsWith("{")) json = l; }, CancellationToken.None); }
        catch (Exception ex) { json = ""; _checkResult.Text = "Check failed: " + ex.Message; }
        finally { _check.Enabled = true; _previewRunning = false; try { File.Delete(tmp); } catch { } }
        if (version != _previewVersion || json.Length == 0) return;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var o = doc.RootElement;
        bool convertible = o.GetProperty("convertible").GetBoolean();
        bool rendered = o.GetProperty("render_ok").GetBoolean();
        double ssim = o.GetProperty("ssim").GetDouble();
        if (Selected == r && File.Exists(renderFile))
        {
            _render.Image?.Dispose();
            _render.Image = Ui.LoadImage(renderFile);
            _renderCaption.Text = "Preview of the LaTeX below";
            _renderCaption.ForeColor = Ui.Muted;
        }
        if (Selected != r) return;
        _checkResult.ForeColor = convertible ? Ui.Ok : Ui.Bad;
        _checkResult.Text = convertible
            ? "✓ Converts to a Word equation." + (rendered && ssim >= 0 ? $" Similarity to the picture: {ssim:0.00}." : "")
            : "✗ Word cannot convert this LaTeX: " + o.GetProperty("convert_error").GetString();
    }

    private void UndoCorrection()
    {
        var r = Selected;
        if (r == null || !_corrections.Remove(r.Sha)) return;
        SaveCorrections();
        foreach (var x in _rows.Where(x => x.Sha == r.Sha)) x.Correction = null;
        Fill();
    }

    private readonly HashSet<string> _autoPreviewed = new();
    private bool _previewRunning;

    /// <summary>The original picture of a row: a PNG/JPEG from the work folder, or a PNG made from a WMF/EMF preview.</summary>
    private string? OriginalImage(Row r)
    {
        var dir = Path.Combine(_outRoot, "_work", _stem, "img");
        if (r.Sha.Length == 0 || !Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, r.Sha + ".*");
        var direct = files.FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg");
        return direct ?? Ui.EnsurePng(files.FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".wmf" or ".emf"));
    }

    private void SelectNext()
    {
        int start = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index + 1 : 0;
        for (int i = start; i < _grid.Rows.Count; i++)
            if ((string?)_grid.Rows[i].Tag == "NEEDS_REVIEW")
            {
                _grid.ClearSelection();
                _grid.Rows[i].Selected = true;
                _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 3);
                _latex.Focus();
                return;
            }
        if (start < _grid.Rows.Count) { _grid.ClearSelection(); _grid.Rows[start].Selected = true; }
    }

    private void UpdateButtons()
    {
        var r = Selected;
        _save.Enabled = r != null && IsEquation(r);   // saving a fix does not touch a running conversion
        _undo.Enabled = r != null && r.Correction != null;
        int pending = _rows.Where(x => x.DisplayStatus == "CORRECTED").Select(x => x.Sha).Distinct().Count();
        int review = _rows.Count(x => x.DisplayStatus == "NEEDS_REVIEW");
        int conv = _rows.Count(x => x.DisplayStatus == "CONVERTED");
        _pending.Text = _rows.Count == 0 ? "" : pending > 0
            ? $"{conv} converted · {review} need review · {pending} fix(es) saved, not yet in the document →"
            : $"{conv} converted · {review} need review";
        _pending.ForeColor = pending > 0 ? Ui.Warn : Ui.Muted;
        _pending.Font = pending > 0 ? Ui.Bold : Ui.Base;
        _apply.Enabled = _corrections.Count > 0 && !_main.Busy && InputPath() != null;
        _openDoc.Enabled = OutputDocx() != null;
    }

    public void SetBusy(bool busy) => UpdateButtons();

    // ------------------------------------------------------------------ re-convert

    private string? InputPath()
    {
        var rep = Path.Combine(_outRoot, _stem, "FINAL_REPORT.md");
        if (!File.Exists(rep)) return null;
        foreach (var l in File.ReadLines(rep))
            if (l.StartsWith("- Input: `", StringComparison.Ordinal))
            {
                var p = l["- Input: `".Length..];
                p = p[..p.IndexOf('`')];
                return File.Exists(p) ? p : null;
            }
        return null;
    }

    private string? OutputDocx()
    {
        var dir = Path.Combine(_outRoot, _stem);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.docx").OrderBy(f => f.Contains("GATE3_FAILED")).FirstOrDefault() : null;
    }

    private async Task ApplyAsync()
    {
        var input = InputPath();
        if (input == null) { MessageBox.Show(this, "The original document could not be found, so it cannot be re-converted.", "MathMorph"); return; }
        var ok = MessageBox.Show(this,
            $"Re-convert {Path.GetFileName(input)} with {_corrections.Count} correction(s)?\n\nThe converted document in the results folder is replaced. The original document is not changed.",
            "Apply corrections", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;
        var args = new List<string> { input, _outRoot, "--force", "--latex-overrides", CorrectionsFile };
        args.AddRange(_main.ModeArgs());
        int code = 0;
        await _main.RunBatchAsync(async (ct, log) =>
        {
            _main.SetProgress(0, 1, $"Re-converting {Path.GetFileName(input)} with corrections…");
            code = await CliRunner.RunAsync(args, log, ct);
            _main.SetProgress(1, 1, "");
        });
        _main.SetStatus(code == 0 ? "Corrections applied. Converted document updated." : code == -1 ? "Cancelled." : "Re-convert finished with problems; see the log.");
        Reload(_outRoot, _stem);
    }

    // ------------------------------------------------------------------ helpers

    private static string Unguard(string s) => s.Length > 1 && s[0] == '\'' && "=+-@\t\r".Contains(s[1]) ? s[1..] : s;

    /// <summary>Same naming as the engine's render files: first 12 hex chars of SHA-256 of the LaTeX.</summary>
    private static string StableHash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

    private static List<string> ParseCsv(string line)
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
}
