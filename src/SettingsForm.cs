// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace OpacityBar
{
    /// <summary>
    /// The OpacityBar flyout: a small, borderless window anchored above the tray. Main page: the look.
    /// The gear in the header switches to the Settings page (startup, theme, reset, quit).
    /// One scale factor (window DPI / 96) drives every size and font.
    /// </summary>
    public class SettingsForm : Form
    {
        private const int WM_DPICHANGED = 0x02E0;
        private const int CS_DROPSHADOW = 0x00020000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly Material[] Styles = new Material[] { Material.Normal, Material.Clear, Material.Color, Material.Blur };

        private readonly AppConfig _config;
        private readonly TaskbarEngine _engine;
        private readonly ConfigSaver _saver;
        private readonly Timer _statusTimer = new Timer();

        private bool _syncing;        // pushing config into controls: their change events must not commit
        private bool _dragging;       // moving the window by its header
        private bool _suppressHide;   // a dialog or another app is opening on purpose
        private bool _pickerOpen;
        private DateTime _restartClickedUtc = DateTime.MinValue;

        // Layout
        private StackPanel _root;
        private HeaderBar _header;
        private TextBlock _note;
        private FlatButton _restartBtn;
        private bool _settingsPage;
        private StackPanel _pageLook, _pageGeneral;

        // Look
        private SegmentedControl _style;
        private ColorSwatchRow _colorRow;
        private ColorPickerPanel _picker;
        private LabeledSlider _opacity;
        private LabeledSlider _blurAmount;
        private TextBlock _hint, _normalHint;
        private Row _topLine, _popups, _search;

        // General
        private Row _startup;
        private SegmentedControl _themeSeg;
        private TextBlock _mapNote;

        public event EventHandler ConfigChanged;
        public event EventHandler QuitRequested;

        /// <summary>UTC time the flyout last hid itself (guards the tray click that caused the hide).</summary>
        public DateTime LastHideUtc { get; private set; }

        public SettingsForm(AppConfig config, TaskbarEngine engine, ConfigSaver saver)
        {
            _config = config;
            _engine = engine;
            _saver = saver;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            Text = AppInfo.Name;
            BackColor = Theme.Surface;

            _statusTimer.Interval = 1000;
            _statusTimer.Tick += delegate { RefreshStatus(); };

            BuildUi();

            _engine.StateUpdated += OnEngineStateUpdated;
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

        private StyleProfile Look { get { return _config.DesktopProfile; } }

        // ---- UI construction --------------------------------------------------------

        private void BuildUi()
        {
            _root = new StackPanel { Pad = Theme.Space4, Gap = Theme.Space3 };

            _header = new HeaderBar();
            _header.DragRequested += delegate { BeginDrag(); };
            _header.IconClicked += delegate { SetPage(!_settingsPage); };

            _note = new TextBlock("") { Visible = false };

            _restartBtn = new FlatButton("Restart Explorer now") { Visible = false, Primary = true };
            _restartBtn.Click += delegate
            {
                _restartClickedUtc = DateTime.UtcNow;
                _restartBtn.Enabled = false;
                _engine.RestartExplorer();
            };

            _pageLook = BuildLookPage();
            _pageGeneral = BuildGeneralPage();

            _root.Controls.Add(_header);
            _root.Controls.Add(_note);
            _root.Controls.Add(_restartBtn);
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

            _style = new SegmentedControl("Normal", "Clear", "Color", "Blur");
            _style.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                int i = _style.SelectedIndex;
                if (i < 0 || i >= Styles.Length) return;
                Look.Material = Styles[i];
                if ((Look.Material == Material.Color || Look.Material == Material.Blur) && Look.Opacity < 10)
                {
                    // Coming from Clear: start at a visible opacity instead of an invisible one.
                    Look.Opacity = 60;
                    _syncing = true;
                    _opacity.Value = Look.Opacity;
                    _syncing = false;
                }
                UpdateVisibility();
                Commit();
            };

            _colorRow = new ColorSwatchRow("Color");
            _colorRow.Clicked += delegate
            {
                _pickerOpen = !_pickerOpen;
                UpdateVisibility();
            };

            _picker = new ColorPickerPanel();
            _picker.ColorChanged += delegate
            {
                if (_syncing) return;
                Color c = _picker.Value;
                Look.Color = ColorMath.Hex(c);
                _colorRow.Value = c;
                CommitDrag();
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
                Look.Opacity = _opacity.Value;
                CommitDrag();
            };

            _blurAmount = new LabeledSlider("Blur amount", 0, 100);
            _blurAmount.Format = delegate(int v) { return v + "%"; };
            _blurAmount.ValueChanged += delegate
            {
                if (_syncing) return;
                Look.BlurAmount = _blurAmount.Value;
                CommitDrag();
            };

            _hint = new TextBlock("Needs Windows transparency effects on") { Visible = false };

            ToggleSwitch lineTog = new ToggleSwitch();
            _topLine = new Row("Hide top line", lineTog);
            lineTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                Look.HideTopLine = lineTog.Checked;
                Commit();
            };

            _normalHint = new TextBlock("The standard Windows taskbar.") { Visible = false };

            p.Controls.Add(new SectionHeader("Style"));
            p.Controls.Add(_style);
            p.Controls.Add(_normalHint);
            p.Controls.Add(_colorRow);
            p.Controls.Add(_picker);
            p.Controls.Add(_blurAmount);
            p.Controls.Add(_opacity);
            p.Controls.Add(_hint);
            ToggleSwitch popTog = new ToggleSwitch();
            _popups = new Row("Style hover previews", popTog);
            popTog.CheckedChanged += delegate
            {
                if (_syncing) return;
                _config.StylePopups = popTog.Checked;
                Commit();
            };

            SegmentedControl searchSeg = new SegmentedControl("System", "Dark", "Light");
            searchSeg.LogicalWidth = 200;
            _search = new Row("Search box", searchSeg);
            searchSeg.SelectedIndexChanged += delegate
            {
                if (_syncing || searchSeg.SelectedIndex < 0) return;
                _config.SearchBoxTheme = searchSeg.SelectedIndex;
                Commit();
            };

            p.Controls.Add(_topLine);
            p.Controls.Add(_search);
            p.Controls.Add(_popups);
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
            Row themeRow = new Row("Theme", _themeSeg);
            _themeSeg.SelectedIndexChanged += delegate
            {
                if (_syncing || _themeSeg.SelectedIndex < 0) return;
                _config.UiTheme = _themeSeg.SelectedIndex;
                Theme.Apply(_config.UiTheme);
                OnThemeChanged();
                Commit();
            };

            FlatButton reset = new FlatButton("Reset to defaults");
            reset.Click += delegate { ResetDefaults(); };

            FlatButton quit = new FlatButton("Quit " + AppInfo.Name);
            quit.Danger = true;
            quit.Click += delegate
            {
                EventHandler h = QuitRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            TextBlock footer = new TextBlock(AppInfo.Name + " " + AppInfo.Version + " by " + AppInfo.Publisher + " · open source\nNo network, no tracking. Emergency quit: Ctrl+Shift+Alt+Q");
            footer.Small = true;
            footer.Center = true;

            p.Controls.Add(_startup);
            p.Controls.Add(themeRow);
            p.Controls.Add(reset);
            if (Diagnostics.Enabled)
            {
                FlatButton map = new FlatButton("Save taskbar map...");
                map.Click += delegate { SaveTaskbarMap(); };
                _mapNote = new TextBlock("") { Visible = false };
                p.Controls.Add(new SectionHeader("Diagnostics"));
                p.Controls.Add(map);
                p.Controls.Add(_mapNote);
            }
            p.Controls.Add(quit);
            p.Controls.Add(footer);
            return p;
        }

        private void ShowPage()
        {
            _header.SettingsMode = _settingsPage;
            _pageLook.Visible = !_settingsPage;
            _pageGeneral.Visible = _settingsPage;
        }

        private void SetPage(bool settings)
        {
            _settingsPage = settings;
            ShowPage();
            if (IsHandleCreated) Relayout();
        }

        private void AddRecentColor(string hex)
        {
            string n = TaskbarStyle.NormalizeHex(hex);
            if (n == null) return;
            if (_config.RecentColors == null) _config.RecentColors = new List<string>();
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

        // ---- Config <-> controls ----------------------------------------------------

        /// <summary>Pushes the current config into every control (no events leak back).</summary>
        public void SyncControlsFromConfig()
        {
            bool was = _syncing;
            _syncing = true;
            try
            {
                _style.SelectedIndex = Array.IndexOf(Styles, Look.Material);
                _opacity.Value = Look.Opacity;
                _blurAmount.Value = Look.BlurAmount;
                Color pc = ColorMath.Parse(Look.Color, Color.Black);
                _picker.Recents = _config.RecentColors;
                _picker.Value = pc;
                _colorRow.Value = pc;
                ((ToggleSwitch)_topLine.Trailing).Checked = Look.HideTopLine;
                ((ToggleSwitch)_popups.Trailing).Checked = _config.StylePopups;
                ((SegmentedControl)_search.Trailing).SelectedIndex = _config.SearchBoxTheme;
                ((ToggleSwitch)_startup.Trailing).Checked = _config.LaunchAtStartup;
                _themeSeg.SelectedIndex = _config.UiTheme;
            }
            finally
            {
                _syncing = was;
            }
            UpdateVisibility();
            RefreshStatus();
            Relayout();
        }

        private void UpdateVisibility()
        {
            bool normal = Look.Material == Material.Normal;
            bool blur = Look.Material == Material.Blur;
            bool colored = Look.Material == Material.Color || blur;   // Blur: color + tint color the blur

            _normalHint.Visible = normal;
            _topLine.Visible = !normal;
            _popups.Visible = !normal;
            _search.Visible = !normal;
            _colorRow.Visible = colored;
            _colorRow.Expanded = _pickerOpen;
            _picker.Visible = colored && _pickerOpen;
            _opacity.Label = blur ? "Tint" : "Opacity";
            _opacity.Visible = colored;
            _blurAmount.Visible = blur;

            _hint.Visible = blur;
            if (blur)
            {
                bool off = !TransparencyEnabled();
                _hint.Dot = off ? (Color?)Theme.Warning : null;
                _hint.TextValue = off ? "Needs Windows transparency effects on. They are off right now." : "Needs Windows transparency effects on";
            }
            if (IsHandleCreated) Relayout();
        }

        /// <summary>A discrete change (click, toggle, swatch, typed color): apply and save now.</summary>
        private void Commit()
        {
            if (_syncing) return;
            _saver.SaveNow();
            _engine.ConfigChanged();
            RefreshStatus();
            EventHandler h = ConfigChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>Continuous input (slider or picker drag): apply live, save once the input pauses.</summary>
        private void CommitDrag()
        {
            if (_syncing) return;
            _engine.ConfigChanged();
            _saver.SaveSoon();
        }

        // ---- Status -----------------------------------------------------------------

        private void OnEngineStateUpdated()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(new Action(RefreshStatus));
            else RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (IsDisposed) return;

            // No status line when everything works: a note appears only when something needs attention.
            string note = null;
            Color noteColor = Theme.Warning;
            EngineHealth health = _engine.Health;
            string detail = _engine.HealthDetail;
            switch (health)
            {
                case EngineHealth.NoTaskbar:
                case EngineHealth.HelperMissing:
                case EngineHealth.HelperOutdated:
                    note = detail;
                    break;
                case EngineHealth.Connecting:
                    note = string.IsNullOrEmpty(detail) ? "Connecting to the taskbar..." : detail;
                    noteColor = Theme.TextSecondary;
                    break;
                case EngineHealth.HelperFailed:
                    note = string.IsNullOrEmpty(detail) ? "The taskbar helper stopped working." : detail;
                    noteColor = Theme.Error;
                    break;
            }

            bool showRestart = health == EngineHealth.HelperOutdated;
            bool restartChanged = _restartBtn.WantVisible != showRestart;
            _restartBtn.Visible = showRestart;
            _restartBtn.Enabled = (DateTime.UtcNow - _restartClickedUtc).TotalSeconds > 8;

            bool showNote = !string.IsNullOrEmpty(note);
            bool changed = _note.WantVisible != showNote || (showNote && _note.TextValue != note);
            _note.Dot = noteColor;
            if (showNote) _note.TextValue = note;
            _note.Visible = showNote;
            if ((changed || restartChanged) && IsHandleCreated) Relayout();
        }

        // ---- Layout / sizing --------------------------------------------------------

        /// <summary>Recomputes content height and resizes the window, keeping its bottom-right corner fixed.</summary>
        private void Relayout()
        {
            int w = Theme.Px(Theme.FlyoutWidth);
            int h = Math.Min(_root.GetPreferredHeight(w - 2) + 2, MaxHeight());

            if (Visible) SetBounds(Right - w, Bottom - h, w, h);
            else Size = new Size(w, h);
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

        private void PlaceAboveTray()
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
            int dpi = UiNative.DpiForPoint(Cursor.Position);
            Theme.SetDpi(dpi);
            Theme.Apply(_config.UiTheme);
            BackColor = Theme.Surface;
            _settingsPage = false;   // always open on the look
            ShowPage();
            SyncControlsFromConfig();
            PlaceAboveTray();

            _suppressHide = false;
            Show();
            int actual = UiNative.WindowDpi(Handle);
            if (actual != dpi)
            {
                Theme.SetDpi(actual);
                Relayout();
                PlaceAboveTray();
            }
            Activate();
            UiNative.SetForegroundWindow(Handle);
            _statusTimer.Start();
        }

        public void HideFlyout()
        {
            if (!Visible) return;
            _statusTimer.Stop();
            LastHideUtc = DateTime.UtcNow;
            Hide();
            _saver.Flush();
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
                if (_settingsPage) SetPage(false);
                else HideFlyout();
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
                _engine.StateUpdated -= OnEngineStateUpdated;
                _statusTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- General actions --------------------------------------------------------

        private void SaveTaskbarMap()
        {
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
                r = MessageBox.Show(this, "Reset all settings to their defaults?", AppInfo.Name,
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
