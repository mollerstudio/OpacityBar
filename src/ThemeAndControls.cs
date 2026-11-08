using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Opacbar
{
    public static class BrandTheme
    {
        // Strict Monochrome Palette
        public static Color GetBgPrimary(bool light) { return light ? Color.FromArgb(250, 250, 250) : Color.FromArgb(10, 10, 10); }
        public static Color GetBgCard(bool light) { return light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(18, 18, 18); }
        public static Color GetBgElevated(bool light) { return light ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 28); }
        public static Color GetBorder(bool light) { return light ? Color.FromArgb(218, 218, 218) : Color.FromArgb(42, 42, 42); }
        public static Color GetBorderStrong(bool light) { return light ? Color.FromArgb(20, 20, 20) : Color.FromArgb(245, 245, 245); }
        public static Color GetTextPrimary(bool light) { return light ? Color.FromArgb(12, 12, 12) : Color.FromArgb(245, 245, 245); }
        public static Color GetTextSecondary(bool light) { return light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(145, 145, 145); }
        public static Color GetTextMuted(bool light) { return light ? Color.FromArgb(155, 155, 155) : Color.FromArgb(95, 95, 95); }

        // Only Semantic Accent Colors: Success (Green), Warning (Yellow), Error (Red)
        public static readonly Color AccentSuccess = Color.FromArgb(34, 197, 94);   // #22C55E
        public static readonly Color AccentWarning = Color.FromArgb(234, 179, 8);   // #EAB308
        public static readonly Color AccentError = Color.FromArgb(239, 68, 68);     // #EF4444

        public static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Icon CreateMonochromeAppIcon(bool active)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Outer monochrome rounded square
                Rectangle outer = new Rectangle(2, 2, 28, 28);
                using (GraphicsPath path = CreateRoundedRect(outer, 7))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(15, 15, 15)))
                using (Pen border = new Pen(Color.FromArgb(240, 240, 240), 2f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                // Floating pill taskbar motif at the bottom of the icon
                Rectangle pill = new Rectangle(6, 20, 20, 5);
                using (GraphicsPath pillPath = CreateRoundedRect(pill, 2))
                using (SolidBrush pillBrush = new SolidBrush(Color.FromArgb(245, 245, 245)))
                {
                    g.FillPath(pillBrush, pillPath);
                }

                // Status dot in top-right of icon (Green = Active, Yellow = Paused)
                Color dotColor = active ? AccentSuccess : AccentWarning;
                using (SolidBrush dotBrush = new SolidBrush(dotColor))
                {
                    g.FillEllipse(dotBrush, 20, 7, 5, 5);
                }

                IntPtr hIcon = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                NativeMethods.DestroyIcon(hIcon);
                return icon;
            }
        }
    }

    // Custom Monochrome Toggle Switch
    public class OpacSwitch : Control
    {
        private bool _checked;
        private bool _lightTheme;
        private bool _useSemanticGreen = false;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    Invalidate();
                    if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
                }
            }
        }

        public bool UseSemanticGreen
        {
            get { return _useSemanticGreen; }
            set { _useSemanticGreen = value; Invalidate(); }
        }

        public bool LightTheme
        {
            get { return _lightTheme; }
            set { _lightTheme = value; Invalidate(); }
        }

        public OpacSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(44, 24);
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = new Rectangle(1, 1, Width - 3, Height - 3);
            Color trackFill;
            Color thumbColor;
            Color borderColor;

            if (_checked)
            {
                if (_useSemanticGreen)
                {
                    trackFill = BrandTheme.AccentSuccess;
                    borderColor = BrandTheme.AccentSuccess;
                    thumbColor = Color.FromArgb(10, 10, 10);
                }
                else
                {
                    trackFill = _lightTheme ? Color.FromArgb(15, 15, 15) : Color.FromArgb(245, 245, 245);
                    borderColor = trackFill;
                    thumbColor = _lightTheme ? Color.FromArgb(250, 250, 250) : Color.FromArgb(12, 12, 12);
                }
            }
            else
            {
                trackFill = _lightTheme ? Color.FromArgb(228, 228, 228) : Color.FromArgb(30, 30, 30);
                borderColor = _lightTheme ? Color.FromArgb(195, 195, 195) : Color.FromArgb(62, 62, 62);
                thumbColor = _lightTheme ? Color.FromArgb(110, 110, 110) : Color.FromArgb(140, 140, 140);
            }

            using (GraphicsPath path = BrandTheme.CreateRoundedRect(track, track.Height / 2))
            using (SolidBrush fill = new SolidBrush(trackFill))
            using (Pen pen = new Pen(borderColor, 1.2f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            int pad = 4;
            int thumbSize = Height - (pad * 2) - 2;
            int thumbX = _checked ? (Width - thumbSize - pad - 2) : pad;
            Rectangle thumbRect = new Rectangle(thumbX, pad, thumbSize, thumbSize);

            using (SolidBrush thumbBrush = new SolidBrush(thumbColor))
            {
                g.FillEllipse(thumbBrush, thumbRect);
            }
        }
    }

    // Custom Monochrome Slider
    public class OpacSlider : Control
    {
        private int _minimum = 0;
        private int _maximum = 100;
        private int _value = 50;
        private bool _dragging = false;
        private bool _lightTheme = false;

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = Math.Max(_minimum + 1, value); Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_minimum, Math.Min(_maximum, value));
                if (_value != clamped)
                {
                    _value = clamped;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        public bool LightTheme
        {
            get { return _lightTheme; }
            set { _lightTheme = value; Invalidate(); }
        }

        public OpacSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 26;
            Cursor = Cursors.Hand;
        }

        private void UpdateFromMouse(int mouseX)
        {
            int pad = 9;
            int trackWidth = Math.Max(1, Width - pad * 2);
            double ratio = (double)(mouseX - pad) / trackWidth;
            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
            Value = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                UpdateFromMouse(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                UpdateFromMouse(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int pad = 9;
            int trackY = Height / 2 - 2;
            int trackWidth = Math.Max(1, Width - pad * 2);
            Rectangle trackRect = new Rectangle(pad, trackY, trackWidth, 4);

            Color bgTrack = _lightTheme ? Color.FromArgb(220, 220, 220) : Color.FromArgb(42, 42, 42);
            Color fgTrack = _lightTheme ? Color.FromArgb(18, 18, 18) : Color.FromArgb(245, 245, 245);

            using (GraphicsPath bgPath = BrandTheme.CreateRoundedRect(trackRect, 2))
            using (SolidBrush bgBrush = new SolidBrush(bgTrack))
            {
                g.FillPath(bgBrush, bgPath);
            }

            double ratio = (double)(_value - _minimum) / Math.Max(1, _maximum - _minimum);
            int activeWidth = (int)Math.Round(ratio * trackWidth);
            if (activeWidth > 0)
            {
                Rectangle activeRect = new Rectangle(pad, trackY, Math.Max(4, activeWidth), 4);
                using (GraphicsPath fgPath = BrandTheme.CreateRoundedRect(activeRect, 2))
                using (SolidBrush fgBrush = new SolidBrush(fgTrack))
                {
                    g.FillPath(fgBrush, fgPath);
                }
            }

            int thumbX = pad + activeWidth - 7;
            int thumbY = Height / 2 - 7;
            Rectangle thumbRect = new Rectangle(thumbX, thumbY, 14, 14);

            Color thumbFill = _lightTheme ? Color.FromArgb(15, 15, 15) : Color.FromArgb(250, 250, 250);
            Color thumbBorder = _lightTheme ? Color.FromArgb(255, 255, 255) : Color.FromArgb(12, 12, 12);

            using (SolidBrush fill = new SolidBrush(thumbFill))
            using (Pen pen = new Pen(thumbBorder, 2f))
            {
                g.FillEllipse(fill, thumbRect);
                g.DrawEllipse(pen, thumbRect);
            }
        }
    }

    // Monochrome Segmented Pill Selector
    public class OpacSegmentedSelector : Control
    {
        private string[] _items = new string[0];
        private int _selectedIndex = 0;
        private int _hoverIndex = -1;
        private bool _lightTheme = false;

        public event EventHandler SelectedIndexChanged;

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                int clamped = Math.Max(0, Math.Min(_items.Length - 1, value));
                if (_selectedIndex != clamped)
                {
                    _selectedIndex = clamped;
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        public bool LightTheme
        {
            get { return _lightTheme; }
            set { _lightTheme = value; Invalidate(); }
        }

        public OpacSegmentedSelector()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 34;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Regular);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_items.Length == 0) return;
            int segWidth = Width / _items.Length;
            int idx = Math.Max(0, Math.Min(_items.Length - 1, e.X / Math.Max(1, segWidth)));
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && _items.Length > 0)
            {
                int segWidth = Width / _items.Length;
                int idx = Math.Max(0, Math.Min(_items.Length - 1, e.X / Math.Max(1, segWidth)));
                SelectedIndex = idx;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle outer = new Rectangle(0, 0, Width - 1, Height - 1);
            Color outerBg = _lightTheme ? Color.FromArgb(236, 236, 236) : Color.FromArgb(24, 24, 24);
            Color outerBorder = BrandTheme.GetBorder(_lightTheme);

            using (GraphicsPath path = BrandTheme.CreateRoundedRect(outer, 7))
            using (SolidBrush bg = new SolidBrush(outerBg))
            using (Pen pen = new Pen(outerBorder, 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }

            if (_items == null || _items.Length == 0) return;

            float segW = (float)(Width - 6) / _items.Length;
            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle segRect = new Rectangle(
                    3 + (int)Math.Round(i * segW),
                    3,
                    (int)Math.Round(segW),
                    Height - 6);

                bool isSelected = (i == _selectedIndex);
                bool isHovered = (i == _hoverIndex && !isSelected);

                if (isSelected)
                {
                    Color activeBg = _lightTheme ? Color.FromArgb(15, 15, 15) : Color.FromArgb(245, 245, 245);
                    using (GraphicsPath segPath = BrandTheme.CreateRoundedRect(segRect, 5))
                    using (SolidBrush activeBrush = new SolidBrush(activeBg))
                    {
                        g.FillPath(activeBrush, segPath);
                    }
                }
                else if (isHovered)
                {
                    Color hoverBg = _lightTheme ? Color.FromArgb(224, 224, 224) : Color.FromArgb(36, 36, 36);
                    using (GraphicsPath segPath = BrandTheme.CreateRoundedRect(segRect, 5))
                    using (SolidBrush hoverBrush = new SolidBrush(hoverBg))
                    {
                        g.FillPath(hoverBrush, segPath);
                    }
                }

                Color textColor;
                if (isSelected)
                {
                    textColor = _lightTheme ? Color.FromArgb(250, 250, 250) : Color.FromArgb(12, 12, 12);
                }
                else
                {
                    textColor = BrandTheme.GetTextSecondary(_lightTheme);
                }

                TextRenderer.DrawText(
                    g,
                    _items[i],
                    Font,
                    segRect,
                    textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Live Interactive Taskbar Visualizer Canvas
    public class LiveTaskbarVisualizer : Control
    {
        private AppConfig _config;
        private DynamicActivityState _previewState = DynamicActivityState.Desktop;

        public LiveTaskbarVisualizer(AppConfig config)
        {
            _config = config;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = 96;
        }

        public void UpdatePreview(AppConfig config, DynamicActivityState state)
        {
            _config = config;
            _previewState = state;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool light = _config != null && _config.UILightTheme;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            // Simulated monochrome desktop wallpaper with geometric grid so transparency/blur is clearly visible
            using (GraphicsPath cardPath = BrandTheme.CreateRoundedRect(bounds, 10))
            {
                g.SetClip(cardPath);

                using (LinearGradientBrush wallBrush = new LinearGradientBrush(
                    bounds,
                    light ? Color.FromArgb(226, 226, 226) : Color.FromArgb(22, 22, 22),
                    light ? Color.FromArgb(205, 205, 205) : Color.FromArgb(12, 12, 12),
                    35f))
                {
                    g.FillRectangle(wallBrush, bounds);
                }

                // Subtle geometric grid lines on simulated desktop
                using (Pen gridPen = new Pen(light ? Color.FromArgb(25, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255), 1f))
                {
                    for (int x = 20; x < Width; x += 28)
                        g.DrawLine(gridPen, x, 0, x, Height);
                    for (int y = 16; y < Height; y += 24)
                        g.DrawLine(gridPen, 0, y, Width, y);
                }

                // Decorative monochrome circles on simulated desktop so user can see glass/clear vs solid
                using (SolidBrush orb1 = new SolidBrush(light ? Color.FromArgb(45, 20, 20, 20) : Color.FromArgb(40, 255, 255, 255)))
                using (SolidBrush orb2 = new SolidBrush(light ? Color.FromArgb(30, 20, 20, 20) : Color.FromArgb(25, 255, 255, 255)))
                {
                    g.FillEllipse(orb1, Width / 2 - 70, 28, 54, 54);
                    g.FillEllipse(orb2, Width / 2 + 25, 36, 42, 42);
                }

                // Label top-left: LIVE PREVIEW
                using (Font badgeFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold))
                {
                    string previewTitle = "LIVE GEOMETRY & OPACITY PREVIEW";
                    TextRenderer.DrawText(
                        g,
                        previewTitle,
                        badgeFont,
                        new Point(12, 9),
                        light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(150, 150, 150));
                }

                // Simulated Taskbar at bottom of preview
                if (_config != null)
                {
                    StyleProfile prof = _config.DesktopProfile;
                    if (_previewState == DynamicActivityState.MaximizedWindow && _config.EnableMaximizedState)
                        prof = _config.MaximizedProfile;
                    else if (_previewState == DynamicActivityState.StartOrSearchOpen && _config.EnableStartMenuState)
                        prof = _config.StartMenuProfile;
                    else if (_previewState == DynamicActivityState.BatterySaver && _config.EnableBatterySaverState)
                        prof = _config.BatterySaverProfile;

                    bool pill = _config.FloatingPillEnabled;
                    int marginX = pill ? Math.Min(120, (int)Math.Round(_config.HorizontalMargin * 0.45) + 12) : 0;
                    int marginY = pill ? Math.Min(10, _config.VerticalMargin + 4) : 0;
                    int radius = pill ? Math.Min(17, (int)Math.Round(_config.CornerRadius * 0.55)) : 0;

                    int barHeight = 32;
                    Rectangle barRect = new Rectangle(
                        marginX + 1,
                        Height - barHeight - marginY - 1,
                        Width - (marginX * 2) - 3,
                        barHeight);

                    int alpha = (int)Math.Round((prof.Opacity / 100.0) * 255.0);
                    if (prof.Mode == TaskbarStyleMode.Clear) alpha = 0;
                    else if (prof.Mode == TaskbarStyleMode.Solid) alpha = 255;
                    else if (prof.Mode == TaskbarStyleMode.Normal) alpha = 230;

                    int lum = prof.Luminance;
                    Color barFill = Color.FromArgb(alpha, lum, lum, lum);

                    using (GraphicsPath barPath = BrandTheme.CreateRoundedRect(barRect, radius))
                    {
                        if (alpha > 0)
                        {
                            using (SolidBrush bBrush = new SolidBrush(barFill))
                            {
                                g.FillPath(bBrush, barPath);
                            }
                        }

                        // Simulated Acrylic noise/frosted sheen if Acrylic or Blur
                        if (prof.Mode == TaskbarStyleMode.Acrylic || prof.Mode == TaskbarStyleMode.Blur)
                        {
                            using (HatchBrush hatch = new HatchBrush(
                                HatchStyle.Percent20,
                                Color.FromArgb(28, 255, 255, 255),
                                Color.Transparent))
                            {
                                g.FillPath(hatch, barPath);
                            }
                        }

                        // Subtle outline so even 100% Clear is visible in preview
                        Color barStroke = prof.ShowTopLine
                            ? Color.FromArgb(190, 240, 240, 240)
                            : Color.FromArgb(75, 160, 160, 160);
                        using (Pen strokePen = new Pen(barStroke, 1f))
                        {
                            g.DrawPath(strokePen, barPath);
                        }
                    }

                    // Simulated centered taskbar app icons (monochrome squares/circles)
                    int iconCount = 5;
                    int iconSize = 12;
                    int iconGap = 10;
                    int totalIconsW = iconCount * iconSize + (iconCount - 1) * iconGap;
                    int startX = barRect.X + (barRect.Width - totalIconsW) / 2;
                    int iconY = barRect.Y + (barRect.Height - iconSize) / 2;

                    Color iconColor = (lum > 150 && alpha > 120)
                        ? Color.FromArgb(220, 20, 20, 20)
                        : Color.FromArgb(235, 245, 245, 245);

                    using (SolidBrush iconBrush = new SolidBrush(iconColor))
                    {
                        for (int i = 0; i < iconCount; i++)
                        {
                            Rectangle ic = new Rectangle(startX + i * (iconSize + iconGap), iconY, iconSize, iconSize);
                            using (GraphicsPath icPath = BrandTheme.CreateRoundedRect(ic, 3))
                            {
                                g.FillPath(iconBrush, icPath);
                            }
                        }
                    }
                }

                g.ResetClip();

                using (Pen borderPen = new Pen(BrandTheme.GetBorder(light), 1f))
                {
                    g.DrawPath(borderPen, cardPath);
                }
            }
        }
    }

    internal static class DynamicStateExtensions
    {
        public static DynamicActivityState StartMenuProfile_Check(this DynamicActivityState s, DynamicActivityState current)
        {
            return DynamicActivityState.StartOrSearchOpen;
        }
    }

    // Monochrome Context Menu Renderer for System Tray
    public class MonochromeMenuRenderer : ToolStripProfessionalRenderer
    {
        private bool _light;

        public MonochromeMenuRenderer(bool light)
        {
            _light = light;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(BrandTheme.GetBgCard(_light)))
            {
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(BrandTheme.GetBorder(_light), 1f))
            {
                e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle r = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
            if (e.Item.Selected && e.Item.Enabled)
            {
                Color sel = _light ? Color.FromArgb(232, 232, 232) : Color.FromArgb(36, 36, 36);
                using (GraphicsPath path = BrandTheme.CreateRoundedRect(r, 5))
                using (SolidBrush b = new SolidBrush(sel))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(b, path);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? BrandTheme.GetTextPrimary(_light)
                : BrandTheme.GetTextMuted(_light);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(BrandTheme.GetBorder(_light), 1f))
            {
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(e.ImageRectangle.X + 3, e.ImageRectangle.Y + 3, 10, 10);
            using (SolidBrush b = new SolidBrush(BrandTheme.AccentSuccess))
            {
                e.Graphics.FillEllipse(b, r);
            }
        }
    }
}
