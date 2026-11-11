using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Opacbar
{
    /// <summary>
    /// Entry point. Command line:
    ///   (none)           start in the tray; if already running, open the running instance's flyout
    ///   --minimized, -m  start in the tray without showing anything (used by "Start with Windows")
    ///   --quit           ask a running Opacbar to quit and restore the taskbar (used by the installer);
    ///                    exit code 0 = not running or quit, 1 = still running after 5 s, 2 = it can't be asked
    ///   --diagnostics    write %LOCALAPPDATA%\Opacbar\opacbar.log and show "Save taskbar map..."
    ///                    (also on when a file named "diagnostics" sits next to Opacbar.exe)
    ///   --inject pid endpoint dll   internal: one helper injection in a short-lived, invisible process
    /// </summary>
    public static class Program
    {
        private const string MutexName = "Local\\Opacbar_Monochrome_SingleInstance_Mutex";
        private const string ShowEventName = "Local\\Opacbar_ShowFlyout";
        private const string QuitEventName = "Local\\Opacbar_Quit";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args == null) args = new string[0];

            // Injector mode (started by TapHelper). Windows lets a process call InitializeXamlDiagnosticsEx
            // successfully only once in its lifetime, so each injection runs in a fresh copy of Opacbar.exe.
            // The exit code is the HRESULT. No UI, no tray icon, no single-instance check.
            if (args.Length == 4 && args[0] == "--inject")
                return TapHelper.RunInjectorProcess(args[1], args[2], args[3]);

            if (HasArg(args, "--quit")) return QuitRunningInstance();

            bool minimized = HasArg(args, "--minimized") || HasArg(args, "-m");
            bool createdNew;
            Mutex mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // Already running: a manual launch opens its flyout; an autostart launch does nothing.
                if (!minimized)
                {
                    try
                    {
                        UiNative.AllowSetForegroundWindow(-1);
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set();
                    }
                    catch { }
                }
                mutex.Dispose();
                return 0;
            }

            Diagnostics.Init(HasArg(args, "--diagnostics"));
            Diagnostics.Log("start " + Application.ProductVersion + (minimized ? " --minimized" : ""));

            using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            using (EventWaitHandle quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (OpacTrayContext ctx = new OpacTrayContext(minimized, showEvent, quitEvent))
                {
                    Application.Run(ctx);
                }
            }

            // The taskbar is restored and settings are saved: let a waiting "--quit" know.
            mutex.ReleaseMutex();
            mutex.Dispose();
            Diagnostics.Log("exit");
            return 0;
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Signals the running instance to quit, then waits up to 5 s for it to finish.</summary>
        private static int QuitRunningInstance()
        {
            Mutex running;
            try { running = Mutex.OpenExisting(MutexName); }
            catch { return 0; } // not running

            using (running)
            {
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(QuitEventName)) ev.Set();
                }
                catch { return 2; } // an older Opacbar without --quit support

                try
                {
                    if (!running.WaitOne(5000)) return 1;
                    running.ReleaseMutex();
                    return 0;
                }
                catch (AbandonedMutexException)
                {
                    // It exited without releasing the mutex (killed): gone all the same.
                    try { running.ReleaseMutex(); } catch { }
                    return 0;
                }
            }
        }
    }

    /// <summary>
    /// Opt-in diagnostics: a small local log (%LOCALAPPDATA%\Opacbar\opacbar.log, max ~256 KB, never
    /// sent anywhere) and the "Save taskbar map..." button. Off unless Opacbar was started with
    /// --diagnostics or a file named "diagnostics" sits next to Opacbar.exe.
    /// </summary>
    public static class Diagnostics
    {
        private static readonly object LogLock = new object();

        public static bool Enabled { get; private set; }

        public static void Init(bool fromCommandLine)
        {
            bool marker = false;
            try { marker = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics")); }
            catch { }
            Enabled = fromCommandLine || marker;
        }

        public static string LogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opacbar", "opacbar.log"); }
        }

        public static void Log(string msg)
        {
            if (!Enabled) return;
            try
            {
                lock (LogLock)
                {
                    string p = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = p + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>The tray app: icon, quick menu, flyout, and the signals from other Opacbar processes.</summary>
    public class OpacTrayContext : ApplicationContext
    {
        private static readonly string[] StyleNames = new string[] { "Clear", "Color", "Blur" };
        private static readonly Material[] StyleModes = new Material[] { Material.Clear, Material.Color, Material.Blur };

        private readonly AppConfig _config;
        private readonly ConfigSaver _saver;
        private TaskbarEngine _engine;
        private SettingsForm _form;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;
        private AppMessageWindow _messageWindow;
        private Control _marshal;
        private RegisteredWaitHandle _showWait, _quitWait;
        private bool _exiting;

        private ToolStripMenuItem _miSettings, _miEnabled, _miStartup;
        private readonly ToolStripMenuItem[] _miStyles = new ToolStripMenuItem[StyleNames.Length];

        public OpacTrayContext(bool startMinimized, EventWaitHandle showEvent, EventWaitHandle quitEvent)
        {
            _config = AppConfig.Load();
            _saver = new ConfigSaver(_config);
            Theme.SetDpi(UiNative.SystemDpi());
            Theme.Apply(_config.UiTheme);

            // Hidden control used only to marshal callbacks onto the UI thread.
            _marshal = new Control();
            _marshal.CreateControl();
            IntPtr unused = _marshal.Handle;

            _engine = new TaskbarEngine(_config);
            _form = new SettingsForm(_config, _engine, _saver);
            _form.ConfigChanged += delegate { SyncTray(); };
            _form.QuitRequested += delegate { ExitThread(); };

            BuildMenu();
            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = IconFactory.CreateTrayIcon(_config.Enabled);
            _trayIcon.Text = "Opacbar";
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.MouseClick += OnTrayMouseClick;
            _trayIcon.Visible = true;

            _messageWindow = new AppMessageWindow(delegate { ExitThread(); }, delegate { _saver.Flush(); });
            SyncTray();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate(object state, bool timedOut)
            {
                if (!timedOut) PostToUi(ShowFlyoutFromSignal);
            }, null, Timeout.Infinite, false);
            _quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, delegate(object state, bool timedOut)
            {
                if (!timedOut) PostToUi(ExitThread);
            }, null, Timeout.Infinite, false);

            if (AppConfig.IsFirstRun)
            {
                _saver.SaveNow();
                if (!startMinimized)
                {
                    PostToUi(delegate
                    {
                        _form.ShowFlyout();
                        if (_trayIcon != null)
                            _trayIcon.ShowBalloonTip(4000, "Opacbar", "Opacbar is running in the tray", ToolTipIcon.None);
                    });
                }
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
                Material m = StyleModes[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(StyleNames[i]);
                mi.Click += delegate
                {
                    _config.Enabled = true;
                    _config.DesktopProfile.Material = m;
                    // Coming from Clear: start at a visible opacity instead of an invisible one.
                    if (m != Material.Clear && _config.DesktopProfile.Opacity < 10) _config.DesktopProfile.Opacity = 60;
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

            ToolStripDropDownMenu dd = style.DropDown as ToolStripDropDownMenu;
            if (dd != null)
            {
                dd.Renderer = _menu.Renderer;
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
                dd.HandleCreated += delegate { Theme.RoundCorners(dd.Handle); };
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

        private static void ApplyMenuLook(ToolStripItemCollection items, Padding pad)
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
            _saver.SaveNow();
            if (_engine != null) _engine.ConfigChanged();
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
            for (int i = 0; i < _miStyles.Length; i++)
                _miStyles[i].Checked = StyleModes[i] == _config.DesktopProfile.Material;
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

        // ---- Exit -------------------------------------------------------------------

        protected override void ExitThreadCore()
        {
            if (_exiting) return;
            _exiting = true;

            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_showWait != null) { _showWait.Unregister(null); _showWait = null; }
            if (_quitWait != null) { _quitWait.Unregister(null); _quitWait = null; }

            if (_form != null && !_form.IsDisposed) _form.HideFlyout();
            _saver.Flush();

            if (_messageWindow != null)
            {
                _messageWindow.Dispose();
                _messageWindow = null;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_engine != null)
            {
                _engine.Dispose(); // restores the stock taskbar
                _engine = null;
            }
            if (_form != null && !_form.IsDisposed) _form.Dispose();
            _saver.Dispose();
            if (_marshal != null)
            {
                _marshal.Dispose();
                _marshal = null;
            }
            base.ExitThreadCore();
        }
    }

    /// <summary>
    /// Hidden top-level window: owns the emergency quit hotkey (Ctrl+Shift+Alt+Q) and hears about
    /// sign-out and shutdown (WM_QUERYENDSESSION / WM_ENDSESSION) so pending settings are written in time.
    /// </summary>
    public sealed class AppMessageWindow : NativeWindow, IDisposable
    {
        private const int HotkeyIdQuit = 9001;
        private readonly Action _onQuit;
        private readonly Action _onSessionEnd;

        public AppMessageWindow(Action onQuit, Action onSessionEnd)
        {
            _onQuit = onQuit;
            _onSessionEnd = onSessionEnd;
            CreateHandle(new CreateParams());
            try
            {
                NativeMethods.RegisterHotKey(Handle, HotkeyIdQuit,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_ALT, NativeMethods.VK_Q);
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyIdQuit)
            {
                _onQuit();
                return;
            }
            if (m.Msg == NativeMethods.WM_QUERYENDSESSION)
            {
                try { _onSessionEnd(); } catch { }
                m.Result = (IntPtr)1; // never block sign-out
                return;
            }
            if (m.Msg == NativeMethods.WM_ENDSESSION && m.WParam != IntPtr.Zero)
            {
                try { _onSessionEnd(); } catch { }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                try { NativeMethods.UnregisterHotKey(Handle, HotkeyIdQuit); } catch { }
                DestroyHandle();
            }
        }
    }
}
