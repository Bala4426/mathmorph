using System.Globalization;
using System.Text.RegularExpressions;

namespace MathMorph.Gui;

/// <summary>Screen 1: pick documents and output folder, choose a mode, convert.</summary>
internal sealed class ConvertPage : UserControl
{
    private readonly MainForm _main;
    private readonly ListView _docs = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Ui.Base, AllowDrop = true, HideSelection = false,
    };
    private readonly DropZone _empty = new()
    {
        Title = "Drop Word documents here",
        Hint = "Drag .docx files or a folder onto this area, or click to choose files.",
        Dock = DockStyle.Fill, AccessibleName = "Add Word documents: drop files here or click to choose",
    };
    private readonly StatTile _tFound = new("Equations found", Ui.Accent);
    private readonly StatTile _tConverted = new("Converted", Ui.Ok);
    private readonly StatTile _tMathType = new("MathType converted", Ui.Info);
    private readonly StatTile _tReview = new("Need review", Ui.Warn);
    private readonly StatTile _tFailed = new("Documents with problems", Ui.Bad);
    private TableLayoutPanel _grid = null!;
    private readonly Label _docSummary = Ui.Label("", Ui.Small, Ui.Muted);
    private readonly TextBox _output = new() { Dock = DockStyle.Fill, Font = Ui.Base, Text = Path.Combine(CliRunner.ProjectRoot(), "output") };
    private readonly RadioButton _modeSafe = new() { Text = "Safe: only equations that pass the check", AutoSize = true, Font = Ui.Base };
    private readonly RadioButton _modeAll = new() { Text = "Convert all, then proofread (recommended)", AutoSize = true, Font = Ui.Base, Checked = true };
    private readonly RadioButton _modeCustom = new() { Text = "Custom score threshold", AutoSize = true, Font = Ui.Base };
    private readonly NumericUpDown _threshold = new() { DecimalPlaces = 2, Increment = 0.05M, Minimum = 0, Maximum = 1, Value = 0.85M, Width = 70, Enabled = false };
    private readonly CheckBox _mathType = new() { Text = "Convert MathType equation objects", AutoSize = true, Font = Ui.Base, Checked = true };
    private readonly Label _mathTypeInfo = Ui.Label("", Ui.Small, Ui.Muted);
    private readonly CheckBox _overwrite = new() { Text = "Replace earlier results for these documents", AutoSize = true, Font = Ui.Base, Checked = true };
    private readonly CheckBox _dryRun = new() { Text = "Preview only: analyse, write no documents", AutoSize = true, Font = Ui.Base };
    private readonly Button _convert = Ui.Primary("Convert documents");
    private readonly Button _review = Ui.Secondary("Review results  →");
    private int _mathTypeCountVersion;

    public ConvertPage(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Ui.Bg;
        Padding = new Padding(8);

        var grid = _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Ui.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        // --- documents card
        var (docCard, docBody) = Ui.MakeCard("Documents", "Word .docx files to convert. Originals are never changed.");
        _docs.AccessibleName = "Documents to convert";
        _docs.OwnerDraw = true;
        _docs.SmallImageList = new ImageList { ImageSize = new Size(1, 32) }; // taller rows
        _docs.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Ui.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var r = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Ui.SmallBold, r, Ui.Muted, flags);
        };
        _docs.DrawItem += (_, e) => { };
        _docs.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            bool sel = e.Item.Selected;
            using (var b = new SolidBrush(sel ? Ui.AccentSoft : Ui.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Color.FromArgb(238, 241, 245))) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            float s = _docs.DeviceDpi / 96f;
            if (e.ColumnIndex == 1)
            {
                var (fore, back) = PillColors(e.SubItem.Tag as string);
                Ui.DrawPill(e.Graphics, e.Bounds, e.SubItem.Text, fore, back, s);
                return;
            }
            var font = e.ColumnIndex == 0 ? Ui.Bold : Ui.Base;
            var color = e.ColumnIndex == 4 ? Ui.Muted : Ui.Text;
            var align = _docs.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, Rectangle.Inflate(e.Bounds, -(int)(8 * s), 0), color,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        };
        _docs.Columns.Add("Document", 260);
        _docs.Columns.Add("Status", 150);
        _docs.Columns.Add("Converted", 80, HorizontalAlignment.Right);
        _docs.Columns.Add("To review", 80, HorizontalAlignment.Right);
        _docs.Columns.Add("Folder", 260);
        // ListView columns and row height are not auto-scaled: scale them once the control knows its DPI
        _docs.HandleCreated += (_, _) =>
        {
            float s = _docs.DeviceDpi / 96f;
            if (s <= 1.01f) return;
            int[] w = { 150, 90, 90 };
            for (int i = 0; i < w.Length; i++) _docs.Columns[i + 1].Width = (int)(w[i] * s);
            _docs.SmallImageList!.ImageSize = new Size(1, (int)(32 * s));
            BeginInvoke(FitColumns);
        };
        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card };
        listHost.Controls.Add(_docs);
        listHost.Controls.Add(_empty);
        var docButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0, 12, 0, 0), BackColor = Ui.Card, WrapContents = false };
        var addFiles = Ui.Secondary("Add files…");
        var addFolder = Ui.Secondary("Add folder…");
        var remove = Ui.Secondary("Remove");
        var clear = Ui.Secondary("Clear");
        docButtons.Controls.AddRange(new Control[] { addFiles, addFolder, remove, clear, _docSummary });
        _docSummary.Margin = new Padding(6, 10, 0, 0);
        remove.AccessibleName = "Remove selected documents";
        clear.AccessibleName = "Clear document list";
        docBody.Controls.Add(listHost);
        docBody.Controls.Add(docButtons);
        grid.Controls.Add(docCard, 0, 0);

        // --- right column: output + options
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Ui.Bg, Margin = new Padding(0) };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var (outCard, outBody) = Ui.MakeCard("Save results to", "Each document gets its own subfolder.");
        var outRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 3, BackColor = Ui.Card };
        _output.AccessibleName = "Output folder";
        _threshold.AccessibleName = "Custom score threshold";
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browseOut = Ui.Secondary("Browse…");
        var openOut = Ui.Secondary("Open");
        _output.Margin = new Padding(0, 6, 8, 0);
        browseOut.AccessibleName = "Browse for output folder";
        openOut.AccessibleName = "Open output folder";
        outRow.Controls.Add(_output, 0, 0);
        outRow.Controls.Add(browseOut, 1, 0);
        outRow.Controls.Add(openOut, 2, 0);
        outBody.Controls.Add(outRow);
        right.Controls.Add(outCard, 0, 0);

        var (optCard, optBody) = Ui.MakeCard("How to convert");
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.Card };
        opts.AutoScroll = false; opts.HorizontalScroll.Enabled = false; opts.HorizontalScroll.Visible = false; opts.HorizontalScroll.Maximum = 0; opts.AutoScroll = true;
        var customRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        customRow.Controls.AddRange(new Control[] { _modeCustom, _threshold });
        _threshold.Margin = new Padding(6, 0, 0, 0);
        opts.Controls.Add(_modeAll);
        opts.Controls.Add(Hint("Every recognised equation is converted. Check them in Review and fix any mistakes."));
        opts.Controls.Add(_modeSafe);
        opts.Controls.Add(Hint("Only equations whose re-rendered image matches the picture closely enough (setting in config\\threshold.json). Usually converts few."));
        opts.Controls.Add(customRow);
        opts.Controls.Add(Hint("Converts equations scoring at or above this similarity (0–1)."));
        opts.Controls.Add(Separator());
        opts.Controls.Add(_mathType);
        opts.Controls.Add(_mathTypeInfo);
        opts.Controls.Add(_overwrite);
        opts.Controls.Add(_dryRun);
        optBody.Controls.Add(opts);
        opts.ClientSizeChanged += (_, _) =>
        {
            // wrap hints to the card width so the options never scroll sideways
            float s = opts.DeviceDpi / 96f;
            int w = Math.Max((int)(160 * s), opts.ClientSize.Width - (int)(36 * s));
            foreach (Control c in opts.Controls)
            {
                if (c is Label l) l.MaximumSize = new Size(w - l.Margin.Left, 0);
                else if (c is Panel p && p.Height <= 2) p.Width = w;
            }
        };
        right.Controls.Add(optCard, 0, 1);
        grid.Controls.Add(right, 1, 0);

        // --- action bar
        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Ui.Bg, Margin = new Padding(0, 4, 0, 0), Visible = false };
        foreach (var t in new[] { _tFound, _tConverted, _tMathType, _tReview, _tFailed })
        {
            tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            tiles.Controls.Add(t);
        }
        tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tiles.AccessibleName = "Results summary";
        _tiles = tiles;
        grid.Controls.Add(tiles, 0, 1);
        grid.SetColumnSpan(tiles, 2);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 12, 0, 0), BackColor = Ui.Bg, WrapContents = false };
        _convert.Margin = new Padding(0);
        bar.Controls.Add(_convert);
        bar.Controls.Add(_review);
        _review.Margin = new Padding(0, 0, 10, 0);
        _review.Visible = false;
        grid.Controls.Add(bar, 0, 2);
        grid.SetColumnSpan(bar, 2);
        Controls.Add(grid);

        // --- behaviour
        foreach (Control c in new Control[] { _docs, _empty })
        {
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) => { _empty.Highlight = false; if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); };
        }
        _empty.DragEnter += (_, _) => _empty.Highlight = true;
        _empty.DragLeave += (_, _) => _empty.Highlight = false;
        _empty.Click += (_, _) => addFiles.PerformClick();
        addFiles.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Word documents (*.docx)|*.docx", Multiselect = true, Title = "Add Word documents" };
            if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
        };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Add every .docx in this folder" };
            if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
        };
        remove.Click += (_, _) => { foreach (ListViewItem i in _docs.SelectedItems) _docs.Items.Remove(i); DocsChanged(); };
        clear.Click += (_, _) => { _docs.Items.Clear(); DocsChanged(); };
        _docs.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) remove.PerformClick(); };
        _docs.DoubleClick += (_, _) => { if (_docs.SelectedItems.Count == 1) OpenReviewFor((string)_docs.SelectedItems[0].Tag!); };
        browseOut.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Folder for converted documents and reports", SelectedPath = _output.Text };
            if (d.ShowDialog(this) == DialogResult.OK) _output.Text = d.SelectedPath;
        };
        openOut.Click += (_, _) => MainForm.OpenFolder(_output.Text);
        _modeCustom.CheckedChanged += (_, _) => _threshold.Enabled = _modeCustom.Checked;
        _convert.Click += async (_, _) => await ConvertAsync();
        // right-click menu on the document list: work on one document while reviewing another
        var menu = new ContextMenuStrip { Font = Ui.Base };
        var miConvert = new ToolStripMenuItem("Convert selected") { ShortcutKeyDisplayString = "Ctrl+Enter" };
        var miReview = new ToolStripMenuItem("Open in Review");
        var miFolder = new ToolStripMenuItem("Open results folder");
        var miRemove = new ToolStripMenuItem("Remove from list") { ShortcutKeyDisplayString = "Del" };
        menu.Items.AddRange(new ToolStripItem[] { miConvert, miReview, miFolder, new ToolStripSeparator(), miRemove });
        menu.Opening += (_, e) =>
        {
            int n = _docs.SelectedItems.Count;
            if (n == 0) { e.Cancel = true; return; }
            miConvert.Text = n == 1 ? $"Convert \"{_docs.SelectedItems[0].Text}\"" : $"Convert {n} selected documents";
            miConvert.Enabled = !_main.Busy;
            var stem = Path.GetFileNameWithoutExtension((string)_docs.SelectedItems[0].Tag!);
            bool hasResult = File.Exists(Path.Combine(OutputRoot, stem, "report.csv"));
            miReview.Enabled = n == 1 && hasResult;
            miFolder.Enabled = n == 1 && Directory.Exists(Path.Combine(OutputRoot, stem));
            miRemove.Enabled = !_main.Busy;
        };
        miConvert.Click += async (_, _) => await ConvertAsync(_docs.SelectedItems.Cast<ListViewItem>().ToList());
        miReview.Click += (_, _) => OpenReviewFor((string)_docs.SelectedItems[0].Tag!);
        miFolder.Click += (_, _) => MainForm.OpenFolder(Path.Combine(OutputRoot, Path.GetFileNameWithoutExtension((string)_docs.SelectedItems[0].Tag!)));
        miRemove.Click += (_, _) => remove.PerformClick();
        _docs.ContextMenuStrip = menu;
        _docs.MouseDown += (_, e) =>
        {
            // right-clicking a row selects it first, as in Explorer
            if (e.Button != MouseButtons.Right) return;
            var hit = _docs.HitTest(e.Location).Item;
            if (hit != null && !hit.Selected) { _docs.SelectedItems.Clear(); hit.Selected = true; }
        };
        _docs.KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter && _docs.SelectedItems.Count > 0 && !_main.Busy)
            { e.SuppressKeyPress = true; await ConvertAsync(_docs.SelectedItems.Cast<ListViewItem>().ToList()); }
        };
        _review.Click += (_, _) =>
        {
            var first = _docs.SelectedItems.Count > 0 ? _docs.SelectedItems[0] : _docs.Items.Count > 0 ? _docs.Items[0] : null;
            if (first != null) OpenReviewFor((string)first.Tag!);
        };
        _docs.ClientSizeChanged += (_, _) => FitColumns();
        _docs.VisibleChanged += (_, _) => FitColumns();
        _docs.Resize += (_, _) => { if (_docs.IsHandleCreated) _docs.BeginInvoke(FitColumns); };
        var defaultInput = Path.Combine(CliRunner.ProjectRoot(), "input");
        if (Directory.Exists(defaultInput)) AddPaths(new[] { defaultInput }, quiet: true);
        DocsChanged();
    }

    public string OutputRoot => _output.Text.Trim();
    public int DocumentCount => _docs.Items.Count;

    /// <summary>Fixed status/count columns; Document and Folder share the rest so the list never scrolls sideways.</summary>
    private void FitColumns()
    {
        if (_docs.Columns.Count < 5 || _docs.ClientSize.Width <= 0) return;
        float s = _docs.DeviceDpi / 96f;
        int fixedW = _docs.Columns[1].Width + _docs.Columns[2].Width + _docs.Columns[3].Width;
        int rest = Math.Max((int)(160 * s), _docs.ClientSize.Width - fixedW - SystemInformation.VerticalScrollBarWidth - (int)(4 * s));
        _docs.Columns[0].Width = (int)(rest * 0.6);
        _docs.Columns[4].Width = rest - _docs.Columns[0].Width;
    }
    private TableLayoutPanel _tiles = null!;

    private static (Color Fore, Color Back) PillColors(string? key) => key switch
    {
        "done" => (Ui.Ok, Ui.OkSoft),
        "review" => (Ui.Warn, Ui.WarnSoft),
        "failed" => (Ui.Bad, Ui.BadSoft),
        "skipped" => (Ui.Info, Ui.InfoSoft),
        "busy" => (Ui.Accent, Ui.AccentSoft),
        _ => (Ui.Neutral, Ui.NeutralSoft),
    };

    private void SetStatusCell(ListViewItem it, string text, string key)
    {
        it.SubItems[1].Text = text;
        it.SubItems[1].Tag = key;
        _docs.Invalidate(it.Bounds);
    }

    /// <summary>Reads report.csv of each converted document and fills the summary tiles.</summary>
    private void ShowSummary(IEnumerable<ListViewItem> items, int failedDocs)
    {
        int found = 0, conv = 0, mt = 0, review = 0;
        foreach (var it in items)
        {
            var report = Path.Combine(OutputRoot, Path.GetFileNameWithoutExtension((string)it.Tag!), "report.csv");
            if (!File.Exists(report)) continue;
            try
            {
                foreach (var line in File.ReadLines(report).Skip(1))
                {
                    var c = SplitCsv(line);
                    if (c.Count < 10) continue;
                    string cat = c[5], st = c[6], note = c[9];
                    bool mathType = cat == "MATHTYPE" || note.StartsWith("MathType", StringComparison.OrdinalIgnoreCase);
                    bool eq = st is "CONVERTED" or "NEEDS_REVIEW" || cat is "CANDIDATE" or "OLE_PREVIEW" or "MATHTYPE" || mathType;
                    if (!eq) continue;
                    found++;
                    if (st == "CONVERTED") { conv++; if (mathType) mt++; }
                    else if (st == "NEEDS_REVIEW") review++;
                }
            }
            catch (IOException) { }
        }
        _tFound.Value = found; _tConverted.Value = conv; _tMathType.Value = mt; _tReview.Value = review; _tFailed.Value = failedDocs;
        _tiles.Visible = true;
        _grid.RowStyles[1].Height = 96 * DeviceDpi / 96f;
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
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

    private static Label Hint(string s) => new()
    {
        Text = s, AutoSize = true, MaximumSize = new Size(360, 0), Font = Ui.Small, ForeColor = Ui.Muted, Margin = new Padding(20, 0, 0, 8),
    };

    private static Control Separator() => new Panel { Height = 1, Width = 340, BackColor = Ui.Border, Margin = new Padding(0, 4, 0, 10) };

    /// <summary>Arguments that express the chosen mode (shared with the Review screen's re-convert).</summary>
    public List<string> ModeArgs()
    {
        var a = new List<string>();
        if (_modeAll.Checked) a.AddRange(new[] { "--min-score", "0" });
        else if (_modeCustom.Checked) a.AddRange(new[] { "--min-score", _threshold.Value.ToString(CultureInfo.InvariantCulture) });
        a.AddRange(new[] { "--mathtype", _mathType.Checked ? "convert" : "skip" });
        return a;
    }

    private void AddPaths(IEnumerable<string> paths, bool quiet = false)
    {
        var existing = _docs.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (var p in paths)
        {
            IEnumerable<string> files = Directory.Exists(p)
                ? Directory.GetFiles(p, "*.docx", SearchOption.TopDirectoryOnly)
                : File.Exists(p) && p.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? new[] { p } : Array.Empty<string>();
            if (!files.Any() && !quiet) skipped++;
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(f);
                if (Path.GetFileName(full).StartsWith("~$", StringComparison.Ordinal) || !existing.Add(full)) continue;
                var it = new ListViewItem(Path.GetFileName(full)) { Tag = full };
                it.SubItems.Add(new ListViewItem.ListViewSubItem(it, "Ready") { Tag = "ready" });
                it.SubItems.Add("");
                it.SubItems.Add("");
                it.SubItems.Add(Path.GetDirectoryName(full));
                _docs.Items.Add(it);
            }
        }
        if (skipped > 0) MessageBox.Show(this, "Only .docx files (or folders containing them) can be added.", "MathMorph", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DocsChanged();
    }

    private async void DocsChanged()
    {
        _empty.Visible = _docs.Items.Count == 0;
        _docs.Visible = _docs.Items.Count > 0;
        _docSummary.Text = _docs.Items.Count == 0 ? "" : _docs.Items.Count == 1 ? "1 document" : $"{_docs.Items.Count} documents";
        _main.UpdateStep();
        _convert.Enabled = _docs.Items.Count > 0 && !_main.Busy;
        // count MathType objects in the background (read-only)
        int version = ++_mathTypeCountVersion;
        var files = _docs.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (files.Count == 0) { _mathTypeInfo.Text = ""; return; }
        _mathTypeInfo.Text = "Checking for MathType equations…";
        int total = 0;
        foreach (var f in files) total += await CliRunner.CountMathTypeAsync(f);
        if (version != _mathTypeCountVersion) return;
        _mathTypeInfo.Text = total == 0 ? "No MathType equations found in these documents."
            : $"{total} MathType equation(s) found. They are converted exactly from the equation data in the file.";
        _mathTypeInfo.Margin = new Padding(20, 0, 0, 8);
    }

    private static readonly Regex DocLine = new(@"^\s+(?<name>.+?\.docx) \.\.\. (?<status>\S+) converted=(?<c>\d+) review=(?<r>\d+)", RegexOptions.Compiled);

    /// <summary>Converts every document in the list, or only <paramref name="only"/> (right-click → Convert).</summary>
    private async Task ConvertAsync(IList<ListViewItem>? only = null)
    {
        if (_main.Busy) { MessageBox.Show(this, "A conversion is already running. Wait for it to finish or press Cancel.", "MathMorph"); return; }
        if (string.IsNullOrWhiteSpace(OutputRoot)) { MessageBox.Show(this, "Choose where to save the results.", "MathMorph"); return; }
        var items = only?.ToList() ?? _docs.Items.Cast<ListViewItem>().ToList();
        if (items.Count == 0) return;
        foreach (var it in items)
        {
            var dir = Path.GetDirectoryName((string)it.Tag!)!;
            if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(OutputRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The output folder must be different from the folder that holds the documents.", "MathMorph", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        _review.Visible = false;
        _convert.Enabled = false;
        foreach (var it in items)
        {
            SetStatusCell(it, "Waiting", "ready");
            it.SubItems[2].Text = ""; it.SubItems[3].Text = "";
        }
        _tiles.Visible = false;
        _grid.RowStyles[1].Height = 0;
        int done = 0, converted = 0, review = 0, failed = 0;
        await _main.RunBatchAsync(async (ct, log) =>
        {
            foreach (var it in items)
            {
                if (ct.IsCancellationRequested) { SetStatusCell(it, "Cancelled", "ready"); continue; }
                SetStatusCell(it, "Converting…", "busy");
                it.EnsureVisible();
                _main.SetProgress(done, items.Count, $"Converting {it.Text} ({done + 1} of {items.Count}) — recognition can take a few minutes per document");
                var args = new List<string> { (string)it.Tag!, OutputRoot };
                args.AddRange(ModeArgs());
                if (_overwrite.Checked) args.Add("--force");
                if (_dryRun.Checked) args.Add("--dry-run");
                string status = "Failed";
                int docReview = 0;
                int code = await CliRunner.RunAsync(args, line =>
                {
                    log(line);
                    var m = DocLine.Match(line);
                    if (m.Success)
                    {
                        status = m.Groups["status"].Value;
                        BeginInvoke(() =>
                        {
                            it.SubItems[2].Text = m.Groups["c"].Value;
                            it.SubItems[3].Text = m.Groups["r"].Value;
                        });
                        converted += int.Parse(m.Groups["c"].Value);
                        review += int.Parse(m.Groups["r"].Value);
                        docReview = int.Parse(m.Groups["r"].Value);
                    }
                }, ct);
                if (code == -1) status = "Cancelled";
                var (label, key) = status switch
                {
                    "OK" => docReview > 0 ? ("Needs review", "review") : ("Converted", "done"),
                    "DRY_RUN" => ("Analysed", "done"),
                    "SKIPPED_EXISTS" => ("Skipped (exists)", "skipped"),
                    "Cancelled" => ("Cancelled", "ready"),
                    _ => (status == "GATE3_FAILED" ? "Failed checks" : "Failed", "failed"),
                };
                if (key == "failed") failed++;
                SetStatusCell(it, label, key);
                done++;
                _main.SetProgress(done, items.Count, "");
            }
        });
        _main.SetStatus(failed == 0
            ? $"Finished: {converted} equation(s) converted, {review} left for review."
            : $"Finished with {failed} problem document(s): {converted} converted, {review} left for review. See the log for details.");
        _convert.Enabled = true;
        _review.Visible = true;
        if (!_dryRun.Checked) ShowSummary(items, failed);
    }

    private void OpenReviewFor(string inputPath)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        _main.ShowReview(OutputRoot, stem);
    }

    public void SetBusy(bool busy) => _convert.Enabled = !busy && _docs.Items.Count > 0;
}
