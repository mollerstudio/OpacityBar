// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpacityBar
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

        // Radii (logical px)
        public const int RadiusControl = 6;
        public const int RadiusSegment = 8;
        public const int RadiusMenu = 4;

        // Window
        public const int FlyoutWidth = 380;
        public const int FlyoutMaxHeight = 760;
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
        // Outfit (SIL Open Font License), embedded by build.ps1. Loaded into GDI+ (for Font objects)
        // and GDI (for TextRenderer). Falls back to Segoe UI if the files are missing.
        private static PrivateFontCollection _outfit;
        private static FontFamily _outfitRegular, _outfitSemiBold;
        private static bool _outfitTried;

        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        private static void LoadOutfit()
        {
            if (_outfitTried) return;
            _outfitTried = true;
            try
            {
                _outfit = new PrivateFontCollection();
                System.Reflection.Assembly asm = typeof(Theme).Assembly;
                foreach (string name in new[] { "Outfit-Regular.ttf", "Outfit-SemiBold.ttf" })
                {
                    using (System.IO.Stream s = asm.GetManifestResourceStream(name))
                    {
                        if (s == null) continue;
                        byte[] data = new byte[s.Length];
                        int read = 0;
                        while (read < data.Length) { int n = s.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
                        IntPtr mem = Marshal.AllocCoTaskMem(data.Length); // kept for the life of the process
                        Marshal.Copy(data, 0, mem, data.Length);
                        _outfit.AddMemoryFont(mem, data.Length);
                        uint count;
                        AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, out count);
                    }
                }
                foreach (FontFamily f in _outfit.Families)
                {
                    if (string.Equals(f.Name, "Outfit", StringComparison.OrdinalIgnoreCase)) _outfitRegular = f;
                    else if (string.Equals(f.Name, "Outfit SemiBold", StringComparison.OrdinalIgnoreCase)) _outfitSemiBold = f;
                }
            }
            catch { _outfitRegular = null; _outfitSemiBold = null; }
        }

        private static Font CreateFont(float px, bool bold)
        {
            LoadOutfit();
            if (bold && _outfitSemiBold != null) return new Font(_outfitSemiBold, px, FontStyle.Regular, GraphicsUnit.Pixel);
            if (_outfitRegular != null) return new Font(_outfitRegular, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return new Font(Family, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static Font Font(float pt, bool bold)
        {
            string key = pt.ToString("0.0") + (bold ? "b" : "r") + _scale.ToString("0.000");
            Font f;
            if (!_fonts.TryGetValue(key, out f))
            {
                float px = pt * 96f / 72f * _scale;
                f = CreateFont(px, bold);
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

            Gfx.Text(g, Label, Theme.Body, new Rectangle(0, 0, Math.Max(0, Width - valueW - Theme.Px(8)), lineH),
                Ink(Theme.Text), Theme.Surface, TextFormatFlags.VerticalCenter);

            float trackH = Theme.PxF(4);
            float left = ThumbD / 2f, right = Width - ThumbD / 2f;
            float cy = TrackY;
            RectangleF track = new RectangleF(left, cy - trackH / 2f, right - left, trackH);
            float t = _max > _min ? (float)(_value - _min) / (_max - _min) : 0f;
            float tx = left + t * (right - left);

            Gfx.FillRound(g, Ink(Theme.Hover), track, trackH / 2f);
            if (tx > left)
                Gfx.FillRound(g, Ink(Theme.OnFill), new RectangleF(left, track.Y, tx - left, trackH), trackH / 2f);

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
    // Row: label on the left, one control on the right
    // ------------------------------------------------------------------------------------
    public class Row : FlyoutControl
    {
        private FlyoutControl _trailing;
        public string Label { get; set; }

        public Row(string label, FlyoutControl trailing)
        {
            Label = label;
            _trailing = trailing;
            if (trailing != null) Controls.Add(trailing);
        }

        public FlyoutControl Trailing { get { return _trailing; } }

        public override int GetPreferredHeight(int width)
        {
            return Theme.Px(36);
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
            Gfx.Text(g, Label, Theme.Body, new Rectangle(0, 0, avail, Height), Ink(Theme.Text), Theme.Surface,
                TextFormatFlags.VerticalCenter);
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

        public TextBlock(string text)
        {
            _text = text;
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
                Theme.TextSecondary, Theme.Surface, fl);
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
        /// <summary>Filled, high-emphasis button (inverted monochrome). Use at most one per view.</summary>
        public bool Primary { get; set; }

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
            if (Primary)
            {
                // Inverted: text color as fill, surface color as text (white on dark, black on light).
                fill = Enabled ? Theme.Text : Theme.Mix(Theme.Text, Theme.Surface, 0.6f);
                if (Enabled && (_hover || _down)) fill = Theme.Mix(Theme.Text, Theme.Surface, _down ? 0.25f : 0.12f);
            }
            else if (Enabled && _down) fill = Theme.Hover;
            else if (Enabled && _hover) fill = Theme.Raised;
            Gfx.FillRound(g, fill, r, Theme.PxF(Theme.RadiusControl));
            if (!Primary)
                Gfx.StrokeRound(g, Ink(Theme.Border), r, Theme.PxF(Theme.RadiusControl), Math.Max(1f, Theme.PxF(1f)));
            Color fore = Primary ? Theme.Surface : Ink(Danger ? Theme.Error : Theme.Text);
            Gfx.Text(g, Caption, Primary ? Theme.BodyBold : Theme.Body, new Rectangle(0, 0, Width, Height), fore, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            FocusRing(g, r, Theme.PxF(Theme.RadiusControl));
        }
    }

    // ------------------------------------------------------------------------------------
    // Header: title plus one icon button. Main page: "OpacityBar" + gear (opens Settings).
    // Settings page: back arrow + "Settings". The rest of the header drags the window.
    // ------------------------------------------------------------------------------------
    public class HeaderBar : FlyoutControl
    {
        private const string GlyphGear = "";
        private const string GlyphBack = "";
        private static string _iconFamily;

        private bool _settingsMode;
        private bool _hover, _down;
        public event EventHandler DragRequested;
        public event EventHandler IconClicked;

        public HeaderBar()
        {
            Cursor = Cursors.Default;
        }

        /// <summary>False: "OpacityBar" + gear. True: back arrow + "Settings".</summary>
        public bool SettingsMode
        {
            get { return _settingsMode; }
            set { _settingsMode = value; Invalidate(); }
        }

        public override int GetPreferredHeight(int width) { return Theme.Px(36); }

        private static string IconFamily
        {
            get
            {
                if (_iconFamily == null)
                {
                    _iconFamily = "Segoe MDL2 Assets";
                    try
                    {
                        using (InstalledFontCollection c = new InstalledFontCollection())
                            foreach (FontFamily f in c.Families)
                                if (f.Name == "Segoe Fluent Icons") { _iconFamily = f.Name; break; }
                    }
                    catch { }
                }
                return _iconFamily;
            }
        }

        private Rectangle IconRect
        {
            get
            {
                int s = Theme.Px(32);
                int y = (Height - s) / 2;
                return _settingsMode ? new Rectangle(-Theme.Px(6), y, s, s) : new Rectangle(Width - s + Theme.Px(6), y, s, s);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = IconRect.Contains(e.Location);
            if (h != _hover) { _hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false; _down = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (IconRect.Contains(e.Location))
            {
                _down = true;
                Invalidate();
                return;
            }
            EventHandler h = DragRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool click = _down && IconRect.Contains(e.Location);
            _down = false;
            Invalidate();
            if (click)
            {
                EventHandler h = IconClicked;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            Rectangle ir = IconRect;
            if (_hover || _down)
                Gfx.FillRound(g, _down ? Theme.Hover : Theme.Raised, ir, Theme.PxF(Theme.RadiusControl));
            using (Font icon = new Font(IconFamily, Theme.PxF(16), FontStyle.Regular, GraphicsUnit.Pixel))
                Gfx.Text(g, _settingsMode ? GlyphBack : GlyphGear, icon, ir, Theme.Text, (_hover || _down) ? (_down ? Theme.Hover : Theme.Raised) : Theme.Surface,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int titleX = _settingsMode ? ir.Right + Theme.Px(4) : 0;
            Gfx.Text(g, _settingsMode ? "Settings" : AppInfo.Name, Theme.Title,
                new Rectangle(titleX, 0, Math.Max(0, Width - titleX - Theme.Px(36)), Height),
                Theme.Text, Theme.Surface, TextFormatFlags.VerticalCenter);
        }
    }

    // ------------------------------------------------------------------------------------
    // Color picker. The chrome stays monochrome; the content (swatches, gradients) is full color.
    // ------------------------------------------------------------------------------------
    public static class ColorMath
    {
        public static Color FromHsv(float h, float s, float v)
        {
            h = h % 360f;
            if (h < 0) h += 360f;
            s = Math.Max(0f, Math.Min(1f, s));
            v = Math.Max(0f, Math.Min(1f, v));
            float c = v * s;
            float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
            float m = v - c;
            float r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb((int)Math.Round((r + m) * 255f), (int)Math.Round((g + m) * 255f), (int)Math.Round((b + m) * 255f));
        }

        public static void ToHsv(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float d = max - min;
            v = max;
            s = max <= 0f ? 0f : d / max;
            if (d <= 0f) h = 0f;
            else if (max == r) h = 60f * (((g - b) / d) % 6f);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
            if (h < 0f) h += 360f;
        }

        public static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>"#RRGGBB" or "RRGGBB" to 0xRRGGBB.</summary>
        public static bool TryParseHex(string s, out int rgb)
        {
            rgb = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6) return false;
            for (int i = 0; i < 6; i++)
            {
                char ch = s[i];
                bool ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!ok) return false;
            }
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out rgb);
        }

        public static bool TryParse(string s, out Color c)
        {
            int rgb;
            if (TryParseHex(s, out rgb))
            {
                c = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                return true;
            }
            c = Color.Black;
            return false;
        }

        public static Color Parse(string s, Color fallback)
        {
            Color c;
            return TryParse(s, out c) ? c : fallback;
        }
    }

    // The "Color" row: label left, color chip + hex + chevron right. Click expands the picker.
    public class ColorSwatchRow : FlyoutControl
    {
        private Color _value = Color.Black;
        private bool _expanded;
        public string Label { get; set; }
        public event EventHandler Clicked;

        public ColorSwatchRow(string label)
        {
            Label = label;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public Color Value
        {
            get { return _value; }
            set { _value = value; Invalidate(); }
        }

        public bool Expanded
        {
            get { return _expanded; }
            set { if (_expanded != value) { _expanded = value; Invalidate(); } }
        }

        public override int GetPreferredHeight(int width) { return Theme.Px(36); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled) return;
            Focus();
            EventHandler h = Clicked;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            Gfx.Text(g, Label, Theme.Body, new Rectangle(0, 0, Width / 2, Height), Ink(Theme.Text), Theme.Surface,
                TextFormatFlags.VerticalCenter);

            // right side, laid out from the edge: chevron, hex, chip
            float chev = Theme.PxF(10);
            float cx = Width - chev / 2f - Theme.PxF(2), cy = Height / 2f;
            float dy = Theme.PxF(2.5f);
            using (Pen p = new Pen(Ink(Theme.TextSecondary), Math.Max(1.25f, Theme.PxF(1.5f))))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float dir = _expanded ? -1f : 1f;
                g.DrawLines(p, new PointF[] {
                    new PointF(cx - chev * 0.35f, cy - dy * dir),
                    new PointF(cx, cy + dy * dir),
                    new PointF(cx + chev * 0.35f, cy - dy * dir) });
            }

            string hex = ColorMath.Hex(_value);
            int hexW = Gfx.Measure(hex, Theme.Body) + Theme.Px(2);
            int hexRight = (int)(cx - chev / 2f - Theme.PxF(8));
            Gfx.Text(g, hex, Theme.Body, new Rectangle(hexRight - hexW, 0, hexW, Height), Ink(Theme.TextSecondary), Theme.Surface,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            float chip = Theme.PxF(20);
            RectangleF cr = new RectangleF(hexRight - hexW - Theme.PxF(8) - chip, (Height - chip) / 2f, chip, chip);
            Gfx.FillRound(g, _value, cr, Theme.PxF(5));
            Gfx.StrokeRound(g, Ink(Theme.TrackOff), cr, Theme.PxF(5), Math.Max(1f, Theme.PxF(1f)));

            RectangleF ring = new RectangleF(0, 0, Width, Height);
            FocusRing(g, ring, Theme.PxF(Theme.RadiusControl));
        }
    }

    // Saturation/value square, hue strip, hex box and quick swatches.
    public class ColorPickerPanel : FlyoutControl
    {
        private static readonly string[] QuickColors = new string[] { "#000000", "#FFFFFF", "#3A3A3A", "#808080", "#C8C8C8" };

        private float _h, _s, _v;
        private Bitmap _svBmp;
        private float _svHue = -1f;
        private readonly TextBox _hex = new TextBox();
        private bool _hexError, _hexDirty, _settingText;
        private int _drag; // 0 none, 1 saturation/value, 2 hue

        /// <summary>Recently used colors ("#RRGGBB"), newest first. Owned by the config.</summary>
        public System.Collections.Generic.List<string> Recents { get; set; }

        /// <summary>Raised on every change, including while dragging.</summary>
        public event EventHandler ColorChanged;
        /// <summary>Raised when an edit ends (mouse up, swatch click, Enter or leaving the hex box).</summary>
        public event EventHandler ColorCommitted;

        public ColorPickerPanel()
        {
            Recents = new System.Collections.Generic.List<string>();
            _v = 0f;
            _hex.BorderStyle = BorderStyle.None;
            _hex.MaxLength = 7;
            _hex.CharacterCasing = CharacterCasing.Upper;
            _hex.TabStop = true;
            _hex.TextChanged += OnHexTextChanged;
            _hex.KeyDown += OnHexKeyDown;
            _hex.LostFocus += OnHexLostFocus;
            Controls.Add(_hex);
            SetColorInternal(Color.Black, true);
        }

        public Color Value
        {
            get { return ColorMath.FromHsv(_h, _s, _v); }
            set
            {
                SetColorInternal(value, true);
                Invalidate();
            }
        }

        private void SetColorInternal(Color c, bool updateText)
        {
            float h, s, v;
            ColorMath.ToHsv(c, out h, out s, out v);
            if (s > 0.001f && v > 0.001f) _h = h; // keep the hue on grays and black so it doesn't jump
            _s = s;
            _v = v;
            if (updateText) SetHexText(ColorMath.Hex(c));
        }

        private void SetHexText(string text)
        {
            if (_hex.Text == text && !_hexError) return;
            _settingText = true;
            try { _hex.Text = text; }
            finally { _settingText = false; }
            _hexError = false;
        }

        public override int GetPreferredHeight(int width)
        {
            int pad = Theme.Px(8);
            return pad + Theme.Px(120) + Theme.Px(8) + Theme.Px(14) + Theme.Px(8) + Theme.Px(28) + Theme.Px(8) + Theme.Px(22) + pad;
        }

        // ---- geometry ----
        private Rectangle SvRect()
        {
            int pad = Theme.Px(8);
            return new Rectangle(pad, pad, Width - 2 * pad, Theme.Px(120));
        }

        private Rectangle HueRect()
        {
            Rectangle sv = SvRect();
            return new Rectangle(sv.X, sv.Bottom + Theme.Px(8), sv.Width, Theme.Px(14));
        }

        private Rectangle HexBox()
        {
            Rectangle hue = HueRect();
            return new Rectangle(hue.X, hue.Bottom + Theme.Px(8), Theme.Px(120), Theme.Px(28));
        }

        private int SwatchTop()
        {
            return HexBox().Bottom + Theme.Px(8);
        }

        private System.Collections.Generic.List<KeyValuePair<Rectangle, Color>> Swatches()
        {
            System.Collections.Generic.List<KeyValuePair<Rectangle, Color>> list = new System.Collections.Generic.List<KeyValuePair<Rectangle, Color>>();
            int size = Theme.Px(22), gap = Theme.Px(6);
            int x = Theme.Px(8), y = SwatchTop();
            foreach (string q in QuickColors)
            {
                list.Add(new KeyValuePair<Rectangle, Color>(new Rectangle(x, y, size, size), ColorMath.Parse(q, Color.Black)));
                x += size + gap;
            }
            x += Theme.Px(10);
            if (Recents != null)
            {
                for (int i = 0; i < Recents.Count && i < 6; i++)   // 6 = AppConfig.MaxRecentColors
                {
                    Color c;
                    if (!ColorMath.TryParse(Recents[i], out c)) continue;
                    list.Add(new KeyValuePair<Rectangle, Color>(new Rectangle(x, y, size, size), c));
                    x += size + gap;
                }
            }
            return list;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Rectangle box = HexBox();
            _hex.Font = Theme.Body;
            _hex.BackColor = Theme.Raised;
            _hex.ForeColor = Theme.Text;
            int pad = Theme.Px(8);
            _hex.Width = Math.Max(10, box.Width - 2 * pad);
            _hex.Location = new Point(box.X + pad, box.Y + (box.Height - _hex.Height) / 2);
        }

        // ---- hex box ----
        private void OnHexTextChanged(object sender, EventArgs e)
        {
            if (_settingText) return;
            _hexDirty = true;
            Color c;
            if (ColorMath.TryParse(_hex.Text, out c))
            {
                _hexError = false;
                SetColorInternal(c, false);
                Invalidate();
                RaiseChanged();
            }
            else
            {
                _hexError = true;
                Invalidate(HexBox());
            }
        }

        private void OnHexKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                CommitHex();
            }
        }

        private void OnHexLostFocus(object sender, EventArgs e)
        {
            CommitHex();
        }

        private void CommitHex()
        {
            if (!_hexDirty) return;
            _hexDirty = false;
            Color c;
            if (ColorMath.TryParse(_hex.Text, out c))
            {
                SetHexText(ColorMath.Hex(c));
                RaiseCommitted();
            }
            else
            {
                SetHexText(ColorMath.Hex(Value)); // revert to the last good color
                Invalidate();
            }
        }

        // ---- events ----
        private void RaiseChanged()
        {
            EventHandler h = ColorChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void RaiseCommitted()
        {
            EventHandler h = ColorCommitted;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ---- mouse ----
        private void UpdateFromMouse(int x, int y)
        {
            if (_drag == 1)
            {
                Rectangle r = SvRect();
                _s = Math.Max(0f, Math.Min(1f, (x - r.X) / (float)Math.Max(1, r.Width - 1)));
                _v = 1f - Math.Max(0f, Math.Min(1f, (y - r.Y) / (float)Math.Max(1, r.Height - 1)));
            }
            else if (_drag == 2)
            {
                Rectangle r = HueRect();
                _h = 360f * Math.Max(0f, Math.Min(0.9999f, (x - r.X) / (float)Math.Max(1, r.Width - 1)));
            }
            else return;
            SetHexText(ColorMath.Hex(Value));
            _hexDirty = false;
            Invalidate();
            RaiseChanged();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Rectangle sv = SvRect();
            Rectangle hue = HueRect();
            hue.Inflate(0, Theme.Px(4));
            if (sv.Contains(e.Location)) _drag = 1;
            else if (hue.Contains(e.Location)) _drag = 2;
            else
            {
                foreach (KeyValuePair<Rectangle, Color> sw in Swatches())
                {
                    if (sw.Key.Contains(e.Location))
                    {
                        SetColorInternal(sw.Value, true);
                        _hexDirty = false;
                        Invalidate();
                        RaiseChanged();
                        RaiseCommitted();
                        return;
                    }
                }
                return;
            }
            Capture = true;
            UpdateFromMouse(e.X, e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag != 0) UpdateFromMouse(e.X, e.Y);
            else
            {
                bool over = false;
                foreach (KeyValuePair<Rectangle, Color> sw in Swatches())
                    if (sw.Key.Contains(e.Location)) { over = true; break; }
                Cursor = (over || SvRect().Contains(e.Location) || HueRect().Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag != 0)
            {
                _drag = 0;
                Capture = false;
                RaiseCommitted();
            }
        }

        // ---- painting ----
        private void EnsureSvBitmap()
        {
            if (_svBmp != null && Math.Abs(_svHue - _h) < 0.01f) return;
            const int W = 96, H = 64;
            if (_svBmp == null) _svBmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
            Color hc = ColorMath.FromHsv(_h, 1f, 1f);
            BitmapData bd = _svBmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte[] row = new byte[W * 4];
                for (int y = 0; y < H; y++)
                {
                    float v = 1f - y / (float)(H - 1);
                    for (int x = 0; x < W; x++)
                    {
                        float s = x / (float)(W - 1);
                        row[x * 4 + 0] = (byte)Math.Round(v * ((1f - s) * 255f + s * hc.B));
                        row[x * 4 + 1] = (byte)Math.Round(v * ((1f - s) * 255f + s * hc.G));
                        row[x * 4 + 2] = (byte)Math.Round(v * ((1f - s) * 255f + s * hc.R));
                        row[x * 4 + 3] = 255;
                    }
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row.Length);
                }
            }
            finally
            {
                _svBmp.UnlockBits(bd);
            }
            _svHue = _h;
        }

        private void Thumb(Graphics g, float cx, float cy, Color fill)
        {
            float d = Theme.PxF(14);
            Gfx.FillCircle(g, fill, cx, cy, d);
            using (Pen dark = new Pen(Color.FromArgb(140, 0, 0, 0), Math.Max(1f, Theme.PxF(1f))))
                g.DrawEllipse(dark, cx - d / 2f - Theme.PxF(1f), cy - d / 2f - Theme.PxF(1f), d + Theme.PxF(2f), d + Theme.PxF(2f));
            using (Pen white = new Pen(Color.White, Math.Max(1.5f, Theme.PxF(2f))))
                g.DrawEllipse(white, cx - d / 2f, cy - d / 2f, d, d);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Smooth(g);
            RectangleF all = new RectangleF(0, 0, Width, Height);
            Gfx.StrokeRound(g, Ink(Theme.Border), all, Theme.PxF(Theme.RadiusSegment), Math.Max(1f, Theme.PxF(1f)));

            // saturation / value square
            Rectangle sv = SvRect();
            EnsureSvBitmap();
            GraphicsState st = g.Save();
            using (GraphicsPath clip = Gfx.RoundRect(sv, Theme.PxF(6)))
            {
                g.SetClip(clip, CombineMode.Replace);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_svBmp, sv);
            }
            g.Restore(st);
            Smooth(g);
            Gfx.StrokeRound(g, Theme.Border, sv, Theme.PxF(6), Math.Max(1f, Theme.PxF(1f)));
            float sx = sv.X + _s * (sv.Width - 1), sy = sv.Y + (1f - _v) * (sv.Height - 1);
            Thumb(g, sx, sy, Value);

            // hue strip
            Rectangle hue = HueRect();
            using (GraphicsPath p = Gfx.RoundRect(hue, hue.Height / 2f))
            using (LinearGradientBrush br = new LinearGradientBrush(hue, Color.Red, Color.Red, 0f))
            {
                ColorBlend cb = new ColorBlend(7);
                cb.Colors = new Color[] { Color.FromArgb(255, 0, 0), Color.FromArgb(255, 255, 0), Color.FromArgb(0, 255, 0),
                                          Color.FromArgb(0, 255, 255), Color.FromArgb(0, 0, 255), Color.FromArgb(255, 0, 255), Color.FromArgb(255, 0, 0) };
                cb.Positions = new float[] { 0f, 1f / 6f, 2f / 6f, 3f / 6f, 4f / 6f, 5f / 6f, 1f };
                br.InterpolationColors = cb;
                g.FillPath(br, p);
            }
            float hx = hue.X + (_h / 360f) * (hue.Width - 1);
            Thumb(g, hx, hue.Y + hue.Height / 2f, ColorMath.FromHsv(_h, 1f, 1f));

            // hex box
            Rectangle box = HexBox();
            Gfx.FillRound(g, Theme.Raised, box, Theme.PxF(Theme.RadiusControl));
            Gfx.StrokeRound(g, _hexError ? Theme.Error : Theme.Border, box, Theme.PxF(Theme.RadiusControl),
                _hexError ? Math.Max(1.5f, Theme.PxF(1.5f)) : Math.Max(1f, Theme.PxF(1f)));
            Rectangle hint = new Rectangle(box.Right + Theme.Px(8), box.Y, Math.Max(0, Width - box.Right - Theme.Px(16)), box.Height);
            Gfx.Text(g, _hexError ? "Use #RRGGBB" : "Hex", Theme.Caption, hint, _hexError ? Theme.Error : Theme.TextSecondary,
                Theme.Surface, TextFormatFlags.VerticalCenter);

            // quick + recent swatches
            Color cur = Value;
            foreach (KeyValuePair<Rectangle, Color> sw in Swatches())
            {
                RectangleF r = sw.Key;
                Gfx.FillRound(g, sw.Value, r, Theme.PxF(5));
                bool selected = sw.Value.ToArgb() == cur.ToArgb();
                if (selected)
                {
                    RectangleF ring = r;
                    ring.Inflate(Theme.PxF(2), Theme.PxF(2));
                    Gfx.StrokeRound(g, Theme.Text, ring, Theme.PxF(7), Math.Max(1.5f, Theme.PxF(1.5f)));
                }
                else
                {
                    Gfx.StrokeRound(g, Theme.TrackOff, r, Theme.PxF(5), Math.Max(1f, Theme.PxF(1f)));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _svBmp != null)
            {
                _svBmp.Dispose();
                _svBmp = null;
            }
            base.Dispose(disposing);
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
    // Tray icon: the logo embedded by the build (assets/tray-black.ico on light taskbars,
    // assets/tray-white.ico on dark ones). Black is the fallback whenever the right one is missing;
    // a drawn glyph is the last resort. Normal style shows the icon dimmed.
    // ------------------------------------------------------------------------------------
    public static class IconFactory
    {
        public static Icon CreateTrayIcon(bool active, bool lightTaskbar)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            Icon custom = lightTaskbar ? null : LoadEmbedded("tray-white.ico", size);
            if (custom == null) custom = LoadEmbedded("tray-black.ico", size);
            if (custom != null)
            {
                if (active) return custom;
                using (custom) return Dim(custom, size);
            }
            return DrawGlyph(active, lightTaskbar, size);
        }

        /// <summary>An icon resource embedded by build.ps1 (assets/*.ico), at the requested size, or null.</summary>
        public static Icon LoadEmbedded(string name, int size)
        {
            try
            {
                using (System.IO.Stream s = typeof(IconFactory).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    return new Icon(s, size, size);
                }
            }
            catch { return null; }
        }

        private static Icon Dim(Icon src, int size)
        {
            using (Bitmap bmp = src.ToBitmap())
            using (Bitmap outBmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(outBmp))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = 0.45f;
                    ia.SetColorMatrix(cm);
                    g.DrawImage(bmp, new Rectangle(0, 0, size, size), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                }
                return ToIcon(outBmp);
            }
        }

        private static Icon ToIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(h);
            }
        }

        private static Icon DrawGlyph(bool enabled, bool lightTaskbar, int size)
        {
            Color ink = lightTaskbar ? Color.FromArgb(0x11, 0x11, 0x11) : Color.White;
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
                return ToIcon(bmp);
            }
        }
    }
}



