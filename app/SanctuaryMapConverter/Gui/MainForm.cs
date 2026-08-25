using System.Linq;
using System.Windows.Forms;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Gui
{
    // One window, two jobs: convert a Supreme Commander map, or generate a
    // random one - then deploy. The FA-textures mode is gated on the user's
    // own Forged Alliance install: the tool ships no GPG art, and if env.scd
    // is not on this machine the option simply is not available.
    public sealed class MainForm : Form
    {
        // -- convert --
        readonly ComboBox _mapPicker = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 430 };
        readonly RadioButton _modeFa = new() { Text = "Original FA textures (local play only)", AutoSize = true };
        readonly RadioButton _modeCc0 = new() { Text = "CC0 textures (shareable)", AutoSize = true, Checked = true };
        readonly ComboBox _convBiome = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly Button _convert = new() { Text = "Convert", Width = 120, Height = 32 };

        // -- generate --
        readonly ComboBox _style = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        readonly ComboBox _biome = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        readonly ComboBox _players = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50 };
        readonly TextBox _seed = new() { Width = 90, PlaceholderText = "random" };
        readonly NumericUpDown _count = new() { Width = 50, Minimum = 1, Maximum = 20, Value = 1 };
        readonly Button _generate = new() { Text = "Generate", Width = 120, Height = 32 };

        // -- shared --
        readonly TextBox _faPath = new() { Width = 340 };
        readonly TextBox _sanctuaryPath = new() { Width = 340 };
        readonly CheckBox _deploy = new() { Text = "Deploy to Sanctuary (both game and editor)", AutoSize = true, Checked = true };
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
            Width = 940; Height = 760;
            StartPosition = FormStartPosition.CenterScreen;

            var paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 0) };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddRow(paths, "Forged Alliance install", _faPath, MakeButton("Browse...", () => PickFolder(_faPath, RefreshFaGate)));
            AddRow(paths, "Sanctuary install", _sanctuaryPath, MakeButton("Browse...", () => PickFolder(_sanctuaryPath, null)));
            paths.Controls.Add(new Label());
            paths.Controls.Add(_deploy);
            paths.Controls.Add(new Label());

            var convertBox = new GroupBox { Text = "Convert a Supreme Commander map", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var ct = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddRow(ct, "Source map", _mapPicker, MakeButton("Browse...", PickSourceFolder));
            var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            modes.Controls.Add(_modeCc0);
            modes.Controls.Add(_modeFa);
            ct.Controls.Add(new Label { Text = "Textures", AutoSize = true, Anchor = AnchorStyles.Left });
            ct.Controls.Add(modes);
            ct.Controls.Add(_convert);
            _convert.Anchor = AnchorStyles.Right;
            // The biome is the lighting/fog base; the source map's own sun and
            // fog override what translates.
            _convBiome.Items.AddRange(new object[] { "Tropical", "Highlands", "Winter", "Evergreen", "Arid" });
            _convBiome.SelectedIndex = 0;
            AddRow(ct, "Lighting biome", _convBiome, new Label());
            convertBox.Controls.Add(ct);

            var genBox = new GroupBox { Text = "Generate a random map", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var gf = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            _style.Items.AddRange(new object[] { "Random", "RiverCrossing", "Mesas", "Plateaus", "Basin", "Open" });
            _biome.Items.AddRange(new object[] { "Random", "Highlands", "Tropical", "Winter", "Evergreen", "Arid" });
            _size.Items.AddRange(new object[] { "256", "512", "1024", "2048" });
            _players.Items.AddRange(new object[] { "2", "3", "4", "6", "8" });
            _style.SelectedIndex = 0; _biome.SelectedIndex = 0;
            _size.SelectedItem = "512"; _players.SelectedItem = "2";
            foreach (var (lbl, c) in new (string, Control)[]
            {
                ("Style", _style), ("Biome", _biome), ("Size", _size),
                ("Players", _players), ("Seed", _seed), ("Count", _count),
            })
            {
                gf.Controls.Add(new Label { Text = lbl, AutoSize = true, Margin = new Padding(6, 8, 2, 0) });
                gf.Controls.Add(c);
            }
            gf.Controls.Add(_generate);
            _generate.Margin = new Padding(18, 2, 2, 2);
            genBox.Controls.Add(gf);

            Controls.Add(_log);
            // Dock order: last added Top control sits highest.
            Controls.Add(genBox);
            Controls.Add(convertBox);
            Controls.Add(paths);

            _convert.Click += (_, _) => RunConvert();
            _generate.Click += (_, _) => RunGenerate();
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
            Log($"  {_mapFolders.Count} source maps found. Convert one, or generate a fresh random map.");
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

        string OutputRoot(string sanctuary) => sanctuary.Length > 0
            ? GamePaths.EngineMaps(sanctuary)
            : Path.Combine(AppContext.BaseDirectory, "converted");

        void RunConvert()
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
                Biome = (string)_convBiome.SelectedItem,
                ScdPath = _faPath.Text.Length > 0 ? GamePaths.ScdPath(_faPath.Text) : null,
                PackDir = _packDir,
                TableCsv = _tableCsv,
                OutputMapsRoot = OutputRoot(sanctuary),
            };

            RunJob(_convert, $"- converting {Path.GetFileName(source)} ({(cc0 ? "CC0 textures" : "FA textures")}) -", log =>
            {
                var result = new Converter(o, log).Run();
                if (deploy) Deployer.Deploy(result.MapDir, sanctuary, log);
                log($"DONE  {result.DisplayName}: {result.Spawns} spawns, {result.Alloys} alloys, {result.Props:n0} props");
            });
        }

        void RunGenerate()
        {
            string sanctuary = _sanctuaryPath.Text;
            bool deploy = _deploy.Checked && sanctuary.Length > 0;
            int seed = -1;
            if (_seed.Text.Trim().Length > 0 && !int.TryParse(_seed.Text.Trim(), out seed))
            {
                Log("seed must be a number (or blank for random)");
                return;
            }

            var o = new RandomMapOptions
            {
                Seed = seed,
                Size = int.Parse((string)_size.SelectedItem),
                Players = int.Parse((string)_players.SelectedItem),
                Style = (string)_style.SelectedItem,
                Biome = (string)_biome.SelectedItem,
                Count = (int)_count.Value,
                MapsRoot = OutputRoot(sanctuary),
                Force = true,
                Validate = sanctuary.Length > 0 ? new ValidateOptions
                {
                    Managed = Path.Combine(sanctuary, "map-editor", "SanctuaryMapEditor_Data", "Managed"),
                    CheckTextures = true,
                    LuaCheck = true,
                    GameRoot = sanctuary,
                } : null,
            };

            RunJob(_generate, $"- generating {o.Count} {o.Style}/{o.Biome} map(s) -", log =>
            {
                var results = RandomMap.Run(o, log);
                foreach (var r in results.Where(r => r.Accepted && deploy))
                    Deployer.Deploy(r.MapDir, sanctuary, log);
                int ok = results.Count(r => r.Accepted);
                log($"DONE  {ok} of {results.Count} map(s) generated");
            });
        }

        void RunJob(Button button, string banner, Action<Action<string>> work)
        {
            button.Enabled = false;
            Log(banner);
            void UiLog(string s) => Invoke(() => Log(s));
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    work(UiLog);
                    UiLog("Restart Sanctuary before loading new maps - it caches map files at launch.");
                    UiLog("");
                }
                catch (Exception e)
                {
                    UiLog("FAILED  " + e.Message);
                    UiLog("");
                }
                finally
                {
                    Invoke(() => button.Enabled = true);
                }
            });
        }

        void Log(string s) => _log.AppendText(s + Environment.NewLine);
    }
}
