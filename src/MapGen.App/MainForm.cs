using System.Diagnostics;
using MapGen.Formats;
using MapGen.Generation;
using MapGen.Terrain;
using MapGen.Theater;

namespace MapGen.App;

internal sealed class MainForm : Form
{
    private static readonly string[] Theaters = { "TEMPERATE", "SNOW", "URBAN", "NEWURBAN", "DESERT", "LUNAR" };
    private static readonly string[] TechTypes = { "oil", "repair", "hospital", "airport", "machineshop", "power", "seclab" };

    private readonly AppConfig config = AppConfig.Load();
    private readonly Dictionary<string, (TheaterData Data, List<ConnectedTileType> Connected)> theaterCache = new();
    private readonly string dataDir = FindDataDirectory();
    private readonly string workDir = Path.Combine(Path.GetTempPath(), "yr-mapgen");

    private ComboBox cbLayout, cbFormat, cbStyle, cbSize, cbShape, cbTheater, cbResources, cbGems, cbEconomy, cbSymmetry, cbTime;
    private TrackBar tbHills, tbCliffs, tbWater, tbTrees, tbGarrisons, tbOreSize, tbGemSize, tbDrills;
    private CheckBox chkTrails, chkWalls;
    private NumericUpDown nudSeed;
    private CheckBox chkRandomSeed, chkDefaultTech;
    private DataGridView gridTech;
    private TextBox txtGameDir, txtReport;
    private Label lblGameDirHint;
    private ZoomImageBox picture;
    private Button btnGenerate, btnRandom, btnSave, btnCopyToClient, btnOpenFolder;
    private Label lblStatus;

    private GenerationResult lastResult;
    private string lastMapPath;

    public MainForm()
    {
        Text = "YR Map Generator";
        Width = 1500;
        Height = 950;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        LoadFromConfig();
        FormClosing += (_, _) => { ReadIntoConfig(); config.Save(); };
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        Controls.Add(split);

        // Settings column.
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(8),
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.Panel1.Controls.Add(left);

        void Row(string label, Control control)
        {
            control.Dock = DockStyle.Fill;
            left.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
            left.Controls.Add(control);
        }

        cbFormat = Combo("1v1", "2v2", "3v3", "4v4", "2v2v2v2");
        cbStyle = EnumCombo<MapStyle>();
        cbSize = EnumCombo<MapSize>();
        cbShape = EnumCombo<MapShape>();
        cbLayout = EnumCombo<MapLayout>();
        cbTheater = Combo(Theaters.Select(t => t[0] + t.Substring(1).ToLowerInvariant()).ToArray());
        cbResources = EnumCombo<ResourceLevel>();
        cbGems = EnumCombo<GemPlacement>();
        cbEconomy = EnumCombo<EconomyMode>();
        cbSymmetry = Combo("Auto");
        cbTime = EnumCombo<TimeOfDay>();
        tbHills = Slider();
        tbCliffs = Slider();
        tbWater = Slider();
        tbTrees = Slider();
        tbGarrisons = Slider();
        tbOreSize = Slider(25, 250);
        tbGemSize = Slider(25, 250);
        tbDrills = Slider();
        chkTrails = new CheckBox { Text = "Dirt trails to tech and gems", AutoSize = true };
        chkWalls = new CheckBox { Text = "Sandbags around shared tech", AutoSize = true };
        nudSeed = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue / 2 };
        chkRandomSeed = new CheckBox { Text = "New random seed each time", AutoSize = true };

        Row("Format", cbFormat);
        Row("Style", cbStyle);
        Row("Size", cbSize);
        Row("Shape", cbShape);
        Row("Layout", cbLayout);
        Row("Symmetry", cbSymmetry);
        new ToolTip().SetToolTip(cbSymmetry, "None: lakes, cliffs and trees are not mirrored; bases, ore, tech and high ground still are, so the map stays fair.");
        Row("Theater", cbTheater);
        Row("Time of day", cbTime);
        Row("Hills", tbHills);
        Row("Cliffs", tbCliffs);
        Row("Water", tbWater);
        Row("Tree cover", tbTrees);
        Row("Garrisons", tbGarrisons);
        Row("", chkTrails);
        Row("", chkWalls);
        Row("Resources", cbResources);
        Row("Ore fields %", tbOreSize);
        Row("Gems", cbGems);
        Row("Gem fields %", tbGemSize);
        Row("Ore drills", tbDrills);
        Row("Economy", cbEconomy);
        Row("Seed", nudSeed);
        Row("", chkRandomSeed);

        chkDefaultTech = new CheckBox { Text = "Default tech for the format", AutoSize = true };
        chkDefaultTech.CheckedChanged += (_, _) => OnDefaultTechChanged();
        Row("Tech", chkDefaultTech);

        gridTech = BuildTechGrid();
        left.Controls.Add(gridTech);
        left.SetColumnSpan(gridTech, 2);

        // The game directory stays visible above the scrolling settings: previews and copying to the
        // client need it, so a new user should see it first.
        var game = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        game.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        game.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.Panel1.Controls.Add(game);

        txtGameDir = new TextBox();
        var btnBrowse = new Button { Text = "...", Width = 30, Dock = DockStyle.Right };
        btnBrowse.Click += (_, _) => BrowseGameDirectory();
        var gamePanel = new Panel { Height = 26, Dock = DockStyle.Fill };
        gamePanel.Controls.Add(txtGameDir);
        gamePanel.Controls.Add(btnBrowse);
        txtGameDir.Dock = DockStyle.Fill;
        game.Controls.Add(new Label { Text = "YR directory", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0), Font = new Font(Font, FontStyle.Bold) });
        game.Controls.Add(gamePanel);

        lblGameDirHint = new Label { AutoSize = true, MaximumSize = new Size(370, 0), Margin = new Padding(0, 2, 0, 6) };
        game.Controls.Add(lblGameDirHint);
        game.SetColumnSpan(lblGameDirHint, 2);
        txtGameDir.TextChanged += (_, _) => UpdateGameDirHint();

        // Actions stay visible below the scrolling settings.
        var actions = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        split.Panel1.Controls.Add(actions);

        btnGenerate = new Button { Text = "Generate", Height = 40, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
        btnGenerate.Click += async (_, _) => await GenerateAsync();
        btnRandom = new Button { Text = "All random", Height = 40, Dock = DockStyle.Fill };
        btnRandom.Click += async (_, _) => { RandomiseSettings(); await GenerateAsync(); };
        actions.Controls.Add(btnGenerate);
        actions.Controls.Add(btnRandom);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        btnSave = new Button { Text = "Save map as...", AutoSize = true, Enabled = false };
        btnSave.Click += (_, _) => SaveMapAs();
        btnCopyToClient = new Button { Text = "Copy to Maps\\Custom", AutoSize = true, Enabled = false };
        btnCopyToClient.Click += (_, _) => CopyToClient();
        btnOpenFolder = new Button { Text = "Open folder", AutoSize = true, Enabled = false };
        btnOpenFolder.Click += (_, _) => { if (lastMapPath != null) Process.Start("explorer.exe", $"/select,\"{lastMapPath}\""); };
        buttons.Controls.AddRange(new Control[] { btnSave, btnCopyToClient, btnOpenFolder });
        actions.Controls.Add(buttons);
        actions.SetColumnSpan(buttons, 2);

        lblStatus = new Label { AutoSize = true, MaximumSize = new Size(370, 0), ForeColor = Color.DimGray };
        actions.Controls.Add(lblStatus);
        actions.SetColumnSpan(lblStatus, 2);

        // Fill must be added after the bottom panel so it takes only the remaining space.
        left.BringToFront();

        // Preview and report.
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel2.Controls.Add(right);

        picture = new ZoomImageBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24) };
        right.Panel1.Controls.Add(picture);
        new ToolTip().SetToolTip(picture, "Wheel to zoom, drag to pan, double-click to fit");

        txtReport = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
        };
        right.Panel2.Controls.Add(txtReport);

        // Splitter distances can only be set once the containers have their real size.
        Load += (_, _) =>
        {
            split.SplitterDistance = 390;
            right.SplitterDistance = Math.Max(200, right.Height - 230);
        };

        // With default tech selected, the grid shows what will be used for the current format.
        foreach (var cb in new[] { cbFormat, cbStyle, cbEconomy })
            cb.SelectedIndexChanged += (_, _) => { if (chkDefaultTech.Checked) OnDefaultTechChanged(); };
        cbFormat.SelectedIndexChanged += (_, _) => RefreshSymmetryChoices((string)cbSymmetry.SelectedItem);
    }

    private static ComboBox Combo(params string[] items)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        cb.Items.AddRange(items);
        cb.SelectedIndex = 0;
        return cb;
    }

    private static ComboBox EnumCombo<T>() where T : struct, Enum => Combo(Enum.GetNames<T>());

    /// <summary>A 0..100 (or custom range) slider; the value shows as a tooltip-free label via the tick marks.</summary>
    private TrackBar Slider(int min = 0, int max = 100)
    {
        var tb = new TrackBar { Minimum = min, Maximum = max, TickFrequency = (max - min) / 10, Height = 30, LargeChange = 10, SmallChange = 5 };
        var tip = new ToolTip();
        tb.ValueChanged += (_, _) => tip.SetToolTip(tb, tb.Value.ToString());
        return tb;
    }

    /// <summary>Only offer symmetries that give a fair layout for the selected format.</summary>
    private void RefreshSymmetryChoices(string keep)
    {
        var allowed = GeneratorSettings.AllowedSymmetries((MatchFormat)cbFormat.SelectedIndex).Select(k => k.ToString()).ToList();
        cbSymmetry.Items.Clear();
        cbSymmetry.Items.Add("Auto");
        foreach (var k in allowed)
            cbSymmetry.Items.Add(k);
        cbSymmetry.SelectedItem = keep != null && allowed.Contains(keep) ? keep : "Auto";
    }

    private DataGridView BuildTechGrid()
    {
        var grid = new DataGridView
        {
            Height = 170,
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersWidth = 24,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
        };

        var type = new DataGridViewComboBoxColumn { HeaderText = "Type", Name = "Type", FillWeight = 120 };
        type.Items.AddRange(TechTypes);
        var placement = new DataGridViewComboBoxColumn { HeaderText = "Placement", Name = "Placement", FillWeight = 110 };
        placement.Items.AddRange(Enum.GetNames<TechPlacement>());
        grid.Columns.Add(type);
        grid.Columns.Add(placement);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Count", Name = "Count", FillWeight = 50 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Min", Name = "Min", FillWeight = 40 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Max", Name = "Max", FillWeight = 40 });
        grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "Delete", Text = "✕", UseColumnTextForButtonValue = true, FillWeight = 22, ToolTipText = "Remove this rule" });
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["Delete"].Index && !grid.Rows[e.RowIndex].IsNewRow)
                grid.Rows.RemoveAt(e.RowIndex);
        };
        grid.DefaultValuesNeeded += (_, e) =>
        {
            e.Row.Cells["Type"].Value = "oil";
            e.Row.Cells["Placement"].Value = nameof(TechPlacement.PerPlayer);
            e.Row.Cells["Count"].Value = "1";
            e.Row.Cells["Min"].Value = "8";
            e.Row.Cells["Max"].Value = "16";
        };
        grid.DataError += (_, e) => e.ThrowException = false;
        return grid;
    }

    // ---------------------------------------------------------------- settings <-> controls

    private void LoadFromConfig()
    {
        var s = config.Settings ?? new GeneratorSettings();
        cbFormat.SelectedIndex = (int)s.Format;
        cbStyle.SelectedItem = s.Style.ToString();
        cbSize.SelectedItem = s.Size.ToString();
        cbShape.SelectedItem = s.Shape.ToString();
        cbLayout.SelectedItem = s.Layout.ToString();
        cbTheater.SelectedIndex = Math.Max(0, Array.IndexOf(Theaters, (s.Theater ?? "TEMPERATE").ToUpperInvariant()));
        tbHills.Value = Percent(s.Hills);
        tbCliffs.Value = Percent(s.Cliffs);
        tbWater.Value = Percent(s.Water);
        tbGarrisons.Value = Percent(s.Garrisons);
        tbOreSize.Value = Math.Clamp((int)Math.Round(s.OreFieldSize * 100), tbOreSize.Minimum, tbOreSize.Maximum);
        tbGemSize.Value = Math.Clamp((int)Math.Round(s.GemFieldSize * 100), tbGemSize.Minimum, tbGemSize.Maximum);
        tbDrills.Value = Percent(s.Drills);
        chkTrails.Checked = s.Trails;
        chkWalls.Checked = s.WallsAroundTech;
        cbResources.SelectedItem = s.Resources.ToString();
        cbGems.SelectedItem = s.Gems.ToString();
        cbEconomy.SelectedItem = s.Economy.ToString();
        RefreshSymmetryChoices(s.Symmetry?.ToString());
        cbTime.SelectedItem = s.Time.ToString();
        tbTrees.Value = Percent(s.Trees);
        nudSeed.Value = Math.Clamp(s.Seed, 0, (int)nudSeed.Maximum);
        chkRandomSeed.Checked = config.RandomSeed;
        txtGameDir.Text = config.GameDirectory ?? "";
        UpdateGameDirHint();

        FillTechGrid(s.Tech ?? new GeneratorSettings { Format = s.Format, Style = s.Style, Economy = s.Economy }.EffectiveTech());
        chkDefaultTech.Checked = config.DefaultTech || s.Tech == null;
        OnDefaultTechChanged();
    }

    private GeneratorSettings ReadSettings()
    {
        var s = new GeneratorSettings
        {
            Format = (MatchFormat)cbFormat.SelectedIndex,
            Style = Enum.Parse<MapStyle>((string)cbStyle.SelectedItem),
            Size = Enum.Parse<MapSize>((string)cbSize.SelectedItem),
            Theater = Theaters[cbTheater.SelectedIndex],
            Shape = Enum.Parse<MapShape>((string)cbShape.SelectedItem),
            Layout = Enum.Parse<MapLayout>((string)cbLayout.SelectedItem),
            Hills = tbHills.Value / 100.0,
            Cliffs = tbCliffs.Value / 100.0,
            Water = tbWater.Value / 100.0,
            Garrisons = tbGarrisons.Value / 100.0,
            OreFieldSize = tbOreSize.Value / 100.0,
            GemFieldSize = tbGemSize.Value / 100.0,
            Drills = tbDrills.Value / 100.0,
            Trails = chkTrails.Checked,
            WallsAroundTech = chkWalls.Checked,
            Resources = Enum.Parse<ResourceLevel>((string)cbResources.SelectedItem),
            Gems = Enum.Parse<GemPlacement>((string)cbGems.SelectedItem),
            Economy = Enum.Parse<EconomyMode>((string)cbEconomy.SelectedItem),
            Symmetry = (string)cbSymmetry.SelectedItem == "Auto" ? null : Enum.Parse<SymmetryKind>((string)cbSymmetry.SelectedItem),
            Time = Enum.Parse<TimeOfDay>((string)cbTime.SelectedItem),
            Trees = tbTrees.Value / 100.0,
            Seed = (int)nudSeed.Value,
        };
        if (!chkDefaultTech.Checked)
            s.Tech = ReadTechGrid();
        return s;
    }

    private void ReadIntoConfig()
    {
        config.Settings = ReadSettings();
        config.Settings.Tech = ReadTechGrid();
        config.RandomSeed = chkRandomSeed.Checked;
        config.DefaultTech = chkDefaultTech.Checked;
        config.GameDirectory = txtGameDir.Text.Trim();
    }

    private static int Percent(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 100);

    private void FillTechGrid(IEnumerable<TechRule> rules)
    {
        gridTech.Rows.Clear();
        foreach (var r in rules)
        {
            string type = TechTypes.Contains(r.Type, StringComparer.OrdinalIgnoreCase) ? r.Type.ToLowerInvariant() : "oil";
            gridTech.Rows.Add(type, r.Placement.ToString(), r.Count.ToString(), r.MinDistance.ToString(), r.MaxDistance.ToString());
        }
    }

    private List<TechRule> ReadTechGrid()
    {
        var list = new List<TechRule>();
        foreach (DataGridViewRow row in gridTech.Rows)
        {
            if (row.IsNewRow || row.Cells["Type"].Value is not string type)
                continue;
            int Int(string col, int def) => int.TryParse(row.Cells[col].Value?.ToString(), out int v) ? v : def;
            list.Add(new TechRule
            {
                Type = type,
                Placement = Enum.TryParse<TechPlacement>(row.Cells["Placement"].Value?.ToString(), out var p) ? p : TechPlacement.PerPlayer,
                Count = Math.Max(1, Int("Count", 1)),
                MinDistance = Int("Min", 8),
                MaxDistance = Int("Max", 16),
            });
        }
        return list;
    }

    private void OnDefaultTechChanged()
    {
        if (chkDefaultTech.Checked)
        {
            var s = ReadSettings();
            s.Tech = null;
            FillTechGrid(s.EffectiveTech());
        }
        gridTech.Enabled = !chkDefaultTech.Checked;
        gridTech.DefaultCellStyle.ForeColor = chkDefaultTech.Checked ? Color.Gray : SystemColors.ControlText;
    }

    private void UpdateGameDirHint()
    {
        bool valid = PreviewRenderer.IsGameDirectory(txtGameDir.Text.Trim());
        lblGameDirHint.Text = valid
            ? "Previews are rendered with your game's art."
            : string.IsNullOrWhiteSpace(txtGameDir.Text)
                ? "Set your Yuri's Revenge (CnCNet) folder to see map previews and copy maps to the client."
                : "No Yuri's Revenge game files found in this folder.";
        lblGameDirHint.ForeColor = valid ? Color.DimGray : Color.Firebrick;
    }

    private void BrowseGameDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select your Yuri's Revenge (CnCNet) directory" };
        if (PreviewRenderer.IsGameDirectory(txtGameDir.Text))
            dialog.SelectedPath = txtGameDir.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            txtGameDir.Text = dialog.SelectedPath;
    }

    // ---------------------------------------------------------------- actions

    private async Task GenerateAsync()
    {
        if (chkRandomSeed.Checked)
            nudSeed.Value = Random.Shared.Next(1, 1_000_000);

        var settings = ReadSettings();
        if (chkDefaultTech.Checked)
            OnDefaultTechChanged();
        ReadIntoConfig();
        config.Save();

        SetBusy(true, "Generating...");
        try
        {
            string gameDir = txtGameDir.Text.Trim();
            var (result, mapPath, previewError) = await Task.Run(() =>
            {
                var (data, connected) = LoadTheater(settings.Theater);
                var generated = new MapGenerator(data, connected).Generate(settings);

                Directory.CreateDirectory(workDir);
                string path = Path.Combine(workDir, $"gen_{settings.FormatLabel}_{settings.Theater.ToLowerInvariant()}_{generated.Seed}.map");
                string error;
                try
                {
                    error = PreviewRenderer.Use(gameDir, r => MapWriter.Write(generated.Map, data, path, r));
                }
                catch (Exception ex)
                {
                    MapWriter.Write(generated.Map, data, path);
                    error = "LightningMap could not load the game files, so the map has a radar-style preview: " + ex.Message;
                }
                return (generated, path, error);
            });
            lastMapPath = mapPath;
            lastResult = result;

            txtReport.Text = $"{result.Map.Name}   {result.Map.Width}x{result.Map.Height}   seed {result.Seed}   " +
                             $"{result.Attempts} attempt(s)\r\n{lastMapPath}\r\n\r\n" + result.Report.ToText().Replace("\n", "\r\n");

            SetBusy(true, "Rendering preview...");
            string png = Path.ChangeExtension(lastMapPath, ".preview.png");
            // Twice the view's width so zooming in stays sharp.
            int width = Math.Clamp(picture.Width * 2, 1600, 4000);
            string error = await PreviewRenderer.RenderAsync(lastMapPath, png, gameDir, width, markers: true);
            ShowPreview(error == null ? png : null);
            SetBusy(false, error ?? previewError ?? $"Generated {result.Map.Name}");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Generation failed: " + ex.Message);
        }
    }

    private void ShowPreview(string png)
    {
        var old = picture.Image;
        picture.Image = null;
        old?.Dispose();
        if (png == null || !File.Exists(png))
            return;
        // Load through a copy so the file is not locked for the next render.
        using var stream = new MemoryStream(File.ReadAllBytes(png));
        picture.Image = Image.FromStream(stream);
    }

    private void SaveMapAs()
    {
        if (lastMapPath == null)
            return;
        using var dialog = new SaveFileDialog
        {
            Filter = "YR map (*.map)|*.map|All files|*.*",
            FileName = Path.GetFileName(lastMapPath),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        File.Copy(lastMapPath, dialog.FileName, overwrite: true);
        lblStatus.Text = "Saved " + dialog.FileName;
    }

    /// <summary>
    /// Copies the map into the client's Maps\Custom folder. The lobby preview is the one embedded in the
    /// map; a same-named .png would take its place, so one left by an earlier copy is removed.
    /// </summary>
    private void CopyToClient()
    {
        string gameDir = txtGameDir.Text.Trim();
        if (lastMapPath == null || !PreviewRenderer.IsGameDirectory(gameDir))
        {
            lblStatus.Text = "Set the Yuri's Revenge directory first.";
            return;
        }

        string customDir = Path.Combine(gameDir, "Maps", "Custom");
        Directory.CreateDirectory(customDir);
        string target = Path.Combine(customDir, Path.GetFileName(lastMapPath));
        File.Copy(lastMapPath, target, overwrite: true);
        File.Delete(Path.ChangeExtension(target, ".png"));
        lblStatus.Text = $"Copied to {target} (pick it from the custom maps list in the client)";
    }

    /// <summary>
    /// Randomises everything except the format, style and economy, within ranges that make playable
    /// maps, with random tech to match.
    /// </summary>
    private void RandomiseSettings()
    {
        var r = Random.Shared;
        T Pick<T>(params T[] items) => items[r.Next(items.Length)];
        void Choose(ComboBox cb, string item) => cb.SelectedItem = item;

        Choose(cbSize, Pick("Small", "Medium", "Medium", "Large"));
        Choose(cbShape, nameof(MapShape.Random));
        // Mostly land maps; island layouts are naval games, so rarer.
        Choose(cbLayout, Pick("Land", "Land", "Land", "Land", "Continent", "TeamIslands", "PlayerIslands", "Archipelago", "Mountainous"));
        RefreshSymmetryChoices("Auto");
        Choose(cbSymmetry, r.NextDouble() < 0.5 ? "Auto" : (string)cbSymmetry.Items[r.Next(cbSymmetry.Items.Count)]);
        cbTheater.SelectedIndex = r.Next(Theaters.Length - 1); // not Lunar (no cliffs or water)
        Choose(cbTime, Pick("Day", "Day", "Dusk", "Night"));
        tbHills.Value = r.Next(10, 90);
        tbCliffs.Value = r.Next(15, 95);
        tbWater.Value = r.Next(0, 80);
        tbTrees.Value = r.Next(20, 85);
        tbGarrisons.Value = r.Next(0, 100);
        chkTrails.Checked = r.NextDouble() < 0.8;
        chkWalls.Checked = r.NextDouble() < 0.7;
        Choose(cbResources, Pick("Low", "Medium", "Medium", "High", "VeryHigh"));
        tbOreSize.Value = r.Next(70, 170);
        Choose(cbGems, Pick("None", "PerPlayer", "PerPlayer", "Contested", "Both"));
        tbGemSize.Value = r.Next(70, 170);
        tbDrills.Value = r.Next(60, 101);
        chkDefaultTech.Checked = false;
        FillTechGrid(RandomTech(r));
        OnDefaultTechChanged();
        chkRandomSeed.Checked = true;
    }

    /// <summary>A random but sensible set of tech rules: oil for the economy setting, plus a few extras.</summary>
    private List<TechRule> RandomTech(Random r)
    {
        var economy = (EconomyMode)cbEconomy.SelectedIndex;
        bool blitz = (MapStyle)cbStyle.SelectedIndex == MapStyle.Blitz;
        var list = new List<TechRule>();

        int ownOil = economy switch
        {
            EconomyMode.OilOnly => r.Next(2, 5),
            EconomyMode.Mixed => r.Next(1, 4),
            _ => r.Next(0, 3),
        };
        if (ownOil > 0)
            list.Add(economy == EconomyMode.Ore
                ? new TechRule { Type = "oil", Placement = TechPlacement.PerPlayer, Count = ownOil, MinDistance = 9, MaxDistance = 16 }
                : new TechRule { Type = "oil", Placement = TechPlacement.PerPlayer, Count = ownOil, MinDistance = 6, MaxDistance = 20 });

        int sharedOil = r.Next(0, 3);
        if (sharedOil > 0)
            list.Add(new TechRule { Type = "oil", Placement = TechPlacement.Contested, Count = sharedOil });

        // Up to two other buildings (fewer on Blitz maps); only one can stand in the centre.
        var extras = new[] { "repair", "hospital", "airport", "machineshop", "power", "seclab" }.OrderBy(_ => r.Next()).Take(r.Next(blitz ? 0 : 1, blitz ? 2 : 3));
        bool centreTaken = false;
        bool teams = (MatchFormat)cbFormat.SelectedIndex != MatchFormat.Duel;
        foreach (var type in extras)
        {
            TechPlacement placement;
            if (!centreTaken && r.NextDouble() < 0.5)
            {
                placement = TechPlacement.Centre;
                centreTaken = true;
            }
            else
            {
                placement = type == "repair" && teams ? TechPlacement.PerTeam : TechPlacement.Contested;
            }
            list.Add(new TechRule { Type = type, Placement = placement, Count = 1 });
        }
        return list;
    }

    private void SetBusy(bool busy, string status)
    {
        btnGenerate.Enabled = !busy;
        btnRandom.Enabled = !busy;
        bool haveMap = !busy && lastMapPath != null;
        btnSave.Enabled = haveMap;
        btnCopyToClient.Enabled = haveMap;
        btnOpenFolder.Enabled = haveMap;
        lblStatus.Text = status;
        UseWaitCursor = busy;
    }

    private (TheaterData, List<ConnectedTileType>) LoadTheater(string theater)
    {
        lock (theaterCache)
        {
            if (!theaterCache.TryGetValue(theater, out var entry))
            {
                var data = TheaterData.Load(dataDir, theater);
                var connected = ConnectedTileType.Load(Path.Combine(dataDir, "ConnectedTileDrawer.ini"), data);
                entry = (data, connected);
                theaterCache[theater] = entry;
            }
            return entry;
        }
    }

    private static string FindDataDirectory()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "data");
            if (File.Exists(Path.Combine(candidate, "temperate.json")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("The data folder (theater tile data) was not found next to the generator.");
    }
}
