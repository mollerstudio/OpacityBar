using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Opacbar
{
    public class SettingsForm : Form
    {
        private AppConfig _config;
        private TaskbarEngine _engine;
        private bool _suppressEvents = false;

        // Header & Status
        private Panel _headerPanel;
        private Label _brandTitle;
        private Label _brandSubtitle;
        private Panel _statusBadge;
        private Color _statusColor = BrandTheme.AccentSuccess;
        private string _statusText = "ACTIVE · ZERO TELEMETRY";
        private Button _closeBtn;

        // Live Visualizer
        private LiveTaskbarVisualizer _visualizer;

        // Navigation Tabs
        private OpacSegmentedSelector _navTabs;
        private Panel _pageAppearance;
        private Panel _pagePillGeometry;
        private Panel _pageDynamicRules;
        private Panel _pageSystemTrust;

        // Page 1: Appearance Controls
        private OpacSwitch _masterEnableSwitch;
        private OpacSegmentedSelector _modeSelector;
        private OpacSlider _opacitySlider;
        private Label _opacityValueLabel;
        private OpacSlider _luminanceSlider;
        private Label _luminanceValueLabel;
        private OpacSwitch _showTopLineSwitch;

        // Page 2: Floating Pill & Corner Controls
        private OpacSwitch _pillEnableSwitch;
        private OpacSegmentedSelector _cornerPresetSelector;
        private OpacSlider _cornerRadiusSlider;
        private Label _cornerRadiusValueLabel;
        private OpacSlider _marginXSlider;
        private Label _marginXValueLabel;
        private OpacSlider _marginYSlider;
        private Label _marginYValueLabel;
        private OpacSwitch _dockWhenMaxSwitch;

        // Page 3: Dynamic Rules Controls
        private OpacSegmentedSelector _ruleTargetSelector;
        private OpacSwitch _ruleEnableSwitch;
        private Label _ruleEnableTitle;
        private OpacSegmentedSelector _ruleModeSelector;
        private OpacSlider _ruleOpacitySlider;
        private Label _ruleOpacityVal;
        private OpacSlider _ruleLuminanceSlider;
        private Label _ruleLuminanceVal;

        // Page 4: System & Transparency Guarantee
        private OpacSwitch _startupSwitch;
        private OpacSwitch _uiLightThemeSwitch;
        private Button _resetDefaultsBtn;
        private Button _openConfigBtn;

        public event EventHandler ConfigChanged;

        public SettingsForm(AppConfig config, TaskbarEngine engine)
        {
            _config = config;
            _engine = engine;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(430, 620);
            Text = "Opacbar";
            DoubleBuffered = true;
            ShowInTaskbar = false;

            BuildUI();
            SyncControlsFromConfig();
            ApplyTheme();
            PositionNearTray();

            _engine.StateUpdated += (state, count) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    UpdateStatusBanner();
                    _visualizer.UpdatePreview(_config, state);
                });
            };
        }

        public void PositionNearTray()
        {
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            int x = workArea.Right - Width - 16;
            int y = workArea.Bottom - Height - 16;
            Location = new Point(Math.Max(16, x), Math.Max(16, y));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowRounding();
        }

        private void ApplyWindowRounding()
        {
            int cornerPref = 2; // DWMWCP_ROUND
            int res = NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            if (res != 0)
            {
                IntPtr rgn = NativeMethods.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 20, 20);
                NativeMethods.SetWindowRgn(Handle, rgn, true);
            }
        }

        private void BuildUI()
        {
            // Header
            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = Color.Transparent
            };
            _headerPanel.MouseDown += OnHeaderMouseDown;

            _brandTitle = new Label
            {
                Text = "OPACBAR",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Location = new Point(20, 14),
                AutoSize = true
            };
            _brandTitle.MouseDown += OnHeaderMouseDown;

            _brandSubtitle = new Label
            {
                Text = "Monochrome Taskbar Studio · v1.0",
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
                Location = new Point(21, 38),
                AutoSize = true
            };
            _brandSubtitle.MouseDown += OnHeaderMouseDown;

            _statusBadge = new Panel
            {
                Size = new Size(165, 24),
                Location = new Point(215, 18),
                BackColor = Color.Transparent
            };
            _statusBadge.Paint += OnPaintStatusBadge;

            _closeBtn = new Button
            {
                Text = "✕",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                Size = new Size(30, 30),
                Location = new Point(Width - 44, 15),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            _closeBtn.FlatAppearance.BorderSize = 0;
            _closeBtn.Click += (s, e) => Hide();

            _headerPanel.Controls.Add(_brandTitle);
            _headerPanel.Controls.Add(_brandSubtitle);
            _headerPanel.Controls.Add(_statusBadge);
            _headerPanel.Controls.Add(_closeBtn);
            Controls.Add(_headerPanel);

            // Live Visualizer
            _visualizer = new LiveTaskbarVisualizer(_config)
            {
                Location = new Point(20, 70),
                Size = new Size(Width - 40, 96)
            };
            Controls.Add(_visualizer);

            // Navigation Segmented Bar
            _navTabs = new OpacSegmentedSelector
            {
                Location = new Point(20, 178),
                Size = new Size(Width - 40, 36),
                Items = new string[] { "Appearance", "Floating Pill", "Dynamic", "System" },
                SelectedIndex = 0
            };
            _navTabs.SelectedIndexChanged += (s, e) => SwitchTab(_navTabs.SelectedIndex);
            Controls.Add(_navTabs);

            int pageTop = 226;
            int pageHeight = Height - pageTop - 16;
            Size pageSize = new Size(Width - 40, pageHeight);

            _pageAppearance = CreatePagePanel(pageTop, pageSize);
            _pagePillGeometry = CreatePagePanel(pageTop, pageSize);
            _pageDynamicRules = CreatePagePanel(pageTop, pageSize);
            _pageSystemTrust = CreatePagePanel(pageTop, pageSize);

            BuildPageAppearance();
            BuildPagePillGeometry();
            BuildPageDynamicRules();
            BuildPageSystemTrust();

            Controls.Add(_pageAppearance);
            Controls.Add(_pagePillGeometry);
            Controls.Add(_pageDynamicRules);
            Controls.Add(_pageSystemTrust);

            SwitchTab(0);
        }

        private Panel CreatePagePanel(int top, Size size)
        {
            return new Panel
            {
                Location = new Point(20, top),
                Size = size,
                BackColor = Color.Transparent,
                Visible = false
            };
        }

        private void BuildPageAppearance()
        {
            int y = 4;

            // Card 1: Master Engine Switch
            Panel masterCard = CreateCard(0, y, 56);
            Label masterTitle = CreateLabel("Enable Opacbar Styling", 14, 10, 9.5f, FontStyle.Bold);
            Label masterSub = CreateMutedLabel("Apply real-time transparency and monochrome tint", 14, 30);
            _masterEnableSwitch = new OpacSwitch
            {
                Location = new Point(masterCard.Width - 58, 16),
                UseSemanticGreen = true
            };
            _masterEnableSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.Enabled = _masterEnableSwitch.Checked;
                NotifyConfigUpdated();
            };
            masterCard.Controls.Add(masterTitle);
            masterCard.Controls.Add(masterSub);
            masterCard.Controls.Add(_masterEnableSwitch);
            _pageAppearance.Controls.Add(masterCard);
            y += 66;

            // Card 2: Taskbar Material Mode
            Panel modeCard = CreateCard(0, y, 86);
            Label modeTitle = CreateLabel("Taskbar Material Mode", 14, 10, 9f, FontStyle.Bold);
            _modeSelector = new OpacSegmentedSelector
            {
                Location = new Point(14, 38),
                Size = new Size(modeCard.Width - 28, 34),
                Items = new string[] { "Clear", "Acrylic", "Blur", "Tinted", "Solid", "Normal" }
            };
            _modeSelector.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.DesktopProfile.Mode = (TaskbarStyleMode)_modeSelector.SelectedIndex;
                NotifyConfigUpdated();
            };
            modeCard.Controls.Add(modeTitle);
            modeCard.Controls.Add(_modeSelector);
            _pageAppearance.Controls.Add(modeCard);
            y += 96;

            // Card 3: Monochrome Opacity & Luminance Sliders
            Panel slidersCard = CreateCard(0, y, 146);
            Label opTitle = CreateLabel("Tint Opacity", 14, 12, 9f, FontStyle.Bold);
            _opacityValueLabel = CreateValueLabel("25%", slidersCard.Width - 65, 12);
            _opacitySlider = new OpacSlider
            {
                Location = new Point(10, 36),
                Size = new Size(slidersCard.Width - 20, 26),
                Minimum = 0,
                Maximum = 100
            };
            _opacitySlider.ValueChanged += (s, e) =>
            {
                _opacityValueLabel.Text = _opacitySlider.Value + "%";
                if (_suppressEvents) return;
                _config.DesktopProfile.Opacity = _opacitySlider.Value;
                NotifyConfigUpdated();
            };

            Label lumTitle = CreateLabel("Monochrome Shade (Black ↔ White)", 14, 76, 9f, FontStyle.Bold);
            _luminanceValueLabel = CreateValueLabel("Pure Black", slidersCard.Width - 110, 76);
            _luminanceSlider = new OpacSlider
            {
                Location = new Point(10, 100),
                Size = new Size(slidersCard.Width - 20, 26),
                Minimum = 0,
                Maximum = 255
            };
            _luminanceSlider.ValueChanged += (s, e) =>
            {
                _luminanceValueLabel.Text = DescribeLuminance(_luminanceSlider.Value);
                if (_suppressEvents) return;
                _config.DesktopProfile.Luminance = _luminanceSlider.Value;
                NotifyConfigUpdated();
            };

            slidersCard.Controls.Add(opTitle);
            slidersCard.Controls.Add(_opacityValueLabel);
            slidersCard.Controls.Add(_opacitySlider);
            slidersCard.Controls.Add(lumTitle);
            slidersCard.Controls.Add(_luminanceValueLabel);
            slidersCard.Controls.Add(_luminanceSlider);
            _pageAppearance.Controls.Add(slidersCard);
            y += 156;

            // Card 4: Taskbar Top Border Line
            Panel borderCard = CreateCard(0, y, 52);
            Label borderTitle = CreateLabel("Show Top Hairline Border", 14, 16, 9f, FontStyle.Regular);
            _showTopLineSwitch = new OpacSwitch
            {
                Location = new Point(borderCard.Width - 58, 14)
            };
            _showTopLineSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.DesktopProfile.ShowTopLine = _showTopLineSwitch.Checked;
                NotifyConfigUpdated();
            };
            borderCard.Controls.Add(borderTitle);
            borderCard.Controls.Add(_showTopLineSwitch);
            _pageAppearance.Controls.Add(borderCard);
        }

        private void BuildPagePillGeometry()
        {
            int y = 4;

            // Card 1: Floating Pill Master Toggle
            Panel pillCard = CreateCard(0, y, 56);
            Label pillTitle = CreateLabel("Floating Pill Taskbar", 14, 10, 9.5f, FontStyle.Bold);
            Label pillSub = CreateMutedLabel("Detach taskbar from screen edges with custom corners", 14, 30);
            _pillEnableSwitch = new OpacSwitch
            {
                Location = new Point(pillCard.Width - 58, 16),
                UseSemanticGreen = true
            };
            _pillEnableSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.FloatingPillEnabled = _pillEnableSwitch.Checked;
                NotifyConfigUpdated();
            };
            pillCard.Controls.Add(pillTitle);
            pillCard.Controls.Add(pillSub);
            pillCard.Controls.Add(_pillEnableSwitch);
            _pagePillGeometry.Controls.Add(pillCard);
            y += 66;

            // Card 2: Quick Corner Shape Presets + Exact Radius Slider
            Panel cornerCard = CreateCard(0, y, 134);
            Label cornerTitle = CreateLabel("Corner Shape & Radius", 14, 10, 9f, FontStyle.Bold);
            _cornerRadiusValueLabel = CreateValueLabel("16 px", cornerCard.Width - 70, 10);

            _cornerPresetSelector = new OpacSegmentedSelector
            {
                Location = new Point(14, 34),
                Size = new Size(cornerCard.Width - 28, 32),
                Items = new string[] { "Square (0)", "Soft (8)", "Rounded (16)", "Full Pill (26)" }
            };
            _cornerPresetSelector.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                int[] radii = new int[] { 0, 8, 16, 26 };
                int r = radii[_cornerPresetSelector.SelectedIndex];
                _cornerRadiusSlider.Value = r;
            };

            Label exactLabel = CreateMutedLabel("Fine-Tune Corner Curvature (0px Square – 32px Pill)", 14, 74);
            _cornerRadiusSlider = new OpacSlider
            {
                Location = new Point(10, 94),
                Size = new Size(cornerCard.Width - 20, 26),
                Minimum = 0,
                Maximum = 32
            };
            _cornerRadiusSlider.ValueChanged += (s, e) =>
            {
                _cornerRadiusValueLabel.Text = _cornerRadiusSlider.Value + " px";
                if (_suppressEvents) return;
                _config.CornerRadius = _cornerRadiusSlider.Value;
                UpdateCornerPresetHighlight(_config.CornerRadius);
                NotifyConfigUpdated();
            };

            cornerCard.Controls.Add(cornerTitle);
            cornerCard.Controls.Add(_cornerRadiusValueLabel);
            cornerCard.Controls.Add(_cornerPresetSelector);
            cornerCard.Controls.Add(exactLabel);
            cornerCard.Controls.Add(_cornerRadiusSlider);
            _pagePillGeometry.Controls.Add(cornerCard);
            y += 144;

            // Card 3: Floating Margins (Horizontal Dock Width & Vertical Elevation)
            Panel marginCard = CreateCard(0, y, 118);
            Label mxTitle = CreateLabel("Horizontal Edge Inset (Pill Width)", 14, 10, 8.75f, FontStyle.Bold);
            _marginXValueLabel = CreateValueLabel("14 px", marginCard.Width - 75, 10);
            _marginXSlider = new OpacSlider
            {
                Location = new Point(10, 30),
                Size = new Size(marginCard.Width - 20, 24),
                Minimum = 0,
                Maximum = 260
            };
            _marginXSlider.ValueChanged += (s, e) =>
            {
                _marginXValueLabel.Text = _marginXSlider.Value + " px";
                if (_suppressEvents) return;
                _config.HorizontalMargin = _marginXSlider.Value;
                NotifyConfigUpdated();
            };

            Label myTitle = CreateLabel("Vertical Floating Gap", 14, 62, 8.75f, FontStyle.Bold);
            _marginYValueLabel = CreateValueLabel("4 px", marginCard.Width - 75, 62);
            _marginYSlider = new OpacSlider
            {
                Location = new Point(10, 82),
                Size = new Size(marginCard.Width - 20, 24),
                Minimum = 0,
                Maximum = 12
            };
            _marginYSlider.ValueChanged += (s, e) =>
            {
                _marginYValueLabel.Text = _marginYSlider.Value + " px";
                if (_suppressEvents) return;
                _config.VerticalMargin = _marginYSlider.Value;
                NotifyConfigUpdated();
            };

            marginCard.Controls.Add(mxTitle);
            marginCard.Controls.Add(_marginXValueLabel);
            marginCard.Controls.Add(_marginXSlider);
            marginCard.Controls.Add(myTitle);
            marginCard.Controls.Add(_marginYValueLabel);
            marginCard.Controls.Add(_marginYSlider);
            _pagePillGeometry.Controls.Add(marginCard);
            y += 126;

            // Card 4: Auto-Expand Pill when Window Maximized
            Panel maxDockCard = CreateCard(0, y, 42);
            Label maxDockLabel = CreateLabel("Un-float to Full Width When Window Maximized", 14, 11, 8.5f, FontStyle.Regular);
            _dockWhenMaxSwitch = new OpacSwitch
            {
                Location = new Point(maxDockCard.Width - 58, 9)
            };
            _dockWhenMaxSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.DockedWhenMaximized = _dockWhenMaxSwitch.Checked;
                NotifyConfigUpdated();
            };
            maxDockCard.Controls.Add(maxDockLabel);
            maxDockCard.Controls.Add(_dockWhenMaxSwitch);
            _pagePillGeometry.Controls.Add(maxDockCard);
        }

        private void BuildPageDynamicRules()
        {
            int y = 4;

            Label intro = CreateMutedLabel("Automatically switch taskbar appearance based on desktop activity:", 2, y);
            _pageDynamicRules.Controls.Add(intro);
            y += 24;

            _ruleTargetSelector = new OpacSegmentedSelector
            {
                Location = new Point(0, y),
                Size = new Size(_pageDynamicRules.Width, 34),
                Items = new string[] { "Maximized App", "Start / Search", "Battery Saver" },
                SelectedIndex = 0
            };
            _ruleTargetSelector.SelectedIndexChanged += (s, e) => SyncDynamicRuleEditor();
            _pageDynamicRules.Controls.Add(_ruleTargetSelector);
            y += 44;

            // Rule Enable Card
            Panel enableCard = CreateCard(0, y, 54);
            _ruleEnableTitle = CreateLabel("Enable Maximized Window Override", 14, 17, 9f, FontStyle.Bold);
            _ruleEnableSwitch = new OpacSwitch
            {
                Location = new Point(enableCard.Width - 58, 15),
                UseSemanticGreen = true
            };
            _ruleEnableSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                SaveCurrentRuleFromEditor();
                NotifyConfigUpdated();
            };
            enableCard.Controls.Add(_ruleEnableTitle);
            enableCard.Controls.Add(_ruleEnableSwitch);
            _pageDynamicRules.Controls.Add(enableCard);
            y += 64;

            // Rule Style Editor Card
            Panel editorCard = CreateCard(0, y, 212);
            Label modeLbl = CreateLabel("Override Material Mode", 14, 12, 9f, FontStyle.Bold);
            _ruleModeSelector = new OpacSegmentedSelector
            {
                Location = new Point(14, 36),
                Size = new Size(editorCard.Width - 28, 32),
                Items = new string[] { "Clear", "Acrylic", "Blur", "Tinted", "Solid", "Normal" }
            };
            _ruleModeSelector.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                SaveCurrentRuleFromEditor();
                NotifyConfigUpdated();
            };

            Label opLbl = CreateLabel("Override Opacity", 14, 82, 8.75f, FontStyle.Bold);
            _ruleOpacityVal = CreateValueLabel("75%", editorCard.Width - 65, 82);
            _ruleOpacitySlider = new OpacSlider
            {
                Location = new Point(10, 104),
                Size = new Size(editorCard.Width - 20, 26),
                Minimum = 0,
                Maximum = 100
            };
            _ruleOpacitySlider.ValueChanged += (s, e) =>
            {
                _ruleOpacityVal.Text = _ruleOpacitySlider.Value + "%";
                if (_suppressEvents) return;
                SaveCurrentRuleFromEditor();
                NotifyConfigUpdated();
            };

            Label lumLbl = CreateLabel("Override Monochrome Shade", 14, 142, 8.75f, FontStyle.Bold);
            _ruleLuminanceVal = CreateValueLabel("Pure Black", editorCard.Width - 110, 142);
            _ruleLuminanceSlider = new OpacSlider
            {
                Location = new Point(10, 164),
                Size = new Size(editorCard.Width - 20, 26),
                Minimum = 0,
                Maximum = 255
            };
            _ruleLuminanceSlider.ValueChanged += (s, e) =>
            {
                _ruleLuminanceVal.Text = DescribeLuminance(_ruleLuminanceSlider.Value);
                if (_suppressEvents) return;
                SaveCurrentRuleFromEditor();
                NotifyConfigUpdated();
            };

            editorCard.Controls.Add(modeLbl);
            editorCard.Controls.Add(_ruleModeSelector);
            editorCard.Controls.Add(opLbl);
            editorCard.Controls.Add(_ruleOpacityVal);
            editorCard.Controls.Add(_ruleOpacitySlider);
            editorCard.Controls.Add(lumLbl);
            editorCard.Controls.Add(_ruleLuminanceVal);
            editorCard.Controls.Add(_ruleLuminanceSlider);
            _pageDynamicRules.Controls.Add(editorCard);
        }

        private void BuildPageSystemTrust()
        {
            int y = 4;

            // Card 1: Startup & UI Theme
            Panel prefCard = CreateCard(0, y, 104);
            Label startLbl = CreateLabel("Launch Opacbar at Windows Sign-In", 14, 15, 9f, FontStyle.Bold);
            _startupSwitch = new OpacSwitch
            {
                Location = new Point(prefCard.Width - 58, 13),
                UseSemanticGreen = true
            };
            _startupSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.LaunchAtStartup = _startupSwitch.Checked;
                NotifyConfigUpdated();
            };

            Label themeLbl = CreateLabel("Light Monochrome Control Panel", 14, 61, 9f, FontStyle.Bold);
            _uiLightThemeSwitch = new OpacSwitch
            {
                Location = new Point(prefCard.Width - 58, 59)
            };
            _uiLightThemeSwitch.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _config.UILightTheme = _uiLightThemeSwitch.Checked;
                ApplyTheme();
                NotifyConfigUpdated();
            };

            prefCard.Controls.Add(startLbl);
            prefCard.Controls.Add(_startupSwitch);
            prefCard.Controls.Add(themeLbl);
            prefCard.Controls.Add(_uiLightThemeSwitch);
            _pageSystemTrust.Controls.Add(prefCard);
            y += 114;

            // Card 2: Open-Source Zero-Permission Security Guarantee
            Panel trustCard = CreateCard(0, y, 178);
            Label trustHeader = CreateLabel("PRIVACY & PERMISSION AUDIT", 14, 12, 9f, FontStyle.Bold);
            Label trustBadge = new Label
            {
                Text = "● VERIFIED SAFE",
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                ForeColor = BrandTheme.AccentSuccess,
                Location = new Point(trustCard.Width - 115, 13),
                AutoSize = true
            };

            string auditBody =
                "✓  Zero Network Access: No sockets, HTTP clients, or telemetry.\r\n" +
                "✓  Standard User Privilege: Runs with `asInvoker` (no UAC elevation).\r\n" +
                "✓  No Process Injection: Uses only standard Win32/DWM window APIs.\r\n" +
                "✓  100% Local Config: Plain JSON stored locally on your machine.\r\n" +
                "✓  Instant Clean Exit: Restores default Windows taskbar on quit.";

            Label trustText = new Label
            {
                Text = auditBody,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
                Location = new Point(14, 38),
                Size = new Size(trustCard.Width - 28, 128)
            };

            trustCard.Controls.Add(trustHeader);
            trustCard.Controls.Add(trustBadge);
            trustCard.Controls.Add(trustText);
            _pageSystemTrust.Controls.Add(trustCard);
            y += 188;

            // Action Buttons: Open JSON Config & Reset Defaults
            _openConfigBtn = new Button
            {
                Text = "View Local JSON",
                Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Regular),
                Size = new Size((_pageSystemTrust.Width - 12) / 2, 36),
                Location = new Point(0, y),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _openConfigBtn.Click += (s, e) =>
            {
                string path = AppConfig.GetConfigPath();
                string err;
                _config.Save(out err);
                try { Process.Start("notepad.exe", path); } catch { }
            };

            _resetDefaultsBtn = new Button
            {
                Text = "Reset to Defaults",
                Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Regular),
                Size = new Size((_pageSystemTrust.Width - 12) / 2, 36),
                Location = new Point((_pageSystemTrust.Width + 12) / 2, y),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _resetDefaultsBtn.Click += (s, e) =>
            {
                AppConfig fresh = new AppConfig();
                _config.Enabled = fresh.Enabled;
                _config.FloatingPillEnabled = fresh.FloatingPillEnabled;
                _config.CornerRadius = fresh.CornerRadius;
                _config.HorizontalMargin = fresh.HorizontalMargin;
                _config.VerticalMargin = fresh.VerticalMargin;
                _config.DockedWhenMaximized = fresh.DockedWhenMaximized;
                _config.DesktopProfile = fresh.DesktopProfile;
                _config.EnableMaximizedState = fresh.EnableMaximizedState;
                _config.MaximizedProfile = fresh.MaximizedProfile;
                _config.EnableStartMenuState = fresh.EnableStartMenuState;
                _config.StartMenuProfile = fresh.StartMenuProfile;
                _config.EnableBatterySaverState = fresh.EnableBatterySaverState;
                _config.BatterySaverProfile = fresh.BatterySaverProfile;
                SyncControlsFromConfig();
                NotifyConfigUpdated();
            };

            _pageSystemTrust.Controls.Add(_openConfigBtn);
            _pageSystemTrust.Controls.Add(_resetDefaultsBtn);
        }

        private Panel CreateCard(int x, int y, int height)
        {
            Panel p = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(_pageAppearance.Width, height),
                Tag = "Card"
            };
            p.Paint += OnPaintCard;
            return p;
        }

        private Label CreateLabel(string text, int x, int y, float size, FontStyle style)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", size, style),
                Location = new Point(x, y),
                AutoSize = true,
                Tag = "PrimaryLabel"
            };
        }

        private Label CreateMutedLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
                Location = new Point(x, y),
                AutoSize = true,
                Tag = "MutedLabel"
            };
        }

        private Label CreateValueLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Location = new Point(x, y),
                Size = new Size(96, 18),
                TextAlign = ContentAlignment.TopRight,
                Tag = "SecondaryLabel"
            };
        }

        private void SwitchTab(int index)
        {
            _pageAppearance.Visible = (index == 0);
            _pagePillGeometry.Visible = (index == 1);
            _pageDynamicRules.Visible = (index == 2);
            _pageSystemTrust.Visible = (index == 3);
        }

        public void SyncControlsFromConfig()
        {
            _suppressEvents = true;

            _masterEnableSwitch.Checked = _config.Enabled;
            _modeSelector.SelectedIndex = (int)_config.DesktopProfile.Mode;
            _opacitySlider.Value = _config.DesktopProfile.Opacity;
            _opacityValueLabel.Text = _config.DesktopProfile.Opacity + "%";
            _luminanceSlider.Value = _config.DesktopProfile.Luminance;
            _luminanceValueLabel.Text = DescribeLuminance(_config.DesktopProfile.Luminance);
            _showTopLineSwitch.Checked = _config.DesktopProfile.ShowTopLine;

            _pillEnableSwitch.Checked = _config.FloatingPillEnabled;
            _cornerRadiusSlider.Value = _config.CornerRadius;
            _cornerRadiusValueLabel.Text = _config.CornerRadius + " px";
            UpdateCornerPresetHighlight(_config.CornerRadius);
            _marginXSlider.Value = _config.HorizontalMargin;
            _marginXValueLabel.Text = _config.HorizontalMargin + " px";
            _marginYSlider.Value = _config.VerticalMargin;
            _marginYValueLabel.Text = _config.VerticalMargin + " px";
            _dockWhenMaxSwitch.Checked = _config.DockedWhenMaximized;

            _startupSwitch.Checked = _config.LaunchAtStartup;
            _uiLightThemeSwitch.Checked = _config.UILightTheme;

            SyncDynamicRuleEditor();
            UpdateStatusBanner();
            _visualizer.UpdatePreview(_config, _engine.CurrentState);

            _suppressEvents = false;
        }

        private void UpdateCornerPresetHighlight(int radius)
        {
            if (radius == 0) _cornerPresetSelector.SelectedIndex = 0;
            else if (radius <= 10) _cornerPresetSelector.SelectedIndex = 1;
            else if (radius <= 20) _cornerPresetSelector.SelectedIndex = 2;
            else _cornerPresetSelector.SelectedIndex = 3;
        }

        private void SyncDynamicRuleEditor()
        {
            bool prev = _suppressEvents;
            _suppressEvents = true;

            int target = _ruleTargetSelector.SelectedIndex;
            StyleProfile prof;
            bool enabled;

            if (target == 0)
            {
                _ruleEnableTitle.Text = "Enable Maximized Window Override";
                enabled = _config.EnableMaximizedState;
                prof = _config.MaximizedProfile;
            }
            else if (target == 1)
            {
                _ruleEnableTitle.Text = "Enable Start / Search Open Override";
                enabled = _config.EnableStartMenuState;
                prof = _config.StartMenuProfile;
            }
            else
            {
                _ruleEnableTitle.Text = "Enable Battery Saver Override";
                enabled = _config.EnableBatterySaverState;
                prof = _config.BatterySaverProfile;
            }

            _ruleEnableSwitch.Checked = enabled;
            _ruleModeSelector.SelectedIndex = (int)prof.Mode;
            _ruleOpacitySlider.Value = prof.Opacity;
            _ruleOpacityVal.Text = prof.Opacity + "%";
            _ruleLuminanceSlider.Value = prof.Luminance;
            _ruleLuminanceVal.Text = DescribeLuminance(prof.Luminance);

            _suppressEvents = prev;
        }

        private void SaveCurrentRuleFromEditor()
        {
            int target = _ruleTargetSelector.SelectedIndex;
            StyleProfile prof = new StyleProfile
            {
                Mode = (TaskbarStyleMode)_ruleModeSelector.SelectedIndex,
                Opacity = _ruleOpacitySlider.Value,
                Luminance = _ruleLuminanceSlider.Value,
                ShowTopLine = _config.DesktopProfile.ShowTopLine
            };

            if (target == 0)
            {
                _config.EnableMaximizedState = _ruleEnableSwitch.Checked;
                _config.MaximizedProfile = prof;
            }
            else if (target == 1)
            {
                _config.EnableStartMenuState = _ruleEnableSwitch.Checked;
                _config.StartMenuProfile = prof;
            }
            else
            {
                _config.EnableBatterySaverState = _ruleEnableSwitch.Checked;
                _config.BatterySaverProfile = prof;
            }
        }

        private void NotifyConfigUpdated()
        {
            string err;
            bool saved = _config.Save(out err);
            _engine.UpdateConfig(_config);
            UpdateStatusBanner(saved ? null : err);
            _visualizer.UpdatePreview(_config, _engine.CurrentState);
            if (ConfigChanged != null) ConfigChanged(this, EventArgs.Empty);
        }

        private void UpdateStatusBanner(string saveError = null)
        {
            if (!string.IsNullOrEmpty(saveError))
            {
                _statusColor = BrandTheme.AccentError;
                _statusText = "CONFIG SAVE ERROR";
            }
            else if (!_config.Enabled)
            {
                _statusColor = BrandTheme.AccentWarning;
                _statusText = "PAUSED · DEFAULT BAR";
            }
            else if (_engine.ActiveTaskbarCount == 0)
            {
                _statusColor = BrandTheme.AccentError;
                _statusText = "NO TASKBAR DETECTED";
            }
            else
            {
                _statusColor = BrandTheme.AccentSuccess;
                switch (_engine.CurrentState)
                {
                    case DynamicActivityState.MaximizedWindow:
                        _statusText = "ACTIVE · MAXIMIZED";
                        break;
                    case DynamicActivityState.StartOrSearchOpen:
                        _statusText = "ACTIVE · START OPEN";
                        break;
                    case DynamicActivityState.BatterySaver:
                        _statusText = "ACTIVE · POWER SAVE";
                        break;
                    default:
                        _statusText = _config.FloatingPillEnabled ? "ACTIVE · PILL MODE" : "ACTIVE · DESKTOP";
                        break;
                }
            }
            _statusBadge.Invalidate();
        }

        private static string DescribeLuminance(int lum)
        {
            if (lum <= 15) return "Pure Black (" + lum + ")";
            if (lum <= 70) return "Charcoal (" + lum + ")";
            if (lum <= 170) return "Neutral Gray (" + lum + ")";
            if (lum <= 235) return "Soft White (" + lum + ")";
            return "Pure White (" + lum + ")";
        }

        public void ApplyTheme()
        {
            bool light = _config.UILightTheme;
            BackColor = BrandTheme.GetBgPrimary(light);

            _brandTitle.ForeColor = BrandTheme.GetTextPrimary(light);
            _brandSubtitle.ForeColor = BrandTheme.GetTextSecondary(light);
            _closeBtn.ForeColor = BrandTheme.GetTextSecondary(light);
            _closeBtn.BackColor = Color.Transparent;

            _navTabs.LightTheme = light;
            _modeSelector.LightTheme = light;
            _cornerPresetSelector.LightTheme = light;
            _ruleTargetSelector.LightTheme = light;
            _ruleModeSelector.LightTheme = light;

            _masterEnableSwitch.LightTheme = light;
            _showTopLineSwitch.LightTheme = light;
            _pillEnableSwitch.LightTheme = light;
            _dockWhenMaxSwitch.LightTheme = light;
            _ruleEnableSwitch.LightTheme = light;
            _startupSwitch.LightTheme = light;
            _uiLightThemeSwitch.LightTheme = light;

            _opacitySlider.LightTheme = light;
            _luminanceSlider.LightTheme = light;
            _cornerRadiusSlider.LightTheme = light;
            _marginXSlider.LightTheme = light;
            _marginYSlider.LightTheme = light;
            _ruleOpacitySlider.LightTheme = light;
            _ruleLuminanceSlider.LightTheme = light;

            StyleButton(_openConfigBtn, light, false);
            StyleButton(_resetDefaultsBtn, light, true);

            ApplyThemeToContainer(_pageAppearance, light);
            ApplyThemeToContainer(_pagePillGeometry, light);
            ApplyThemeToContainer(_pageDynamicRules, light);
            ApplyThemeToContainer(_pageSystemTrust, light);

            _visualizer.Invalidate();
            Invalidate();
        }

        private void StyleButton(Button btn, bool light, bool isWarningHover)
        {
            btn.BackColor = BrandTheme.GetBgCard(light);
            btn.ForeColor = BrandTheme.GetTextPrimary(light);
            btn.FlatAppearance.BorderColor = BrandTheme.GetBorder(light);
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.MouseOverBackColor = BrandTheme.GetBgElevated(light);
        }

        private void ApplyThemeToContainer(Control parent, bool light)
        {
            foreach (Control c in parent.Controls)
            {
                if (c.Tag as string == "Card")
                {
                    c.BackColor = BrandTheme.GetBgCard(light);
                    c.Invalidate();
                }
                else if (c.Tag as string == "PrimaryLabel")
                {
                    c.ForeColor = BrandTheme.GetTextPrimary(light);
                }
                else if (c.Tag as string == "SecondaryLabel")
                {
                    c.ForeColor = BrandTheme.GetTextSecondary(light);
                }
                else if (c.Tag as string == "MutedLabel")
                {
                    c.ForeColor = BrandTheme.GetTextMuted(light);
                }
                else if (c is Label && c.ForeColor != BrandTheme.AccentSuccess)
                {
                    c.ForeColor = BrandTheme.GetTextSecondary(light);
                }

                if (c.HasChildren)
                {
                    ApplyThemeToContainer(c, light);
                }
            }
        }

        private void OnPaintCard(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;
            if (p == null) return;
            bool light = _config.UILightTheme;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            using (GraphicsPath path = BrandTheme.CreateRoundedRect(r, 8))
            using (Pen pen = new Pen(BrandTheme.GetBorder(light), 1f))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        private void OnPaintStatusBadge(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool light = _config.UILightTheme;
            Rectangle r = new Rectangle(0, 0, _statusBadge.Width - 1, _statusBadge.Height - 1);

            using (GraphicsPath path = BrandTheme.CreateRoundedRect(r, 11))
            using (SolidBrush bg = new SolidBrush(BrandTheme.GetBgCard(light)))
            using (Pen border = new Pen(BrandTheme.GetBorder(light), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            // Semantic status dot (Green = Success/Active, Yellow = Warning/Paused, Red = Error)
            using (SolidBrush dot = new SolidBrush(_statusColor))
            {
                g.FillEllipse(dot, 10, 7, 9, 9);
            }

            using (Font f = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold))
            {
                Rectangle textRect = new Rectangle(24, 0, _statusBadge.Width - 28, _statusBadge.Height);
                TextRenderer.DrawText(
                    g,
                    _statusText,
                    f,
                    textRect,
                    BrandTheme.GetTextPrimary(light),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool light = _config.UILightTheme;
            using (Pen outerBorder = new Pen(BrandTheme.GetBorder(light), 1f))
            {
                e.Graphics.DrawRectangle(outerBorder, 0, 0, Width - 1, Height - 1);
            }
        }

        private void OnHeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HT_CAPTION), IntPtr.Zero);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
