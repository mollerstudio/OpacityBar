using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Opacbar
{
    public class OpacTrayContext : ApplicationContext
    {
        private AppConfig _config;
        private TaskbarEngine _engine;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private SettingsForm _settingsForm;

        // Tray Menu Items
        private ToolStripMenuItem _menuOpenStudio;
        private ToolStripMenuItem _menuEnableToggle;
        private ToolStripMenuItem _menuPillToggle;
        private ToolStripMenuItem _menuModeClear;
        private ToolStripMenuItem _menuModeAcrylic;
        private ToolStripMenuItem _menuModeBlur;
        private ToolStripMenuItem _menuModeTinted;
        private ToolStripMenuItem _menuModeSolid;
        private ToolStripMenuItem _menuModeNormal;
        private ToolStripMenuItem _menuCornerSquare;
        private ToolStripMenuItem _menuCornerSoft;
        private ToolStripMenuItem _menuCornerRounded;
        private ToolStripMenuItem _menuCornerPill;
        private ToolStripMenuItem _menuStartup;

        public OpacTrayContext(bool startMinimized)
        {
            _config = AppConfig.Load();
            _engine = new TaskbarEngine(_config);
            _settingsForm = new SettingsForm(_config, _engine);
            _settingsForm.ConfigChanged += (s, e) => SyncTrayFromConfig();

            BuildTrayMenu();

            _trayIcon = new NotifyIcon
            {
                Icon = BrandTheme.CreateMonochromeAppIcon(_config.Enabled),
                Text = "Opacbar — Monochrome Taskbar Studio (Left-click to customize)",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            _trayIcon.MouseClick += OnTrayMouseClick;
            SyncTrayFromConfig();

            if (!startMinimized)
            {
                ShowStudioWindow();
            }
        }

        private void BuildTrayMenu()
        {
            _trayMenu = new ContextMenuStrip
            {
                Renderer = new MonochromeMenuRenderer(_config.UILightTheme),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ShowImageMargin = true
            };

            _menuOpenStudio = new ToolStripMenuItem("Open Opacbar Studio...")
            {
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
            };
            _menuOpenStudio.Click += (s, e) => ShowStudioWindow();

            _menuEnableToggle = new ToolStripMenuItem("Enable Taskbar Styling");
            _menuEnableToggle.Click += (s, e) =>
            {
                _config.Enabled = !_config.Enabled;
                ApplyAndSave();
            };

            // Quick Material Mode SubItems
            _menuModeClear = CreateModeItem("Mode: Clear (100% Transparent)", TaskbarStyleMode.Clear);
            _menuModeAcrylic = CreateModeItem("Mode: Acrylic Glass", TaskbarStyleMode.Acrylic);
            _menuModeBlur = CreateModeItem("Mode: Frosted Blur", TaskbarStyleMode.Blur);
            _menuModeTinted = CreateModeItem("Mode: Monochrome Tint", TaskbarStyleMode.Tinted);
            _menuModeSolid = CreateModeItem("Mode: Solid Monochrome", TaskbarStyleMode.Solid);
            _menuModeNormal = CreateModeItem("Mode: Windows Default", TaskbarStyleMode.Normal);

            // Floating Pill & Quick Corner Items
            _menuPillToggle = new ToolStripMenuItem("Floating Pill Taskbar");
            _menuPillToggle.Click += (s, e) =>
            {
                _config.FloatingPillEnabled = !_config.FloatingPillEnabled;
                ApplyAndSave();
            };

            _menuCornerSquare = CreateCornerItem("Corners: Square (0 px)", 0);
            _menuCornerSoft = CreateCornerItem("Corners: Soft (8 px)", 8);
            _menuCornerRounded = CreateCornerItem("Corners: Rounded (16 px)", 16);
            _menuCornerPill = CreateCornerItem("Corners: Full Pill (26 px)", 26);

            _menuStartup = new ToolStripMenuItem("Launch at Windows Sign-In");
            _menuStartup.Click += (s, e) =>
            {
                _config.LaunchAtStartup = !_config.LaunchAtStartup;
                ApplyAndSave();
            };

            ToolStripMenuItem menuExit = new ToolStripMenuItem("Exit & Restore Default Taskbar");
            menuExit.Click += (s, e) => ExitThread();

            _trayMenu.Items.Add(_menuOpenStudio);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_menuEnableToggle);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_menuModeClear);
            _trayMenu.Items.Add(_menuModeAcrylic);
            _trayMenu.Items.Add(_menuModeBlur);
            _trayMenu.Items.Add(_menuModeTinted);
            _trayMenu.Items.Add(_menuModeSolid);
            _trayMenu.Items.Add(_menuModeNormal);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_menuPillToggle);
            _trayMenu.Items.Add(_menuCornerSquare);
            _trayMenu.Items.Add(_menuCornerSoft);
            _trayMenu.Items.Add(_menuCornerRounded);
            _trayMenu.Items.Add(_menuCornerPill);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_menuStartup);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(menuExit);
        }

        private ToolStripMenuItem CreateModeItem(string label, TaskbarStyleMode mode)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Click += (s, e) =>
            {
                _config.Enabled = true;
                _config.DesktopProfile.Mode = mode;
                ApplyAndSave();
            };
            return item;
        }

        private ToolStripMenuItem CreateCornerItem(string label, int radius)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Click += (s, e) =>
            {
                _config.FloatingPillEnabled = true;
                _config.CornerRadius = radius;
                ApplyAndSave();
            };
            return item;
        }

        private void ApplyAndSave()
        {
            string err;
            _config.Save(out err);
            _engine.UpdateConfig(_config);
            _settingsForm.SyncControlsFromConfig();
            SyncTrayFromConfig();
        }

        private void SyncTrayFromConfig()
        {
            _trayMenu.Renderer = new MonochromeMenuRenderer(_config.UILightTheme);

            Icon oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = BrandTheme.CreateMonochromeAppIcon(_config.Enabled);
            if (oldIcon != null) oldIcon.Dispose();

            _menuEnableToggle.Checked = _config.Enabled;
            _menuPillToggle.Checked = _config.FloatingPillEnabled;
            _menuStartup.Checked = _config.LaunchAtStartup;

            TaskbarStyleMode m = _config.DesktopProfile.Mode;
            _menuModeClear.Checked = (m == TaskbarStyleMode.Clear);
            _menuModeAcrylic.Checked = (m == TaskbarStyleMode.Acrylic);
            _menuModeBlur.Checked = (m == TaskbarStyleMode.Blur);
            _menuModeTinted.Checked = (m == TaskbarStyleMode.Tinted);
            _menuModeSolid.Checked = (m == TaskbarStyleMode.Solid);
            _menuModeNormal.Checked = (m == TaskbarStyleMode.Normal);

            int r = _config.CornerRadius;
            _menuCornerSquare.Checked = (_config.FloatingPillEnabled && r == 0);
            _menuCornerSoft.Checked = (_config.FloatingPillEnabled && r > 0 && r <= 10);
            _menuCornerRounded.Checked = (_config.FloatingPillEnabled && r > 10 && r <= 20);
            _menuCornerPill.Checked = (_config.FloatingPillEnabled && r > 20);
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_settingsForm.Visible)
                {
                    _settingsForm.Hide();
                }
                else
                {
                    ShowStudioWindow();
                }
            }
        }

        private void ShowStudioWindow()
        {
            _settingsForm.PositionNearTray();
            _settingsForm.Show();
            _settingsForm.Activate();
            _settingsForm.BringToFront();
        }

        protected override void ExitThreadCore()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_engine != null)
            {
                _engine.Dispose();
                _engine = null;
            }
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.Dispose();
            }
            base.ExitThreadCore();
        }
    }

    public static class Program
    {
        private static Mutex _singleInstanceMutex;

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "Local\\Opacbar_Monochrome_SingleInstance_Mutex", out createdNew);
            if (!createdNew)
            {
                return;
            }

            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool startMinimized = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimized = true;
                    }
                }
            }

            using (OpacTrayContext ctx = new OpacTrayContext(startMinimized))
            {
                Application.Run(ctx);
            }

            GC.KeepAlive(_singleInstanceMutex);
        }
    }
}
