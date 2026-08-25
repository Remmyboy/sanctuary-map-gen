using System.Linq;
using System.Windows.Forms;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Gui
{
    // One window, one job: pick a Supreme Commander map, pick a texture mode,
    // convert, deploy. The FA-textures mode is gated on the user's own
    // Forged Alliance install - the tool ships no GPG art, and if env.scd is
    // not on this machine the option simply is not available.
    public sealed class MainForm : Form
    {
        readonly ComboBox _mapPicker = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 430 };
        readonly TextBox _faPath = new() { Width = 340 };
        readonly TextBox _sanctuaryPath = new() { Width = 340 };
        readonly RadioButton _modeFa = new() { Text = "Original FA textures (local play only)", AutoSize = true };
        readonly RadioButton _modeCc0 = new() { Text = "CC0 textures (shareable)", AutoSize = true, Checked = true };
        readonly CheckBox _deploy = new() { Text = "Deploy to Sanctuary (both game and editor)", AutoSize = true, Checked = true };
        readonly Button _convert = new() { Text = "Convert", Width = 120, Height = 34 };
        readonly TextBox _log = new()
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Font = new System.Drawing.Font("Consolas", 9f),
        };

        readonly List<string> _mapFolders = new();
        string _packDir, _tableCsv;

        public MainForm()
        {
            Text = "Sanctuary Map Converter";
            Width = 900; Height = 640;
            StartPosition = FormStartPosition.CenterScreen;

            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3,
                Padding = new Padding(10),
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddRow(top, "Supreme Commander map", _mapPicker, MakeButton("Browse...", PickSourceFolder));
            AddRow(top, "Forged Alliance install", _faPath, MakeButton("Browse...", () => PickFolder(_faPath, RefreshFaGate)));
            AddRow(top, "Sanctuary install", _sanctuaryPath, MakeButton("Browse...", () => PickFolder(_sanctuaryPath, null)));

            var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            modes.Controls.Add(_modeCc0);
            modes.Controls.Add(_modeFa);
            modes.Controls.Add(_deploy);
            top.Controls.Add(new Label { Text = "Textures", AutoSize = true, Anchor = AnchorStyles.Left });
            top.Controls.Add(modes);
            top.Controls.Add(_convert);
            _convert.Anchor = AnchorStyles.Right;

            Controls.Add(_log);
            Controls.Add(top);

            _convert.Click += (_, _) => Convert();
            Load += (_, _) => Detect();
        }

        static Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => onClick();
            return b;
        }

        static void AddRow(TableLayoutPanel t, string label, Control mid, Control right)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            mid.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(mid);
            t.Controls.Add(right);
        }

        void Detect()
        {
            _faPath.Text = GamePaths.FindFaInstall() ?? "";
            _sanctuaryPath.Text = GamePaths.FindSanctuaryInstall() ?? "";
            (_packDir, _tableCsv) = GamePaths.DataFiles(GamePaths.FindDataDir());

            if (_packDir == null || !File.Exists(_tableCsv))
            {
                _modeCc0.Enabled = false;
                _modeCc0.Text = "CC0 textures (bundled data missing)";
                _modeFa.Checked = true;
            }
            RefreshFaGate();
            RefreshMapList();

            Log("Sanctuary Map Converter");
            Log($"  Forged Alliance: {(_faPath.Text.Length > 0 ? _faPath.Text : "not found - FA-textures mode disabled")}");
            Log($"  Sanctuary:       {(_sanctuaryPath.Text.Length > 0 ? _sanctuaryPath.Text : "not found - set it to deploy")}");
            Log($"  CC0 library:     {(_modeCc0.Enabled ? _packDir : "missing")}");
            Log($"  {_mapFolders.Count} source maps found. Pick one and press Convert.");
            Log("");
        }

        void RefreshFaGate()
        {
            bool haveFa = _faPath.Text.Length > 0 && File.Exists(GamePaths.ScdPath(_faPath.Text));
            _modeFa.Enabled = haveFa;
            if (!haveFa)
            {
                _modeFa.Text = "Original FA textures (needs your Forged Alliance install)";
                if (_modeFa.Checked && _modeCc0.Enabled) _modeCc0.Checked = true;
            }
            else _modeFa.Text = "Original FA textures (local play only)";
        }

        void RefreshMapList()
        {
            _mapFolders.Clear();
            _mapPicker.Items.Clear();
            foreach (var root in GamePaths.SourceMapRoots(_faPath.Text.Length > 0 ? _faPath.Text : null))
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (Directory.EnumerateFiles(dir, "*.scmap").Any())
                    {
                        _mapFolders.Add(dir);
                        _mapPicker.Items.Add(Path.GetFileName(dir));
                    }
            if (_mapPicker.Items.Count > 0) _mapPicker.SelectedIndex = 0;
        }

        void PickSourceFolder()
        {
            using var d = new FolderBrowserDialog { Description = "Folder containing the .scmap" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _mapFolders.Add(d.SelectedPath);
            _mapPicker.Items.Add(Path.GetFileName(d.SelectedPath));
            _mapPicker.SelectedIndex = _mapPicker.Items.Count - 1;
        }

        void PickFolder(TextBox target, Action after)
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) != DialogResult.OK) return;
            target.Text = d.SelectedPath;
            after?.Invoke();
            RefreshMapList();
        }

        void Convert()
        {
            if (_mapPicker.SelectedIndex < 0) { Log("pick a map first"); return; }
            string source = _mapFolders[_mapPicker.SelectedIndex];
            bool cc0 = _modeCc0.Checked;
            string sanctuary = _sanctuaryPath.Text;
            bool deploy = _deploy.Checked && sanctuary.Length > 0;

            var o = new ConvertOptions
            {
                Source = source,
                Cc0Textures = cc0,
                ScdPath = _faPath.Text.Length > 0 ? GamePaths.ScdPath(_faPath.Text) : null,
                PackDir = _packDir,
                TableCsv = _tableCsv,
                OutputMapsRoot = sanctuary.Length > 0
                    ? GamePaths.EngineMaps(sanctuary)
                    : Path.Combine(AppContext.BaseDirectory, "converted"),
            };

            _convert.Enabled = false;
            Log($"- converting {Path.GetFileName(source)} ({(cc0 ? "CC0 textures" : "FA textures")}) -");

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var result = new Converter(o, s => Invoke(() => Log(s))).Run();
                    if (deploy) Deployer.Deploy(result.MapDir, sanctuary, s => Invoke(() => Log(s)));
                    Invoke(() =>
                    {
                        Log($"DONE  {result.DisplayName}: {result.Spawns} spawns, {result.Alloys} alloys, {result.Props:n0} props");
                        Log("Restart Sanctuary before loading the map - it caches map files at launch.");
                        Log("");
                    });
                }
                catch (Exception e)
                {
                    Invoke(() => { Log("FAILED  " + e.Message); Log(""); });
                }
                finally
                {
                    Invoke(() => _convert.Enabled = true);
                }
            });
        }

        void Log(string s) => _log.AppendText(s + Environment.NewLine);
    }
}
