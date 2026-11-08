using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Opacbar
{
    // ------------------------------------------------------------------------------------
    // Private native helpers (kept here so they never collide with NativeMethods.cs)
    // ------------------------------------------------------------------------------------
    internal static class UiNative
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        public static int WindowDpi(IntPtr hwnd)
        {
            try
            {
                uint d = GetDpiForWindow(hwnd);
                if (d >= 48) return (int)d;
            }
            catch { }
            return SystemDpi();
        }

        public static int SystemDpi()
        {
            try
            {
                uint d = GetDpiForSystem();
                if (d >= 48) return (int)d;
            }
            catch { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return (int)Math.Round(g.DpiX);
            }
            catch { }
            return 96;
        }

        public static int DpiForPoint(Point p)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT(p.X, p.Y), 2 /* nearest */);
                uint dx, dy;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0 /* effective */, out dx, out dy) == 0 && dx >= 48)
                    return (int)dx;
            }
            catch { }
            return SystemDpi();
        }
    }

    // ------------------------------------------------------------------------------------
    // Theme: the tokens from STYLEGUIDE.md. Everything reads from here.
    // ------------------------------------------------------------------------------------
    public static class Theme
    {
        // Spacing (logical px)
        public const int Space1 = 4;
        public const int Space2 = 8;
        public const int Space3 = 12;
        public const int Space4 = 16;
        public const int Space6 = 24;

        // Radii (logical px)
        public const int RadiusControl = 6;
        public const int RadiusSegment = 8;
        public const int RadiusMenu = 4;

        // Window
        public const int FlyoutWidth = 380;
        public const int FlyoutMaxHeight = 560;
        public const int FlyoutEdgeGap = 12;

        // Type scale (points; rendered as pt * 96/72 * scale pixels)
        public const float TitlePt = 14f;
        public const float BodyPt = 10f;
        public const float CaptionPt = 9f;
        public const float SmallPt = 8f;

        // Palette (assigned by Apply)
        public static Color Surface, Raised, Hover, Border, Text, TextSecondary, TextDisabled,
                            TrackOff, OnFill, OnFillText, Success, Warning, Error;

        public static bool Dark { get; private set; }

        public static event Action Changed;

        private static float _scale = 1f;
        private static string _family;
        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        static Theme()
        {
            _scale = Math.Max(1f, UiNative.SystemDpi() / 96f);
            Apply(0);
        }

        public static float Scale
        {
            get { return _scale; }
            set
            {
                if (value < 0.5f) value = 0.5f;
                _scale = value;
            }
        }

        public static void SetDpi(int dpi)
        {
            Scale = dpi / 96f;
        }

        public static int Px(float logical)
        {
            return (int)Math.Round(logical * _scale);
        }

        public static float PxF(float logical)
        {
            return logical * _scale;
        }

        // ---- Windows theme detection ----

        private static bool ReadLightFlag(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(name);
                        if (v is int) return (int)v != 0;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>True when Windows apps use the light theme.</summary>
        public static bool SystemAppsLight() { return ReadLightFlag("AppsUseLightTheme"); }

        /// <summary>True when the taskbar and tray use the light theme.</summary>
        public static bool SystemTaskbarLight() { return ReadLightFlag("SystemUsesLightTheme"); }

        /// <summary>uiTheme: 0 = follow Windows, 1 = dark, 2 = light.</summary>
        public static void Apply(int uiTheme)
        {
            bool dark = uiTheme == 1 || (uiTheme != 2 && !SystemAppsLight());
            Dark = dark;
            if (dark)
            {
                Surface = Color.FromArgb(0x1F, 0x1F, 0x1F);
                Raised = Color.FromArgb(0x2B, 0x2B, 0x2B);
                Hover = Color.FromArgb(0x38, 0x38, 0x38);
                Border = Color.FromArgb(0x3D, 0x3D, 0x3D);
                Text = Color.FromArgb(0xFF, 0xFF, 0xFF);
                TextSecondary = Color.FromArgb(0xA6, 0xA6, 0xA6);
                TextDisabled = Color.FromArgb(0x66, 0x66, 0x66);
                TrackOff = Color.FromArgb(0x8A, 0x8A, 0x8A);
                OnFill = Color.FromArgb(0xFF, 0xFF, 0xFF);
                OnFillText = Color.FromArgb(0x00, 0x00, 0x00);
                Success = Color.FromArgb(0x3F, 0xB9, 0x50);
                Warning = Color.FromArgb(0xE3, 0xB3, 0x41);
                Error = Color.FromArgb(0xF8, 0x51, 0x49);
            }
            else
            {
                Surface = Color.FromArgb(0xFA, 0xFA, 0xFA);
                Raised = Color.FromArgb(0xEC, 0xEC, 0xEC);
                Hover = Color.FromArgb(0xE0, 0xE0, 0xE0);
                Border = Color.FromArgb(0xD9, 0xD9, 0xD9);
                Text = Color.FromArgb(0x11, 0x11, 0x11);
                TextSecondary = Color.FromArgb(0x5E, 0x5E, 0x5E);
                TextDisabled = Color.FromArgb(0xAA, 0xAA, 0xAA);
                TrackOff = Color.FromArgb(0x7A, 0x7A, 0x7A);
                OnFill = Color.FromArgb(0x11, 0x11, 0x11);
                OnFillText = Color.FromArgb(0xFF, 0xFF, 0xFF);
                Success = Color.FromArgb(0x1A, 0x7F, 0x37);
                Warning = Color.FromArgb(0x9A, 0x67, 0x00);
                Error = Color.FromArgb(0xCF, 0x22, 0x2E);
            }
            Action h = Changed;
            if (h != null) h();
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // ---- Fonts ----

        private static string Family
        {
            get
            {
                if (_family == null)
                {
                    _family = "Segoe UI";
                    try
                    {
                        using (InstalledFontCollection c = new InstalledFontCollection())
                        {
                            foreach (FontFamily f in c.Families)
                            {
                                if (string.Equals(f.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
                                {
                                    _family = f.Name;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
                return _family;
            }
        }

        /// <summary>Font sized in points at the current scale, built in pixels so it is DPI-correct.</summary>
        public static Font Font(float pt, bool bold)
        {
            string key = pt.ToString("0.0") + (bold ? "b" : "r") + _scale.ToString("0.000");
            Font f;
            if (!_fonts.TryGetValue(key, out f))
            {
                float px = pt * 96f / 72f * _scale;
                f = new Font(Family, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                _fonts[key] = f;
            }
            return f;
        }

        public static Font Title { get { return Font(TitlePt, true); } }
        public static Font Body { get { return Font(BodyPt, false); } }
        public static Font BodyBold { get { return Font(BodyPt, true); } }
        public static Font Caption { get { return Font(CaptionPt, false); } }
        public static Font CaptionBold { get { return Font(CaptionPt, true); } }
        public static Font Small { get { return Font(SmallPt, false); } }

        // ---- Window chrome ----

        public static void RoundCorners(IntPtr hwnd)
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            catch { }
        }

        public static void SetBorderColor(IntPtr hwnd, Color c)
        {
            try
            {
                int bgr = c.R | (c.G << 8) | (c.B << 16);
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref bgr, 4);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------------------------
    // Drawing helpers
    // ------------------------------------------------------------------------------------
    public static class Gfx
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(0f, radius) * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius, float width)
        {
            RectangleF i = new RectangleF(r.X + width / 2f, r.Y + width / 2f, r.Width - width, r.Height - width);
            using (GraphicsPath p = RoundRect(i, Math.Max(0f, radius - width / 2f)))
            using (Pen pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        public static void FillCircle(Graphics g, Color c, float cx, float cy, float d)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillEllipse(b, cx - d / 2f, cy - d / 2f, d, d);
        }

        public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                             TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

        public static void Text(Graphics g, string s, Font f, Rectangle r, Color fore, Color back, TextFormatFlags extra)
        {
            if (string.IsNullOrEmpty(s)) return;
            TextRenderer.DrawText(g, s, f, r, fore, back, Flags | extra);
        }

        public static int Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        }
    }

    // ------------------------------------------------------------------------------------
    // Base control
    // ------------------------------------------------------------------------------------
    public abstract class FlyoutControl : Control
    {
        /// <summary>Fixed width in logical px when placed as a trailing control in a Row (0 = none).</summary>
        public int LogicalWidth { get; set; }

        protected FlyoutControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Surface;
        }

        private bool _wantVisible = true;

        /// <summary>
        /// What was last assigned to Visible. Control.Visible returns false while any parent
        /// is hidden (e.g. the flyout before first show), which would make layout skip everything.
        /// </summary>
        public bool WantVisible { get { return _wantVisible; } }

        protected override void SetVisibleCore(bool value)
        {
            _wantVisible = value;
            base.SetVisibleCore(value);
        }

        public virtual int PreferredWidth { get { return Theme.Px(LogicalWidth); } }

        public virtual int GetPreferredHeight(int width) { return Height; }

        /// <summary>Text/ink color that fades when the control is disabled.</summary>
        protected Color Ink(Color c)
        {
            return Enabled ? c : Theme.Mix(c, Theme.Surface, 0.6f);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Surface);
        }

        protected void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        protected void FocusRing(Graphics g, RectangleF r, float radius)
        {
            if (Focused && ShowFocusCues)
                Gfx.StrokeRound(g, Theme.Text, r, radius, Math.Max(1f, Theme.PxF(1.5f)));
        }
    }

    // ------------------------------------------------------------------------------------
    // Vertical stacking helper
    // ------------------------------------------------------------------------------------
    public class StackPanel : FlyoutControl
    {
        public int Gap { get; set; }   // logical px between children
        public int Pad { get; set; }   // logical px on all sides

        public StackPanel()
        {
            Gap = Theme.Space2;
            Pad = 0;
        }

        private static bool IsShown(Control c)
        {
            FlyoutControl f = c as FlyoutControl;
            return f != null ? f.WantVisible : c.Visible;
        }

        private static int ChildHeight(Control c, int width)
        {
            FlyoutControl f = c as FlyoutControl;
            return f != null ? f.GetPreferredHeight(width) : c.Height;
        }

        public override int GetPreferredHeight(int width)
        {
            int inner = Math.Max(0, width - 2 * Theme.Px(Pad));
            int total = 2 * Theme.Px(Pad);
            bool any = false;
            for (int i = 0; i < Controls.Count; i++)
            {
                Control c = Controls[i];
                if (!IsShown(c)) continue;
                if (any) total += Theme.Px(Gap);
                total += ChildHeight(c, inner);
                any = true;
            }
            return total;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.Px(Pad);
            int inner = Math.Max(0, Width - 2 * pad);
            int y = pad;
            bool any = false;
            for (int i = 0; i < Controls.Count; i++)
            {
                Control c = Controls[i];
                if (!IsShown(c)) continue;
                if (any) y += Theme.Px(Gap);
                int h = ChildHeight(c, inner);
                c.SetBounds(pad, y, inner, h);
                if (c is FlyoutControl) c.PerformLayout();
                y += h;
                any = true;
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Toggle
    // ------------------------------------------------------------------------------------
    public class ToggleSwitch : FlyoutControl
    {
        private bool _checked;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            LogicalWidth = 40;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public override int GetPreferredHeight(int width) { return Theme.Px(20); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Enabled) Checked = !Checked;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Checked = !Checked;
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            float w = Theme.PxF(40), h = Theme.PxF(20);
            RectangleF r = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
            float pad = Theme.PxF(4), d = h - 2 * pad;
            float cy = r.Y + h / 2f;
            if (_checked)
            {
                Gfx.FillRound(g, Ink(Theme.OnFill), r, h / 2f);
                Gfx.FillCircle(g, Ink(Theme.OnFillText), r.Right - pad - d / 2f, cy, d);
            }
            else
            {
                Gfx.StrokeRound(g, Ink(Theme.TrackOff), r, h / 2f, Math.Max(1f, Theme.PxF(1f)));
                Gfx.FillCircle(g, Ink(Theme.TrackOff), r.X + pad + d / 2f, cy, d);
            }
            RectangleF ring = r;
            ring.Inflate(Theme.PxF(2), Theme.PxF(2));
            FocusRing(g, ring, h / 2f + Theme.PxF(2));
        }
    }

    // ------------------------------------------------------------------------------------
    // Segmented control
    // ------------------------------------------------------------------------------------
    public class SegmentedControl : FlyoutControl
    {
        private readonly string[] _items;
        private int _selected = -1;
        private int _hover = -1;
        public event EventHandler SelectedIndexChanged;

        public SegmentedControl(params string[] items)
        {
            _items = items;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                Invalidate();
                EventHandler h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public override int GetPreferredHeight(int width) { return Theme.Px(32); }

        private int HitTest(int x)
        {
            if (_items.Length == 0 || Width <= 0) return -1;
            int i = x * _items.Length / Width;
            return Math.Max(0, Math.Min(_items.Length - 1, i));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.X);
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled)
            {
                Focus();
                SelectedIndex = HitTest(e.X);
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left && _selected > 0) { SelectedIndex = _selected - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Right && _selected < _items.Length - 1) { SelectedIndex = _selected + 1; e.Handled = true; }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            RectangleF all = new RectangleF(0, 0, Width, Height);
            Gfx.FillRound(g, Ink(Theme.Raised), all, Theme.PxF(Theme.RadiusSegment));
            if (_items.Length == 0) return;

            float inset = Theme.PxF(2);
            float segW = (Width - 2 * inset) / _items.Length;
            Font font = Theme.Caption;
            for (int i = 0; i < _items.Length; i++)
            {
                RectangleF seg = new RectangleF(inset + i * segW, inset, segW, Height - 2 * inset);
                bool sel = i == _selected;
                Color fill = Color.Empty;
                Color fore = Theme.Text;
                if (sel) { fill = Theme.OnFill; fore = Theme.OnFillText; }
                else if (i == _hover && Enabled) fill = Theme.Hover;

                if (fill != Color.Empty)
                    Gfx.FillRound(g, Ink(fill), seg, Theme.PxF(Theme.RadiusControl));
                Rectangle tr = Rectangle.Round(seg);
                Gfx.Text(g, _items[i], sel ? Theme.CaptionBold : font, tr, Ink(fore),
                    sel ? Ink(Theme.OnFill) : (i == _hover && Enabled ? Theme.Hover : Ink(Theme.Raised)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            FocusRing(g, all, Theme.PxF(Theme.RadiusSegment));
        }
    }

    // ------------------------------------------------------------------------------------
    // Slider with label and value text
    // ------------------------------------------------------------------------------------
    public class LabeledSlider : FlyoutControl
    {
        private int _min, _max, _value;
        private bool _drag;
        public string Label { get; set; }
        public string Hint { get; set; }
        public bool Gradient { get; set; }        // black -> white track (for the shade slider)
        public Func<int, string> Format { get; set; }
        public event EventHandler ValueChanged;

        public LabeledSlider(string label, int min, int max)
        {
            Label = label;
            _min = min;
            _max = max;
            _value = min;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                EventHandler h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public bool Dragging { get { return _drag; } }

        public override int GetPreferredHeight(int width) { return Theme.Px(46); }

        private float ThumbD { get { return Theme.PxF(16); } }

        private float TrackY { get { return Theme.PxF(34); } }

        private void ValueFromX(int x)
        {
            float left = ThumbD / 2f, right = Width - ThumbD / 2f;
            float t = (x - left) / Math.Max(1f, right - left);
            t = Math.Max(0f, Math.Min(1f, t));
            Value = _min + (int)Math.Round(t * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            _drag = true;
            Capture = true;
            ValueFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag) ValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drag = false;
            Capture = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right || k == Keys.Home || k == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = Math.Max(1, (_max - _min) / 20);
            switch (e.KeyCode)
            {
                case Keys.Left: Value = _value - 1; e.Handled = true; break;
                case Keys.Right: Value = _value + 1; e.Handled = true; break;
                case Keys.PageDown: Value = _value - step; e.Handled = true; break;
                case Keys.PageUp: Value = _value + step; e.Handled = true; break;
                case Keys.Home: Value = _min; e.Handled = true; break;
                case Keys.End: Value = _max; e.Handled = true; break;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);

            int lineH = Theme.Px(20);
            Rectangle labelRect = new Rectangle(0, 0, Width, lineH);
            string valueText = Format != null ? Format(_value) : _value.ToString();
            int valueW = Gfx.Measure(valueText, Theme.Body) + Theme.Px(2);
            Gfx.Text(g, valueText, Theme.Body, new Rectangle(Width - valueW, 0, valueW, lineH),
                Ink(Theme.TextSecondary), Theme.Surface, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            int lw = Gfx.Measure(Label, Theme.Body);
            Gfx.Text(g, Label, Theme.Body, new Rectangle(0, 0, Math.Max(0, Width - valueW - Theme.Px(8)), lineH),
                Ink(Theme.Text), Theme.Surface, TextFormatFlags.VerticalCenter);
            if (!string.IsNullOrEmpty(Hint))
            {
                int hx = lw + Theme.Px(8);
                int hw = Width - valueW - Theme.Px(8) - hx;
                if (hw > 0)
                    Gfx.Text(g, Hint, Theme.Caption, new Rectangle(hx, 0, hw, lineH),
                        Ink(Theme.TextSecondary), Theme.Surface, TextFormatFlags.VerticalCenter);
            }

            float trackH = Theme.PxF(4);
            float left = ThumbD / 2f, right = Width - ThumbD / 2f;
            float cy = TrackY;
            RectangleF track = new RectangleF(left, cy - trackH / 2f, right - left, trackH);
            float t = _max > _min ? (float)(_value - _min) / (_max - _min) : 0f;
            float tx = left + t * (right - left);

            if (Gradient)
            {
                RectangleF gr = new RectangleF(left - Theme.PxF(2), cy - Theme.PxF(3), right - left + Theme.PxF(4), Theme.PxF(6));
                using (GraphicsPath p = Gfx.RoundRect(gr, gr.Height / 2f))
                using (LinearGradientBrush br = new LinearGradientBrush(gr, Color.Black, Color.White, 0f))
                {
                    if (!Enabled)
                    {
                        br.LinearColors = new Color[] { Ink(Color.Black), Ink(Color.White) };
                    }
                    g.FillPath(br, p);
                }
                Gfx.StrokeRound(g, Ink(Theme.TrackOff), gr, gr.Height / 2f, Math.Max(1f, Theme.PxF(1f)));
            }
            else
            {
                Gfx.FillRound(g, Ink(Theme.Hover), track, trackH / 2f);
                if (tx > left)
                    Gfx.FillRound(g, Ink(Theme.OnFill), new RectangleF(left, track.Y, tx - left, trackH), trackH / 2f);
            }

            // Thumb: filled disc with a surface-colored ring so it reads on any track.
            float ring = Theme.PxF(2);
            Gfx.FillCircle(g, Theme.Surface, tx, cy, ThumbD + ring);
            Gfx.FillCircle(g, Ink(Theme.OnFill), tx, cy, ThumbD - Theme.PxF(2));
            if (Focused && ShowFocusCues)
            {
                using (Pen pen = new Pen(Theme.Text, Math.Max(1f, Theme.PxF(1.5f))))
                    g.DrawEllipse(pen, tx - ThumbD / 2f - ring, cy - ThumbD / 2f - ring, ThumbD + 2 * ring, ThumbD + 2 * ring);
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Row: label (+ optional description) on the left, one control on the right
    // ------------------------------------------------------------------------------------
    public class Row : FlyoutControl
    {
        private FlyoutControl _trailing;
        public string Label { get; set; }
        public string Description { get; set; }

        public Row(string label, FlyoutControl trailing)
        {
            Label = label;
            _trailing = trailing;
            if (trailing != null) Controls.Add(trailing);
        }

        public FlyoutControl Trailing { get { return _trailing; } }

        public override int GetPreferredHeight(int width)
        {
            return Theme.Px(string.IsNullOrEmpty(Description) ? 36 : 52);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_trailing == null) return;
            int w = _trailing.PreferredWidth;
            if (w <= 0) w = Theme.Px(40);
            int h = _trailing.GetPreferredHeight(w);
            _trailing.SetBounds(Width - w, (Height - h) / 2, w, h);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            ToggleSwitch t = _trailing as ToggleSwitch;
            if (t != null && Enabled) t.Checked = !t.Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int tw = _trailing != null ? _trailing.Width + Theme.Px(Theme.Space3) : 0;
            int avail = Math.Max(0, Width - tw);
            if (string.IsNullOrEmpty(Description))
            {
                Gfx.Text(g, Label, Theme.Body, new Rectangle(0, 0, avail, Height), Ink(Theme.Text), Theme.Surface,
                    TextFormatFlags.VerticalCenter);
            }
            else
            {
                int l1 = Theme.Px(20), l2 = Theme.Px(16);
                int top = (Height - l1 - l2) / 2;
                Gfx.Text(g, Label, Theme.Body, new Rectangle(0, top, avail, l1), Ink(Theme.Text), Theme.Surface,
                    TextFormatFlags.VerticalCenter);
                Gfx.Text(g, Description, Theme.Caption, new Rectangle(0, top + l1, avail, l2), Ink(Theme.TextSecondary),
                    Theme.Surface, TextFormatFlags.VerticalCenter);
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Section header
    // ------------------------------------------------------------------------------------
    public class SectionHeader : FlyoutControl
    {
        public string Caption { get; set; }

        public SectionHeader(string caption) { Caption = caption; }

        public override int GetPreferredHeight(int width) { return Theme.Px(24); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Gfx.Text(e.Graphics, Caption, Theme.CaptionBold, new Rectangle(0, 0, Width, Height),
                Ink(Theme.TextSecondary), Theme.Surface, TextFormatFlags.Bottom);
        }
    }

    // ------------------------------------------------------------------------------------
    // Text block with optional status dot (wraps to the available width)
    // ------------------------------------------------------------------------------------
    public class TextBlock : FlyoutControl
    {
        private string _text;
        private Color? _dot;
        public bool Small { get; set; }
        public bool Center { get; set; }
        public bool Secondary { get; set; }

        public TextBlock(string text)
        {
            _text = text;
            Secondary = true;
        }

        public string TextValue
        {
            get { return _text; }
            set { _text = value; Invalidate(); }
        }

        public Color? Dot
        {
            get { return _dot; }
            set { _dot = value; Invalidate(); }
        }

        private Font F { get { return Small ? Theme.Small : Theme.Caption; } }

        private int Indent { get { return _dot.HasValue ? Theme.Px(16) : 0; } }

        public override int GetPreferredHeight(int width)
        {
            if (string.IsNullOrEmpty(_text)) return 0;
            int w = Math.Max(10, width - Indent);
            Size s = TextRenderer.MeasureText(_text, F, new Size(w, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return Math.Max(s.Height, Theme.Px(16));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (string.IsNullOrEmpty(_text)) return;
            int indent = Indent;
            if (_dot.HasValue)
            {
                Smooth(g);
                Gfx.FillCircle(g, _dot.Value, Theme.PxF(4), Theme.PxF(8), Theme.PxF(8));
            }
            TextFormatFlags fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            if (Center) fl |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, _text, F, new Rectangle(indent, 0, Width - indent, Height),
                Secondary ? Theme.TextSecondary : Theme.Text, Theme.Surface, fl);
        }
    }

    // ------------------------------------------------------------------------------------
    // Flat button
    // ------------------------------------------------------------------------------------
    public class FlatButton : FlyoutControl
    {
        private bool _hover, _down;
        public string Caption { get; set; }
        public bool Danger { get; set; }

        public FlatButton(string caption)
        {
            Caption = caption;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public override int GetPreferredHeight(int width) { return Theme.Px(36); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            RectangleF r = new RectangleF(0, 0, Width, Height);
            Color fill = Theme.Surface;
            if (Enabled && _down) fill = Theme.Hover;
            else if (Enabled && _hover) fill = Theme.Raised;
            Gfx.FillRound(g, fill, r, Theme.PxF(Theme.RadiusControl));
            Gfx.StrokeRound(g, Ink(Theme.Border), r, Theme.PxF(Theme.RadiusControl), Math.Max(1f, Theme.PxF(1f)));
            Color fore = Danger ? Theme.Error : Theme.Text;
            Gfx.Text(g, Caption, Theme.Body, new Rectangle(0, 0, Width, Height), Ink(fore), fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            FocusRing(g, r, Theme.PxF(Theme.RadiusControl));
        }
    }

    // ------------------------------------------------------------------------------------
    // Header: title, status line with dot, master toggle. The title area drags the window.
    // ------------------------------------------------------------------------------------
    public class HeaderBar : FlyoutControl
    {
        private readonly ToggleSwitch _toggle = new ToggleSwitch();
        private string _status = "";
        private Color _statusColor = Theme.TextSecondary;
        public event EventHandler DragRequested;

        public HeaderBar()
        {
            Controls.Add(_toggle);
        }

        public ToggleSwitch Toggle { get { return _toggle; } }

        public void SetStatus(string text, Color dot)
        {
            _status = text;
            _statusColor = dot;
            Invalidate();
        }

        public override int GetPreferredHeight(int width) { return Theme.Px(44); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int w = _toggle.PreferredWidth, h = _toggle.GetPreferredHeight(w);
            _toggle.SetBounds(Width - w, (Height - h) / 2, w, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                EventHandler h = DragRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            int titleH = Theme.Px(24);
            Gfx.Text(g, "Opacbar", Theme.Title, new Rectangle(0, 0, Width, titleH), Theme.Text, Theme.Surface,
                TextFormatFlags.VerticalCenter);
            int dotX = Theme.Px(4);
            int lineH = Theme.Px(16);
            int y = Height - lineH;
            Gfx.FillCircle(g, _statusColor, dotX, y + lineH / 2f, Theme.PxF(8));
            Gfx.Text(g, _status, Theme.Caption, new Rectangle(Theme.Px(16), y, Math.Max(0, Width - _toggle.Width - Theme.Px(24)), lineH),
                Theme.TextSecondary, Theme.Surface, TextFormatFlags.VerticalCenter);
        }
    }

    // ------------------------------------------------------------------------------------
    // Tray menu renderer (monochrome)
    // ------------------------------------------------------------------------------------
    public class MenuColors : ProfessionalColorTable
    {
        public MenuColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Surface; } }
    }

    public class MonochromeMenuRenderer : ToolStripProfessionalRenderer
    {
        public MonochromeMenuRenderer() : base(new MenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.Surface);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // flat: no separate margin column
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(Theme.Px(4), Theme.Px(1), e.Item.Width - 2 * Theme.Px(4), e.Item.Height - 2 * Theme.Px(1));
            using (SolidBrush b = new SolidBrush(Theme.Surface))
                g.FillRectangle(b, 0, 0, e.Item.Width, e.Item.Height);
            if (e.Item.Selected && e.Item.Enabled)
                Gfx.FillRound(g, Theme.Hover, r, Theme.PxF(Theme.RadiusMenu));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? Theme.TextSecondary : Theme.TextDisabled;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ImageRectangle;
            float s = Math.Min(r.Width, r.Height);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float u = Math.Max(4f, s * 0.28f);
            using (Pen p = new Pen(Theme.Text, Math.Max(1.5f, Theme.PxF(1.6f))))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] {
                    new PointF(cx - u, cy + 0.05f * u),
                    new PointF(cx - u * 0.35f, cy + u * 0.7f),
                    new PointF(cx + u, cy - u * 0.65f) });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawLine(p, Theme.Px(8), y, e.Item.Width - Theme.Px(8), y);
        }
    }

    // ------------------------------------------------------------------------------------
    // Tray icon: crisp monochrome pill glyph generated at the real icon size
    // ------------------------------------------------------------------------------------
    public static class IconFactory
    {
        public static Icon CreateTrayIcon(bool enabled)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            Color ink = Theme.SystemTaskbarLight() ? Color.FromArgb(0x11, 0x11, 0x11) : Color.White;
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    float stroke = Math.Max(1.5f, size / 12f);
                    float x = size * 0.07f, w = size * 0.86f;
                    float h = size * 0.46f, y = (size - h) / 2f;
                    int a = enabled ? 255 : 120;
                    Color c = Color.FromArgb(a, ink);
                    RectangleF outer = new RectangleF(x, y, w, h);
                    Gfx.StrokeRound(g, c, outer, h / 2f, stroke);
                    if (enabled)
                    {
                        // Filled left half: reads as "bar with transparency".
                        float inset = stroke + Math.Max(1f, size / 16f);
                        float fh = h - 2 * inset;
                        RectangleF fill = new RectangleF(x + inset, y + inset, (w - 2 * inset) * 0.5f, fh);
                        Gfx.FillRound(g, c, fill, fh / 2f);
                    }
                }
                IntPtr h2 = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h2))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(h2);
                }
            }
        }
    }
}


