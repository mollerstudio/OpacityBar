using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Opacbar
{
    /// <summary>
    /// The Opacbar flyout: a small, borderless window anchored above the tray.
    /// One scale factor (window DPI / 96) drives every size and font.
    /// </summary>
    public class SettingsForm : Form
    {
        private const int WM_DPICHANGED = 0x02E0;
        private const int CS_DROPSHADOW = 0x00020000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly Material[] Materials = new Material[]
        {
            Material.Clear, Material.Color, Material.Blur, Material.Acrylic, Material.Windows
        };
        private static int _lastTab;

        private readonly AppConfig _config;
        private readonly TaskbarEngine _engine;
        private readonly Timer _saveTimer = new Timer();
        private readonly Timer _statusTimer = new Timer();

        private bool _syncing;
        private bool _dragging;
        private bool _suppressHide;

        // Layout
        private StackPanel _root;
        private HeaderBar _header;
        private TextBlock _note;
        private FlatButton _restartBtn;
        private SegmentedControl _tabs;
        private StackPanel _pageLook, _pageGeneral;
        private int _restartTick = int.MinValue;
        private bool _pickerOpen;

        // Look
        private SegmentedControl _situation, _style;
        private Row _useDifferent, _topLine;
        private ColorSwatchRow _colorRow;
        private ColorPickerPanel _picker;
        private LabeledSlider _opacity;
        private TextBlock _hint;

        // General
        private Row _startup, _themeRow;
        private SegmentedControl _themeSeg;
        private TextBlock _mapNote;
        public event EventHandler ConfigChanged;
        public event EventHandler QuitRequested;

        /// <summary>Environment.TickCount at the moment the flyout last hid itself.</summary>
        public int LastHideTick { get; private set; }

        public SettingsForm(AppConfig config, TaskbarEngine engine)
        {
            _config = config;
            _engine = engine;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            Text = "Opacbar";
            BackColor = Theme.Surface;

            _saveTimer.Interval = 300;
            _saveTimer.Tick += delegate { FlushSave(); };
            _statusTimer.Interval = 1000;
            _statusTimer.Tick += delegate { RefreshStatus(); };

            BuildUi();

            if (_engine != null) _engine.StateUpdated += OnEngineStateUpdated;
            Theme.SetDpi(UiNative.SystemDpi());
            SyncControlsFromConfig();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        // ---- UI construction --------------------------------------------------------

        private void BuildUi()
        {
            _root = new StackPanel { Pad = Theme.Space4, Gap = Theme.Space3 };

            _header = new HeaderBar();
            _header.DragRequested += delegate { BeginDrag(); };
            _header.Toggle.CheckedChanged += delegate
            {
                if (_syncing) return;
                _config.Enabled = _header.Toggle.Checked;
                Commit();
            };

            _note = new TextBlock("") { Visible = false };

            _restartBtn = new FlatButton("Restart Explorer") { Visible = false };
            _restartBtn.Click += delegate
            {
                if (_engine == null) return;
                _restartTick = Environment.TickCount;
                _restartBtn.Enabled = false;
                _engine.RestartExplorer();
            };

            _tabs = new SegmentedControl("Look", "General");
            _tabs.SelectedIndex = Math.Max(0, Math.Min(1, _lastTab));
            _tabs.SelectedIndexChanged += delegate
            {
                _lastTab = _tabs.SelectedIndex;
                ShowPage();
                Relayout();
            };

            _pageLook = BuildLookPage();
            _pageGeneral = BuildGeneralPage();

            _root.Controls.Add(_header);
            _root.Controls.Add(_note);
            _root.Controls.Add(_restartBtn);
            _root.Controls.Add(_tabs);
            _root.Controls.Add(_pageLook);
            _root.Controls.Add(_pageGeneral);
            Controls.Add(_root);
            ShowPage();
        }

        private static StackPanel NewPage()
        {
            return new StackPanel { Gap = Theme.Space1, Pad = 0 };
        }

        private StackPanel BuildLookPage()
        {
            StackPanel p = NewPage();

            _situation = new SegmentedControl("Desktop", "Maximized", "Start", "Battery");
            _situation.SelectedIndex = 0;
            _situation.SelectedIndexChanged += delegate
            {
                SyncControlsFromConfig();
            };

            ToggleSwitch useTog = new ToggleSwitch();
            _useDifferent = new Row("Use a different look", useTog);
            useTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                SetSituationEnabled(_situation.SelectedIndex, useTog.Checked);
                UpdateEnabledStates();
                Commit();
            };

            _style = new SegmentedControl("Clear", "Color", "Blur", "Acrylic", "Windows");
            _style.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                int i = _style.SelectedIndex;
                if (i < 0 || i >= Materials.Length) return;
                StyleProfile cp = CurrentProfile();
                cp.Material = Materials[i];
                if (IsTinted(cp.Material) && cp.Opacity < 10)
                {
                    // Coming from Clear: start at a visible opacity instead of an invisible one.
                    cp.Opacity = 60;
                    _syncing = true;
                    _opacity.Value = cp.Opacity;
                    _syncing = false;
                }
                UpdateEnabledStates();
                Commit();
            };

            _colorRow = new ColorSwatchRow("Color");
            _colorRow.Clicked += delegate
            {
                _pickerOpen = !_pickerOpen;
                UpdateEnabledStates();
            };

            _picker = new ColorPickerPanel();
            _picker.Recents = _config.RecentColors;
            _picker.ColorChanged += delegate
            {
                if (_syncing) return;
                Color c = _picker.Value;
                CurrentProfile().Color = ColorMath.Hex(c);
                _colorRow.Value = c;
                CommitLight(); // live apply while dragging
            };
            _picker.ColorCommitted += delegate
            {
                if (_syncing) return;
                AddRecentColor(ColorMath.Hex(_picker.Value));
                _picker.Invalidate();
                Commit();
            };

            _opacity = new LabeledSlider("Opacity", 0, 100);
            _opacity.Format = delegate(int v) { return v + "%"; };
            _opacity.ValueChanged += delegate
            {
                if (_syncing) return;
                CurrentProfile().Opacity = _opacity.Value;
                CommitLight();
            };

            _hint = new TextBlock("Needs Windows transparency effects on") { Visible = false };

            ToggleSwitch lineTog = new ToggleSwitch();
            _topLine = new Row("Hide top line", lineTog);
            lineTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                CurrentProfile().HideTopLine = lineTog.Checked;
                Commit();
            };

            p.Controls.Add(new SectionHeader("Situation"));
            p.Controls.Add(_situation);
            p.Controls.Add(_useDifferent);
            p.Controls.Add(new SectionHeader("Style"));
            p.Controls.Add(_style);
            p.Controls.Add(_colorRow);
            p.Controls.Add(_picker);
            p.Controls.Add(_opacity);
            p.Controls.Add(_hint);
            p.Controls.Add(_topLine);
            return p;
        }

        private static bool IsTinted(Material m)
        {
            return m == Material.Color || m == Material.Blur || m == Material.Acrylic;
        }

        private void AddRecentColor(string hex)
        {
            string n = TaskbarStyle.NormalizeHex(hex);
            if (n == null) return;
            if (_config.RecentColors == null) _config.RecentColors = new System.Collections.Generic.List<string>();
            _config.RecentColors.RemoveAll(delegate(string s) { return string.Equals(s, n, StringComparison.OrdinalIgnoreCase); });
            _config.RecentColors.Insert(0, n);
            while (_config.RecentColors.Count > AppConfig.MaxRecentColors)
                _config.RecentColors.RemoveAt(_config.RecentColors.Count - 1);
            _picker.Recents = _config.RecentColors;
        }

        /// <summary>True unless Windows transparency effects are switched off.</summary>
        private static bool TransparencyEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("EnableTransparency");
                        if (v is int) return (int)v != 0;
                    }
                }
            }
            catch { }
            return true;
        }
        private StackPanel BuildGeneralPage()
        {
            StackPanel p = NewPage();
            p.Gap = Theme.Space2;

            ToggleSwitch startTog = new ToggleSwitch();
            _startup = new Row("Start with Windows", startTog);
            startTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                _config.LaunchAtStartup = startTog.Checked;
                Commit();
            };

            _themeSeg = new SegmentedControl("System", "Dark", "Light");
            _themeSeg.LogicalWidth = 200;
            _themeRow = new Row("Theme", _themeSeg);
            _themeSeg.SelectedIndexChanged += delegate
            {
                if (_syncing || _themeSeg.SelectedIndex < 0) return;
                _config.UiTheme = _themeSeg.SelectedIndex;
                Theme.Apply(_config.UiTheme);
                OnThemeChanged();
                Commit();
            };

            FlatButton open = new FlatButton("Open settings file");
            open.Click += delegate { OpenSettingsFile(); };

            FlatButton reset = new FlatButton("Reset to defaults");
            reset.Click += delegate { ResetDefaults(); };

            FlatButton quit = new FlatButton("Quit and restore taskbar");
            quit.Danger = true;
            quit.Click += delegate
            {
                FlushSave();
                EventHandler h = QuitRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            TextBlock footer = new TextBlock("Open source · no network · no admin\nEmergency quit: Ctrl+Shift+Alt+Q");
            footer.Small = true;
            footer.Center = true;

            FlatButton map = new FlatButton("Save taskbar map...");
            map.Click += delegate { SaveTaskbarMap(); };
            _mapNote = new TextBlock("") { Visible = false };

            p.Controls.Add(_startup);
            p.Controls.Add(_themeRow);
            p.Controls.Add(new SectionHeader("Settings"));
            p.Controls.Add(open);
            p.Controls.Add(reset);
            p.Controls.Add(new SectionHeader("Developer"));
            p.Controls.Add(map);
            p.Controls.Add(_mapNote);
            p.Controls.Add(quit);
            p.Controls.Add(footer);
            return p;
        }
        private void ShowPage()
        {
            _pageLook.Visible = _tabs.SelectedIndex == 0;
            _pageGeneral.Visible = _tabs.SelectedIndex == 1;
        }
        // ---- Config <-> controls ----------------------------------------------------

        private StyleProfile CurrentProfile()
        {
            switch (_situation.SelectedIndex)
            {
                case 1: return _config.MaximizedProfile;
                case 2: return _config.StartMenuProfile;
                case 3: return _config.BatterySaverProfile;
                default: return _config.DesktopProfile;
            }
        }

        private bool GetSituationEnabled(int idx)
        {
            switch (idx)
            {
                case 1: return _config.EnableMaximizedState;
                case 2: return _config.EnableStartMenuState;
                case 3: return _config.EnableBatterySaverState;
                default: return true;
            }
        }

        private void SetSituationEnabled(int idx, bool v)
        {
            switch (idx)
            {
                case 1: _config.EnableMaximizedState = v; break;
                case 2: _config.EnableStartMenuState = v; break;
                case 3: _config.EnableBatterySaverState = v; break;
            }
        }

        /// <summary>Pushes the current config into every control (no events leak back).</summary>
        public void SyncControlsFromConfig()
        {
            bool was = _syncing;
            _syncing = true;
            try
            {
                _header.Toggle.Checked = _config.Enabled;

                int idx = Math.Max(0, _situation.SelectedIndex);
                StyleProfile p = CurrentProfile();
                ((ToggleSwitch)_useDifferent.Trailing).Checked = GetSituationEnabled(idx);
                _style.SelectedIndex = Array.IndexOf(Materials, p.Material);
                _opacity.Value = p.Opacity;
                Color pc = ColorMath.Parse(p.Color, Color.Black);
                _picker.Recents = _config.RecentColors;
                _picker.Value = pc;
                _colorRow.Value = pc;
                ((ToggleSwitch)_topLine.Trailing).Checked = p.HideTopLine;
                ((ToggleSwitch)_startup.Trailing).Checked = _config.LaunchAtStartup;
                _themeSeg.SelectedIndex = _config.UiTheme;
            }
            finally
            {
                _syncing = was;
            }
            UpdateEnabledStates();
            RefreshStatus();
            Relayout();
        }

        private void UpdateEnabledStates()
        {
            int idx = Math.Max(0, _situation.SelectedIndex);
            _useDifferent.Visible = idx > 0;
            bool look = idx == 0 || GetSituationEnabled(idx);
            Material m = CurrentProfile().Material;
            bool tinted = IsTinted(m);

            _style.Enabled = look;
            _colorRow.Visible = tinted;
            _colorRow.Enabled = look;
            _colorRow.Expanded = _pickerOpen;
            _picker.Visible = tinted && look && _pickerOpen;
            _opacity.Visible = tinted;
            _opacity.Enabled = look;
            _topLine.Enabled = look && m != Material.Windows;

            bool blurry = m == Material.Blur || m == Material.Acrylic;
            _hint.Visible = blurry;
            if (blurry)
            {
                bool off = !TransparencyEnabled();
                _hint.Dot = off ? (Color?)Theme.Warning : null;
                _hint.TextValue = off ? "Needs Windows transparency effects on. They are off right now." : "Needs Windows transparency effects on";
            }
            if (IsHandleCreated) Relayout();
        }

        private void Commit()
        {
            if (_syncing) return;
            if (_engine != null) _engine.UpdateConfig(_config);
            _saveTimer.Stop();
            _saveTimer.Start();
            RefreshStatus();
            EventHandler h = ConfigChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>Cheap commit for continuous input (dragging): updates the taskbar and schedules a save only.</summary>
        private void CommitLight()
        {
            if (_syncing) return;
            if (_engine != null) _engine.UpdateConfig(_config);
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        private void FlushSave()
        {
            _saveTimer.Stop();
            string err;
            _config.Save(out err);
        }

        // ---- Status -----------------------------------------------------------------

        private void OnEngineStateUpdated(DynamicActivityState state, int count)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(new Action(RefreshStatus));
            else RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (IsDisposed || _engine == null) return;

            string text;
            Color dot = Theme.Success;
            string note = null;
            Color noteColor = Theme.Warning;

            EngineHealth health = _engine.Health;
            if (!_config.Enabled)
            {
                text = "Off";
                dot = Theme.TextDisabled;
            }
            else if (health == EngineHealth.Paused)
            {
                text = "Paused";
                dot = Theme.TextSecondary;
            }
            else if (health == EngineHealth.NoTaskbar)
            {
                text = "No taskbar found";
                dot = Theme.Warning;
                note = _engine.HealthDetail;
            }
            else if (health == EngineHealth.HelperMissing)
            {
                text = "Helper missing";
                dot = Theme.Warning;
                note = _engine.HealthDetail;
            }
            else if (health == EngineHealth.HelperOutdated)
            {
                text = "Helper update pending";
                dot = Theme.Warning;
                note = _engine.HealthDetail;
            }
            else if (health == EngineHealth.HelperFailed)
            {
                text = "Helper failed";
                dot = Theme.Error;
                noteColor = Theme.Error;
                note = _engine.HealthDetail;
            }
            else
            {
                switch (_engine.CurrentState)
                {
                    case DynamicActivityState.MaximizedWindow: text = "Active · window maximized"; break;
                    case DynamicActivityState.StartOrSearchOpen: text = "Active · Start or Search open"; break;
                    case DynamicActivityState.BatterySaver: text = "Active · battery saver"; break;
                    case DynamicActivityState.Disabled: text = "Off"; dot = Theme.TextDisabled; break;
                    default: text = "Active"; break;
                }
                int n = _engine.ActiveTaskbarCount;
                if (n > 1 && dot == Theme.Success) text += " · " + n + " taskbars";
            }

            _header.SetStatus(text, dot);

            bool showRestart = _config.Enabled && health == EngineHealth.HelperOutdated;
            bool restartChanged = _restartBtn.WantVisible != showRestart;
            _restartBtn.Visible = showRestart;
            _restartBtn.Enabled = unchecked(Environment.TickCount - _restartTick) > 8000;

            bool showNote = !string.IsNullOrEmpty(note);
            bool changed = _note.WantVisible != showNote || (showNote && _note.TextValue != note);
            _note.Dot = noteColor;
            if (showNote) _note.TextValue = note;
            _note.Visible = showNote;
            if ((changed || restartChanged) && IsHandleCreated) Relayout();
        }

        // ---- Layout / sizing --------------------------------------------------------

        /// <summary>Recomputes content height and resizes the window, keeping its bottom edge fixed.</summary>
        private void Relayout()
        {

            int w = Theme.Px(Theme.FlyoutWidth);
            int inner = w - 2;
            int h = _root.GetPreferredHeight(inner) + 2;
            h = Math.Min(h, MaxHeight());

            if (Visible)
            {
                int bottom = Bottom;
                int right = Right;
                SetBounds(right - w, bottom - h, w, h);
            }
            else
            {
                Size = new Size(w, h);
            }
            _root.SetBounds(1, 1, w - 2, h - 2);
            _root.PerformLayout();
            Invalidate(true);

        }

        private int MaxHeight()
        {
            int max = Theme.Px(Theme.FlyoutMaxHeight);
            try
            {
                Rectangle wa = Screen.FromPoint(Visible ? new Point(Left, Top) : Cursor.Position).WorkingArea;
                max = Math.Min(max, wa.Height - 2 * Theme.Px(Theme.FlyoutEdgeGap));
            }
            catch { }
            return Math.Max(Theme.Px(200), max);
        }

        private void PlaceAboveTray(int dpi)
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int gap = Theme.Px(Theme.FlyoutEdgeGap);
            int w = Theme.Px(Theme.FlyoutWidth);
            int h = Math.Min(_root.GetPreferredHeight(w - 2) + 2, MaxHeight());
            SetBounds(wa.Right - w - gap, wa.Bottom - h - gap, w, h);
            _root.SetBounds(1, 1, w - 2, h - 2);
            _root.PerformLayout();
        }

        public void ShowFlyout()
        {
            Point cur = Cursor.Position;
            int dpi = UiNative.DpiForPoint(cur);
            Theme.SetDpi(dpi);
            Theme.Apply(_config.UiTheme);
            BackColor = Theme.Surface;
            SyncControlsFromConfig();
            PlaceAboveTray(dpi);

            _suppressHide = false;
            Show();
            int actual = UiNative.WindowDpi(Handle);
            if (actual != dpi)
            {
                Theme.SetDpi(actual);
                Relayout();
                PlaceAboveTray(actual);
            }
            Activate();
            UiNative.SetForegroundWindow(Handle);
            _statusTimer.Start();
        }

        public void HideFlyout()
        {
            if (!Visible) return;
            _statusTimer.Stop();
            LastHideTick = Environment.TickCount;
            Hide();
            if (_saveTimer.Enabled) FlushSave();
        }

        public void OnThemeChanged()
        {
            BackColor = Theme.Surface;
            if (IsHandleCreated) Theme.SetBorderColor(Handle, Theme.Border);
            Invalidate(true);
        }

        // ---- Window behavior --------------------------------------------------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundCorners(Handle);
            Theme.SetBorderColor(Handle, Theme.Border);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible && !_dragging && !_suppressHide) HideFlyout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                HideFlyout();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                if (dpi >= 48)
                {
                    Theme.SetDpi(dpi);
                    Relayout();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private void BeginDrag()
        {
            _dragging = true;
            try
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HT_CAPTION, IntPtr.Zero);
            }
            finally
            {
                _dragging = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_engine != null) _engine.StateUpdated -= OnEngineStateUpdated;
                _saveTimer.Dispose();
                _statusTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- General actions --------------------------------------------------------

        private void OpenSettingsFile()
        {
            FlushSave();
            string path = AppConfig.GetConfigPath();
            _suppressHide = true;
            try
            {
                try { Process.Start(path); }
                catch { Process.Start("notepad.exe", "\"" + path + "\""); }
            }
            catch { }
            _suppressHide = false;
            HideFlyout();
        }

        private void SaveTaskbarMap()
        {
            if (_engine == null) return;
            string path, err;
            bool ok = _engine.SaveTaskbarMap(out path, out err);
            _mapNote.Dot = ok ? Theme.Success : Theme.Error;
            _mapNote.TextValue = ok ? "Saved to " + path : err;
            _mapNote.Visible = true;
            Relayout();
            if (!ok) return;
            _suppressHide = true;
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch { }
            _suppressHide = false;
        }

        private void ResetDefaults()
        {
            _suppressHide = true;
            DialogResult r;
            try
            {
                r = MessageBox.Show(this, "Reset all settings to their defaults?", "Opacbar",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            }
            finally
            {
                _suppressHide = false;
            }
            if (r == DialogResult.Yes)
            {
                _config.CopyFrom(new AppConfig());
                Theme.Apply(_config.UiTheme);
                OnThemeChanged();
                SyncControlsFromConfig();
                Commit();
            }
            if (Visible)
            {
                Activate();
                UiNative.SetForegroundWindow(Handle);
            }
        }
    }
}






