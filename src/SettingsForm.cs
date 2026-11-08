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

        private static readonly TaskbarStyleMode[] StyleModes = new TaskbarStyleMode[]
        {
            TaskbarStyleMode.Clear, TaskbarStyleMode.Tinted, TaskbarStyleMode.Blur,
            TaskbarStyleMode.Acrylic, TaskbarStyleMode.Solid, TaskbarStyleMode.Normal
        };

        private static readonly int[] CornerPresets = new int[] { 0, 8, 16, 24 };

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
        private SegmentedControl _tabs;
        private StackPanel _pageLook, _pageShape, _pageGeneral;

        // Look
        private SegmentedControl _situation, _style;
        private Row _useDifferent, _topLine;
        private LabeledSlider _shade, _opacity;

        // Shape
        private Row _floating, _docked;
        private SegmentedControl _corners;
        private LabeledSlider _radius, _sideGap, _bottomGap;

        // General
        private Row _startup, _themeRow;
        private SegmentedControl _themeSeg;

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

            _tabs = new SegmentedControl("Look", "Shape", "General");
            _tabs.SelectedIndex = _lastTab;
            _tabs.SelectedIndexChanged += delegate
            {
                _lastTab = _tabs.SelectedIndex;
                ShowPage();
                Relayout();
            };

            _pageLook = BuildLookPage();
            _pageShape = BuildShapePage();
            _pageGeneral = BuildGeneralPage();

            _root.Controls.Add(_header);
            _root.Controls.Add(_note);
            _root.Controls.Add(_tabs);
            _root.Controls.Add(_pageLook);
            _root.Controls.Add(_pageShape);
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

            _style = new SegmentedControl("Clear", "Tint", "Blur", "Acrylic", "Solid", "Windows");
            _style.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                int i = _style.SelectedIndex;
                if (i >= 0 && i < StyleModes.Length) CurrentProfile().Mode = StyleModes[i];
                UpdateEnabledStates();
                Commit();
            };

            _shade = new LabeledSlider("Shade", 0, 255);
            _shade.Hint = "Black to white";
            _shade.Gradient = true;
            _shade.Format = delegate(int v) { return Math.Round(v * 100.0 / 255.0) + "%"; };
            _shade.ValueChanged += delegate
            {
                if (_syncing) return;
                CurrentProfile().Luminance = _shade.Value;
                Commit();
            };

            _opacity = new LabeledSlider("Opacity", 0, 100);
            _opacity.Format = delegate(int v) { return v + "%"; };
            _opacity.ValueChanged += delegate
            {
                if (_syncing) return;
                CurrentProfile().Opacity = _opacity.Value;
                Commit();
            };

            ToggleSwitch lineTog = new ToggleSwitch();
            _topLine = new Row("Hide top line", lineTog);
            lineTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                CurrentProfile().ShowTopLine = !lineTog.Checked;
                Commit();
            };

            p.Controls.Add(new SectionHeader("Situation"));
            p.Controls.Add(_situation);
            p.Controls.Add(_useDifferent);
            p.Controls.Add(new SectionHeader("Style"));
            p.Controls.Add(_style);
            p.Controls.Add(_shade);
            p.Controls.Add(_opacity);
            p.Controls.Add(_topLine);
            return p;
        }

        private StackPanel BuildShapePage()
        {
            StackPanel p = NewPage();

            ToggleSwitch floatTog = new ToggleSwitch();
            _floating = new Row("Floating", floatTog);
            _floating.Description = "Lift the taskbar off the screen edge";
            floatTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                _config.FloatingPillEnabled = floatTog.Checked;
                UpdateEnabledStates();
                Commit();
            };

            _corners = new SegmentedControl("Square", "Soft", "Round", "Pill");
            _corners.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                int i = _corners.SelectedIndex;
                if (i < 0 || i >= CornerPresets.Length) return;
                _config.CornerRadius = CornerPresets[i];
                _syncing = true;
                _radius.Value = _config.CornerRadius;
                _syncing = false;
                Commit();
            };

            _radius = new LabeledSlider("Corner radius", 0, 24);
            _radius.Format = delegate(int v) { return v + " px"; };
            _radius.ValueChanged += delegate
            {
                if (_syncing) return;
                _config.CornerRadius = _radius.Value;
                _syncing = true;
                _corners.SelectedIndex = Array.IndexOf(CornerPresets, _config.CornerRadius);
                _syncing = false;
                Commit();
            };

            _sideGap = new LabeledSlider("Side gap", 0, 64);
            _sideGap.Format = delegate(int v) { return v + " px"; };
            _sideGap.ValueChanged += delegate
            {
                if (_syncing) return;
                _config.HorizontalMargin = _sideGap.Value;
                Commit();
            };

            _bottomGap = new LabeledSlider("Bottom gap", 0, 12);
            _bottomGap.Format = delegate(int v) { return v + " px"; };
            _bottomGap.ValueChanged += delegate
            {
                if (_syncing) return;
                _config.VerticalMargin = _bottomGap.Value;
                Commit();
            };

            ToggleSwitch dockTog = new ToggleSwitch();
            _docked = new Row("Full width when an app is maximized", dockTog);
            dockTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                _config.DockedWhenMaximized = dockTog.Checked;
                Commit();
            };

            p.Controls.Add(_floating);
            p.Controls.Add(new SectionHeader("Corners"));
            p.Controls.Add(_corners);
            p.Controls.Add(_radius);
            p.Controls.Add(new SectionHeader("Spacing"));
            p.Controls.Add(_sideGap);
            p.Controls.Add(_bottomGap);
            p.Controls.Add(_docked);
            return p;
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

            p.Controls.Add(_startup);
            p.Controls.Add(_themeRow);
            p.Controls.Add(new SectionHeader("Settings"));
            p.Controls.Add(open);
            p.Controls.Add(reset);
            p.Controls.Add(quit);
            p.Controls.Add(footer);
            return p;
        }

        private void ShowPage()
        {
            _pageLook.Visible = _tabs.SelectedIndex == 0;
            _pageShape.Visible = _tabs.SelectedIndex == 1;
            _pageGeneral.Visible = _tabs.SelectedIndex == 2;
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
                _style.SelectedIndex = Array.IndexOf(StyleModes, p.Mode);
                _shade.Value = p.Luminance;
                _opacity.Value = p.Opacity;
                ((ToggleSwitch)_topLine.Trailing).Checked = !p.ShowTopLine;

                ((ToggleSwitch)_floating.Trailing).Checked = _config.FloatingPillEnabled;
                _corners.SelectedIndex = Array.IndexOf(CornerPresets, _config.CornerRadius);
                _radius.Value = _config.CornerRadius;
                _sideGap.Value = _config.HorizontalMargin;
                _bottomGap.Value = _config.VerticalMargin;
                ((ToggleSwitch)_docked.Trailing).Checked = _config.DockedWhenMaximized;

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
            TaskbarStyleMode m = CurrentProfile().Mode;

            _style.Enabled = look;
            _shade.Enabled = look && m != TaskbarStyleMode.Normal;
            _opacity.Enabled = look && (m == TaskbarStyleMode.Tinted || m == TaskbarStyleMode.Blur || m == TaskbarStyleMode.Acrylic);
            _topLine.Enabled = look;

            bool fl = _config.FloatingPillEnabled;
            _corners.Enabled = fl;
            _radius.Enabled = fl;
            _sideGap.Enabled = fl;
            _bottomGap.Enabled = fl;
            _docked.Enabled = fl;
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

            bool showNote = !string.IsNullOrEmpty(note);
            bool changed = _note.WantVisible != showNote || (showNote && _note.TextValue != note);
            _note.Dot = noteColor;
            if (showNote) _note.TextValue = note;
            _note.Visible = showNote;
            if (changed && IsHandleCreated) Relayout();
        }

        // ---- Layout / sizing --------------------------------------------------------

        /// <summary>Recomputes content height and resizes the window, keeping its bottom edge fixed.</summary>
        private void Relayout()
        {
            int w = Theme.Px(Theme.FlyoutWidth);
            int inner = w - 2;
            int h = _root.GetPreferredHeight(inner) + 2;
            h = Math.Min(h, Theme.Px(Theme.FlyoutMaxHeight));

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

        private void PlaceAboveTray(int dpi)
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int gap = Theme.Px(Theme.FlyoutEdgeGap);
            int w = Theme.Px(Theme.FlyoutWidth);
            int h = Math.Min(_root.GetPreferredHeight(w - 2) + 2, Theme.Px(Theme.FlyoutMaxHeight));
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



