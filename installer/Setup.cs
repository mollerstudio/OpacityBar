// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace OpacityBar.Setup
{
    /// <summary>
    /// OpacityBarSetup.exe: a one-window, per-user installer. The app files are embedded as resources
    /// ("payload/..."), copied to %LOCALAPPDATA%\Programs\OpacityBar, and registered in Settings &gt; Apps
    /// (uninstall runs "OpacityBar.exe --uninstall"). No admin rights, no network.
    ///   /silent   install without UI (keeps the current Start with Windows choice; on for new installs)
    /// </summary>
    public static class SetupProgram
    {
        public static readonly string[] Payload = { AppInfo.ExeName, AppInfo.HelperPrefix + "x64.dll", AppInfo.HelperPrefix + "arm64.dll" };

        [STAThread]
        public static int Main(string[] args)
        {
            bool silent = false;
            foreach (string a in args)
                if (string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                    silent = true;

            if (silent)
            {
                string err;
                bool startup = Lifecycle.InstalledVersion() == null || Lifecycle.IsRunAtStartup();
                if (!Installer.Run(startup, null, out err)) return 1;
                // Installing closed any running copy: start it again, quietly in the tray.
                try { Process.Start(Path.Combine(AppInfo.InstallDir, AppInfo.ExeName), "--minimized").Dispose(); } catch { }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.SetDpi(UiNative.SystemDpi());
            Theme.Apply(0);
            Application.Run(new SetupForm());
            return 0;
        }
    }

    /// <summary>The install steps (no UI). Safe to run again: it is also the update and repair path.</summary>
    public static class Installer
    {
        public static bool Run(bool startWithWindows, Action<int, string> progress, out string error)
        {
            error = null;
            try
            {
                Report(progress, 5, "Closing " + AppInfo.Name + "...");
                Lifecycle.QuitRunning(8000);

                if (Lifecycle.LegacyInstalled())
                {
                    Report(progress, 20, "Removing the old Opacbar...");
                    Lifecycle.RemoveLegacyInstall();
                }

                Lifecycle.CancelSignInCleanup();
                Report(progress, 40, "Copying files...");
                string dir = AppInfo.InstallDir;
                Directory.CreateDirectory(dir);
                long size = 0;
                foreach (string name in SetupProgram.Payload)
                    size += Extract(name, Path.Combine(dir, name));

                Report(progress, 75, "Finishing...");
                string exe = Path.Combine(dir, AppInfo.ExeName);
                try { Lifecycle.CreateShortcut(AppInfo.StartMenuShortcut, exe, "Make your taskbar clear, colored or blurred"); }
                catch { } // a missing shortcut is not worth failing the install for
                Lifecycle.WriteUninstallEntry(dir, size);
                Lifecycle.SetRunAtStartup(exe, startWithWindows);

                Report(progress, 100, "Done");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void Report(Action<int, string> progress, int pct, string text)
        {
            if (progress != null) progress(pct, text);
        }

        /// <summary>Writes one embedded file (via a temp file, retried while the old one is still locked).</summary>
        private static long Extract(string name, string dest)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + name))
            {
                if (s == null) throw new InvalidOperationException("This setup is incomplete (" + name + " is missing).");
                string tmp = dest + ".new";
                using (FileStream f = File.Create(tmp)) s.CopyTo(f);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(dest))
                        {
                            // Explorer keeps the loaded helper DLL locked, but a locked file can still be
                            // renamed: move it aside (the app deletes "*.old" files once they're free).
                            try { File.Delete(dest); }
                            catch (Exception) { File.Move(dest, dest + "." + DateTime.Now.Ticks + ".old"); }
                        }
                        File.Move(tmp, dest);
                        break;
                    }
                    catch (Exception)
                    {
                        if (attempt >= 20) throw new IOException(name + " is in use. Close " + AppInfo.Name + " and try again.");
                        Thread.Sleep(250);
                    }
                }
                return new FileInfo(dest).Length;
            }
        }
    }

    /// <summary>The installer window.</summary>
    public class SetupForm : Form
    {
        private enum Stage { Ready, Working, Done, Failed }

        private const int CS_DROPSHADOW = 0x00020000;
        private const int LogicalW = 400, LogicalH = 400;

        private readonly string _installed = Lifecycle.InstalledVersion();
        private readonly Row _startupRow;
        private readonly ToggleSwitch _startup = new ToggleSwitch();
        private readonly FlatButton _button = new FlatButton("Install");
        private Stage _stage = Stage.Ready;
        private string _message;
        private int _progress;
        private bool _closeHover;
        private Image _logo;

        public SetupForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Text = AppInfo.Name + " Setup";
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            KeyPreview = true;
            ClientSize = new Size(Theme.Px(LogicalW), Theme.Px(LogicalH));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _logo = LoadLogo();

            _startup.Checked = _installed == null || Lifecycle.IsRunAtStartup();
            _startupRow = new Row("Start with Windows", _startup);
            _button.Primary = true;
            _button.Caption = _installed == null ? "Install"
                : AppInfo.CompareVersions(AppInfo.Version, _installed) > 0 ? "Update" : "Reinstall";
            _button.Click += delegate { OnButton(); };

            Controls.Add(_startupRow);
            Controls.Add(_button);
            LayoutControls();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundCorners(Handle);
            Theme.SetBorderColor(Handle, Theme.Border);
        }

        /// <summary>White logo on the dark theme, black on light; black whenever the white one is missing.</summary>
        private static Image LoadLogo()
        {
            string[] names = Theme.Dark ? new[] { "logo-white.png", "logo-black.png" } : new[] { "logo-black.png" };
            foreach (string name in names)
            {
                try
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                        if (s != null) return new Bitmap(s);
                }
                catch { }
            }
            return null;
        }

        private void LayoutControls()
        {
            int w = Theme.Px(300), x = (ClientSize.Width - w) / 2;
            _startupRow.SetBounds(x, Theme.Px(250), w, _startupRow.GetPreferredHeight(w));
            _button.SetBounds(x, Theme.Px(304), w, Theme.Px(40));
            _startupRow.Visible = _stage == Stage.Ready || _stage == Stage.Failed;
        }

        // ---- Actions ----------------------------------------------------------------

        private void OnButton()
        {
            if (_stage == Stage.Done)
            {
                Close();
                return;
            }
            if (_stage == Stage.Working) return;

            _stage = Stage.Working;
            _button.Caption = _installed == null ? "Installing..." : "Updating...";
            _button.Enabled = false;
            _progress = 0;
            _message = null;
            LayoutControls();
            Invalidate();

            bool startup = _startup.Checked;
            Thread t = new Thread(delegate()
            {
                string err;
                bool ok = Installer.Run(startup, delegate(int pct, string text)
                {
                    Post(delegate { _progress = pct; _message = text; Invalidate(); });
                }, out err);
                if (ok) LaunchApp();
                Post(delegate { Finish(ok, err); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private static void LaunchApp()
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(AppInfo.InstallDir, AppInfo.ExeName)) { UseShellExecute = true }).Dispose(); }
            catch { }
        }

        private void Finish(bool ok, string err)
        {
            _stage = ok ? Stage.Done : Stage.Failed;
            _message = ok ? AppInfo.Name + " is running. Find it in the tray, next to the clock." : err;
            _button.Caption = ok ? "Done" : "Try again";
            _button.Enabled = true;
            LayoutControls();
            Invalidate();
        }

        private void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        // ---- Painting ---------------------------------------------------------------

        private Rectangle CloseRect
        {
            get { int s = Theme.Px(32); return new Rectangle(ClientSize.Width - s - Theme.Px(8), Theme.Px(8), s, s); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int W = ClientSize.Width;

            // Close button
            Rectangle cr = CloseRect;
            if (_closeHover) Gfx.FillRound(g, Theme.Raised, cr, Theme.PxF(Theme.RadiusControl));
            using (Font f = new Font("Segoe MDL2 Assets", Theme.PxF(11), FontStyle.Regular, GraphicsUnit.Pixel))
                Gfx.Text(g, "", f, cr, Theme.TextSecondary, _closeHover ? Theme.Raised : Theme.Surface,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // Logo
            int ls = Theme.Px(80);
            Rectangle lr = new Rectangle((W - ls) / 2, Theme.Px(52), ls, ls);
            if (_logo != null) g.DrawImage(_logo, lr);
            else DrawGlyphLogo(g, lr);

            // Title + subtitle
            using (Font title = new Font(Theme.Title.FontFamily, Theme.PxF(26), FontStyle.Bold, GraphicsUnit.Pixel))
                Gfx.Text(g, AppInfo.Name, title, new Rectangle(0, Theme.Px(148), W, Theme.Px(36)), Theme.Text, Theme.Surface,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            string sub;
            Color subColor = Theme.TextSecondary;
            switch (_stage)
            {
                case Stage.Working: sub = _message ?? ""; break;
                case Stage.Done: sub = _message; break;
                case Stage.Failed: sub = _message; subColor = Theme.Error; break;
                default:
                    if (_installed == null) sub = "Make your taskbar clear, colored or blurred.";
                    else if (AppInfo.CompareVersions(AppInfo.Version, _installed) > 0) sub = "Update from " + _installed + " to " + AppInfo.Version + ".";
                    else sub = "Version " + _installed + " is installed. Reinstall to repair it.";
                    break;
            }
            Rectangle sr = new Rectangle(Theme.Px(24), Theme.Px(188), W - Theme.Px(48), Theme.Px(44));
            TextRenderer.DrawText(g, sub, Theme.Body, sr, subColor, Theme.Surface,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            // Progress
            if (_stage == Stage.Working)
            {
                int pw = Theme.Px(300);
                RectangleF track = new RectangleF((W - pw) / 2f, Theme.Px(266), pw, Theme.PxF(4));
                Gfx.FillRound(g, Theme.Raised, track, track.Height / 2f);
                RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, pw * _progress / 100f), track.Height);
                Gfx.FillRound(g, Theme.Text, fill, track.Height / 2f);
            }

            // Footer
            if (_stage == Stage.Done) _footer = "You can uninstall it any time from Settings > Apps.";
            Gfx.Text(g, _footer, Theme.Small, new Rectangle(0, ClientSize.Height - Theme.Px(36), W, Theme.Px(20)),
                Theme.TextSecondary, Theme.Surface, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private string _footer = "Version " + AppInfo.Version + " · by " + AppInfo.Publisher + " · open source, no admin rights";

        /// <summary>Placeholder mark until assets/logo.png exists: a rounded tile with a half-filled bar.</summary>
        private static void DrawGlyphLogo(Graphics g, Rectangle r)
        {
            Gfx.FillRound(g, Theme.Text, r, r.Width * 0.24f);
            float bw = r.Width * 0.64f, bh = r.Height * 0.22f;
            RectangleF bar = new RectangleF(r.X + (r.Width - bw) / 2f, r.Y + (r.Height - bh) / 2f, bw, bh);
            Gfx.StrokeRound(g, Theme.Surface, bar, bh / 2f, Math.Max(2f, r.Width / 26f));
            float inset = Math.Max(2f, r.Width / 26f) + r.Width / 40f;
            RectangleF half = new RectangleF(bar.X + inset, bar.Y + inset, (bw - 2 * inset) * 0.5f, bh - 2 * inset);
            Gfx.FillRound(g, Theme.Surface, half, half.Height / 2f);
        }

        // ---- Window behavior ----------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = CloseRect.Contains(e.Location);
            if (h != _closeHover) { _closeHover = h; Invalidate(CloseRect); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_closeHover) { _closeHover = false; Invalidate(CloseRect); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect.Contains(e.Location))
            {
                if (_stage != Stage.Working) Close();
                return;
            }
            // Drag the window by any empty area.
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HT_CAPTION, IntPtr.Zero);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _stage != Stage.Working) { Close(); return true; }
            if (keyData == Keys.Enter) { OnButton(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_stage == Stage.Working && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _logo != null) _logo.Dispose();
            base.Dispose(disposing);
        }
    }
}
