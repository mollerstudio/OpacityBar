using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Opacbar
{
    public class OpacTrayContext : ApplicationContext
    {
        private static readonly string[] StyleNames = new string[] { "Clear", "Color", "Blur" };
        private static readonly Material[] StyleModes = new Material[]
        {
            Material.Clear, Material.Color, Material.Blur
        };
        private readonly AppConfig _config;
        private TaskbarEngine _engine;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;
        private SettingsForm _form;
        private HotkeyMessageWindow _hotkeyWindow;
        private Control _marshal;
        private RegisteredWaitHandle _showWait;

        private ToolStripMenuItem _miSettings, _miEnabled, _miStartup;
        private readonly ToolStripMenuItem[] _miStyles = new ToolStripMenuItem[StyleNames.Length];

        public OpacTrayContext(bool startMinimized, bool firstRun, EventWaitHandle showEvent)
        {
            _config = AppConfig.Load();
            firstRun = firstRun || AppConfig.IsFirstRun;
            Theme.SetDpi(UiNative.SystemDpi());
            Theme.Apply(_config.UiTheme);

            // Hidden control used only to marshal callbacks onto the UI thread.
            _marshal = new Control();
            IntPtr unused = _marshal.Handle;

            _engine = new TaskbarEngine(_config);
            _form = new SettingsForm(_config, _engine);
            _form.ConfigChanged += delegate { SyncTray(); };
            _form.QuitRequested += delegate { ExitThread(); };

            BuildMenu();

            _trayIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateTrayIcon(_config.Enabled),
                Text = "Opacbar",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _trayIcon.MouseClick += OnTrayMouseClick;

            _hotkeyWindow = new HotkeyMessageWindow(delegate { ExitThread(); });
            SyncTray();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            if (showEvent != null)
            {
                _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate(object state, bool timedOut)
                {
                    if (!timedOut) PostToUi(ShowFlyoutFromSignal);
                }, null, Timeout.Infinite, false);
            }

            if (firstRun)
            {
                string err;
                _config.Save(out err);
                PostToUi(delegate
                {
                    _form.ShowFlyout();
                    if (_trayIcon != null)
                        _trayIcon.ShowBalloonTip(4000, "Opacbar", "Opacbar is running in the tray", ToolTipIcon.None);
                });
            }
            else if (!startMinimized)
            {
                // Normal launch: stay quiet in the tray.
            }
        }

        private void PostToUi(Action a)
        {
            try
            {
                if (_marshal != null && _marshal.IsHandleCreated && !_marshal.IsDisposed)
                    _marshal.BeginInvoke(a);
            }
            catch { }
        }

        private void ShowFlyoutFromSignal()
        {
            if (_form == null || _form.IsDisposed) return;
            if (!_form.Visible) _form.ShowFlyout();
        }

        // ---- Menu -------------------------------------------------------------------

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Renderer = new MonochromeMenuRenderer();
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = true;
            _menu.Opening += delegate { PrepareMenu(); };
            _menu.HandleCreated += delegate { Theme.RoundCorners(_menu.Handle); };

            _miSettings = new ToolStripMenuItem("Settings…");
            _miSettings.Click += delegate { if (!_form.Visible) _form.ShowFlyout(); };

            _miEnabled = new ToolStripMenuItem("Enabled");
            _miEnabled.Click += delegate
            {
                _config.Enabled = !_config.Enabled;
                ApplyAndSave();
            };

            ToolStripMenuItem style = new ToolStripMenuItem("Style");
            for (int i = 0; i < StyleNames.Length; i++)
            {
                int idx = i;
                ToolStripMenuItem mi = new ToolStripMenuItem(StyleNames[i]);
                mi.Click += delegate
                {
                    _config.Enabled = true;
                    _config.DesktopProfile.Material = StyleModes[idx];
                    if (StyleModes[idx] == Material.Color && _config.DesktopProfile.Opacity < 10)
                        _config.DesktopProfile.Opacity = 60;
                    ApplyAndSave();
                };
                _miStyles[i] = mi;
                style.DropDownItems.Add(mi);
            }

            _miStartup = new ToolStripMenuItem("Start with Windows");
            _miStartup.Click += delegate
            {
                _config.LaunchAtStartup = !_config.LaunchAtStartup;
                ApplyAndSave();
            };

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { ExitThread(); };

            _menu.Items.Add(_miSettings);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miEnabled);
            _menu.Items.Add(style);
            _menu.Items.Add(_miStartup);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(quit);

            foreach (ToolStripMenuItem sub in new ToolStripMenuItem[] { style })
            {
                ToolStripDropDownMenu dd = sub.DropDown as ToolStripDropDownMenu;
                if (dd != null)
                {
                    dd.Renderer = _menu.Renderer;
                    dd.ShowImageMargin = false;
                    dd.ShowCheckMargin = true;
                    ToolStripDropDownMenu captured = dd;
                    dd.HandleCreated += delegate { Theme.RoundCorners(captured.Handle); };
                }
            }
        }

        private void PrepareMenu()
        {
            // Size the menu for the monitor the user clicked on.
            Theme.SetDpi(UiNative.DpiForPoint(Cursor.Position));
            _menu.Font = Theme.Body;
            Padding itemPad = new Padding(Theme.Px(4), Theme.Px(5), Theme.Px(4), Theme.Px(5));
            ApplyMenuLook(_menu.Items, itemPad);
            _miSettings.Font = Theme.BodyBold;
            SyncTray();
        }

        private void ApplyMenuLook(ToolStripItemCollection items, Padding pad)
        {
            foreach (ToolStripItem it in items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                mi.Font = Theme.Body;
                mi.Padding = pad;
                if (mi.HasDropDownItems)
                {
                    mi.DropDown.Font = Theme.Body;
                    ApplyMenuLook(mi.DropDownItems, pad);
                }
            }
        }

        // ---- State sync -------------------------------------------------------------

        private void ApplyAndSave()
        {
            string err;
            _config.Save(out err);
            if (_engine != null) _engine.UpdateConfig(_config);
            if (_form != null && !_form.IsDisposed) _form.SyncControlsFromConfig();
            SyncTray();
        }

        private void SyncTray()
        {
            if (_trayIcon == null) return;
            Theme.Apply(_config.UiTheme);
            _form.OnThemeChanged();

            Icon old = _trayIcon.Icon;
            _trayIcon.Icon = IconFactory.CreateTrayIcon(_config.Enabled);
            if (old != null) old.Dispose();

            _miEnabled.Checked = _config.Enabled;
            _miStartup.Checked = _config.LaunchAtStartup;

            Material m = _config.DesktopProfile.Material;
            if (m == Material.Acrylic || m == Material.Windows) m = Material.Blur;   // merged into Blur
            for (int i = 0; i < _miStyles.Length; i++) _miStyles[i].Checked = (StyleModes[i] == m);
        }
        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
            PostToUi(delegate { if (_trayIcon != null) SyncTray(); });
        }

        // ---- Tray interaction -------------------------------------------------------

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_form.Visible)
            {
                _form.HideFlyout();
                return;
            }
            // Clicking the tray icon deactivates the flyout first; don't instantly reopen it.
            if ((DateTime.UtcNow - _form.LastHideUtc).TotalMilliseconds < 400) return;
            _form.ShowFlyout();
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_showWait != null)
            {
                _showWait.Unregister(null);
                _showWait = null;
            }
            if (_hotkeyWindow != null)
            {
                _hotkeyWindow.Dispose();
                _hotkeyWindow = null;
            }
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
            if (_form != null && !_form.IsDisposed)
            {
                _form.HideFlyout();
                _form.Dispose();
            }
            if (_marshal != null)
            {
                _marshal.Dispose();
                _marshal = null;
            }
            base.ExitThreadCore();
        }
    }

    public class HotkeyMessageWindow : NativeWindow, IDisposable
    {
        private const int HOTKEY_ID_PANIC_EXIT = 9001;
        private readonly Action _onQuit;

        public HotkeyMessageWindow(Action onQuit)
        {
            _onQuit = onQuit;
            CreateHandle(new CreateParams());
            try
            {
                NativeMethods.RegisterHotKey(
                    Handle,
                    HOTKEY_ID_PANIC_EXIT,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_ALT,
                    NativeMethods.VK_Q);
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID_PANIC_EXIT)
            {
                if (_onQuit != null) _onQuit();
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                try { NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_PANIC_EXIT); } catch { }
                DestroyHandle();
            }
        }
    }

    public static class Program
    {
        private const string MutexName = "Local\\Opacbar_Monochrome_SingleInstance_Mutex";
        private const string ShowEventName = "Local\\Opacbar_ShowFlyout";

        private static Mutex _singleInstanceMutex;

        [STAThread]
        public static int Main(string[] args)
        {
            // Short-lived injector mode (started by TapHelper). Windows allows a process to call
            // InitializeXamlDiagnosticsEx successfully only ONCE in its lifetime; every later call
            // fails with 0x80070490. So each injection runs in a fresh copy of Opacbar.exe:
            //   Opacbar.exe --inject <explorerPid> <endpoint> <helperDllPath>
            // The exit code is the HRESULT. This mode shows no UI and skips the single-instance check.
            if (args != null && args.Length == 4 && args[0] == "--inject")
                return TapHelper.RunInjectorProcess(args[1], args[2], args[3]);

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // Another instance is running: ask it to open its flyout, then leave.
                try
                {
                    UiNative.AllowSetForegroundWindow(-1);
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName))
                        ev.Set();
                }
                catch { }
                return 0;
            }

            EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

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

            using (OpacTrayContext ctx = new OpacTrayContext(startMinimized, false, showEvent))
            {
                Application.Run(ctx);
            }

            GC.KeepAlive(showEvent);
            GC.KeepAlive(_singleInstanceMutex);
            return 0;
        }
    }
}

