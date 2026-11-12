// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpacityBar
{
    /// <summary>
    /// Entry point. Command line:
    ///   (none)           start in the tray; if already running, open the running instance's flyout
    ///   --minimized, -m  start in the tray without showing anything (used by "Start with Windows")
    ///   --quit           ask a running OpacityBar to quit and restore the taskbar; exit code 0 = none left
    ///   --uninstall      remove the installed copy (Settings &gt; Apps); add --silent to skip the dialogs
    ///   --diagnostics    write %LOCALAPPDATA%\OpacityBar\opacitybar.log and show "Save taskbar map..."
    ///                    (also on when a file named "diagnostics" sits next to OpacityBar.exe)
    ///   --inject pid endpoint dll   internal: one helper injection in a short-lived, invisible process
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args == null) args = new string[0];

            // Injector mode (started by TapHelper). Windows lets a process call InitializeXamlDiagnosticsEx
            // successfully only once in its lifetime, so each injection runs in a fresh copy of the exe.
            // The exit code is the HRESULT. No UI, no tray icon, no single-instance check.
            if (args.Length == 4 && args[0] == "--inject")
                return TapHelper.RunInjectorProcess(args[1], args[2], args[3]);

            if (HasArg(args, "--quit")) return Lifecycle.QuitRunning(6000) ? 0 : 1;
            if (HasArg(args, "--uninstall")) return Uninstall(HasArg(args, "--silent"));

            bool minimized = HasArg(args, "--minimized") || HasArg(args, "-m");
            bool createdNew;
            Mutex mutex = new Mutex(true, AppInfo.MutexName, out createdNew);
            if (!createdNew)
            {
                // Already running: a manual launch opens its flyout; an autostart launch does nothing.
                if (!minimized)
                {
                    try
                    {
                        UiNative.AllowSetForegroundWindow(-1);
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(AppInfo.ShowEventName)) ev.Set();
                    }
                    catch { }
                }
                mutex.Dispose();
                return 0;
            }

            Diagnostics.Init(HasArg(args, "--diagnostics"));
            Diagnostics.Log("start " + AppInfo.Version + (minimized ? " --minimized" : ""));

            // A still-running Opacbar 1.0 (the old name) would fight over the taskbar: ask it to quit.
            QuitLegacyCopy();
            DeleteOldHelperFiles();

            using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowEventName))
            using (EventWaitHandle quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.QuitEventName))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (TrayContext ctx = new TrayContext(minimized, showEvent, quitEvent))
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

        /// <summary>Helper DLLs the installer renamed aside during an update; deletable once Explorer let go.</summary>
        private static void DeleteOldHelperFiles()
        {
            try
            {
                foreach (string f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, AppInfo.HelperPrefix + "*.old"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private static void QuitLegacyCopy()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(AppInfo.LegacyMutexName)) { }
            }
            catch { return; } // not running
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(AppInfo.LegacyQuitEventName)) ev.Set();
                Thread.Sleep(1500);
            }
            catch { }
        }

        /// <summary>Removes the installed copy: quits it, then deletes its files, entries and settings.</summary>
        private static int Uninstall(bool silent)
        {
            Application.EnableVisualStyles();
            if (!silent)
            {
                DialogResult r = MessageBox.Show(
                    "Remove " + AppInfo.Name + " from this PC?\n\nYour taskbar goes back to the normal Windows look.",
                    AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.OK) return 1;
            }

            Lifecycle.QuitRunning(8000);           // the app restores the taskbar as it quits
            TapHelper.UnloadFromExplorer(5000);    // then the helper leaves Explorer and frees its DLL
            ThemeSync.RemoveAll();                 // the user's own Windows mode back, our registry key gone
            Lifecycle.SetRunAtStartup("", false);
            Lifecycle.DeleteQuietly(AppInfo.StartMenuShortcut);
            Lifecycle.RemoveUninstallEntry();
            Lifecycle.DeleteDirQuietly(AppInfo.RoamingDir);
            Lifecycle.DeleteDirQuietly(AppInfo.LocalDir);
            Lifecycle.DeleteDirQuietly(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opacbar"));
            Lifecycle.DeleteDirQuietly(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opacbar"));

            string selfDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (string.Equals(selfDir, AppInfo.InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Lifecycle.DeleteDirAfterExit(selfDir);
                // Explorer keeps the helper DLL locked until it restarts: finish at the next sign-in.
                Lifecycle.DeleteDirAtNextSignIn(selfDir);
            }

            if (!silent)
                MessageBox.Show(AppInfo.Name + " was removed.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.None);
            return 0;
        }
    }

    /// <summary>
    /// Opt-in diagnostics: a small local log (%LOCALAPPDATA%\OpacityBar\opacitybar.log, max ~256 KB, never
    /// sent anywhere) and the "Save taskbar map..." button. Off unless started with --diagnostics or a
    /// file named "diagnostics" sits next to the exe.
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
            get { return Path.Combine(AppInfo.LocalDir, "opacitybar.log"); }
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

    /// <summary>The tray app: icon, quick menu, flyout, and the signals from other processes.</summary>
    public class TrayContext : ApplicationContext
    {
        private static readonly string[] StyleNames = new string[] { "Normal", "Clear", "Color", "Blur" };
        private static readonly Material[] StyleModes = new Material[] { Material.Normal, Material.Clear, Material.Color, Material.Blur };

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
        private string _iconKey;

        private ToolStripMenuItem _miSettings, _miStartup;
        private readonly ToolStripMenuItem[] _miStyles = new ToolStripMenuItem[StyleNames.Length];

        public TrayContext(bool startMinimized, EventWaitHandle showEvent, EventWaitHandle quitEvent)
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
            _trayIcon.Text = AppInfo.Name;
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.MouseClick += OnTrayMouseClick;
            SyncTray();
            _trayIcon.Visible = true;

            _messageWindow = new AppMessageWindow(delegate { ExitThread(); }, delegate { _saver.Flush(); });
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
                            _trayIcon.ShowBalloonTip(4000, AppInfo.Name, AppInfo.Name + " lives in the tray. Click its icon any time.", ToolTipIcon.None);
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

            _miSettings = new ToolStripMenuItem("Open " + AppInfo.Name);
            _miSettings.Click += delegate { if (!_form.Visible) _form.ShowFlyout(); };
            _menu.Items.Add(_miSettings);
            _menu.Items.Add(new ToolStripSeparator());

            for (int i = 0; i < StyleNames.Length; i++)
            {
                Material m = StyleModes[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(StyleNames[i]);
                mi.Click += delegate
                {
                    _config.DesktopProfile.Material = m;
                    // Coming from Clear: start at a visible opacity instead of an invisible one.
                    if ((m == Material.Color || m == Material.Blur) && _config.DesktopProfile.Opacity < 10)
                        _config.DesktopProfile.Opacity = 60;
                    ApplyAndSave();
                };
                _miStyles[i] = mi;
                _menu.Items.Add(mi);
            }

            _menu.Items.Add(new ToolStripSeparator());
            _miStartup = new ToolStripMenuItem("Start with Windows");
            _miStartup.Click += delegate
            {
                _config.LaunchAtStartup = !_config.LaunchAtStartup;
                ApplyAndSave();
            };
            _menu.Items.Add(_miStartup);

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { ExitThread(); };
            _menu.Items.Add(quit);
        }

        private void PrepareMenu()
        {
            // Size the menu for the monitor the user clicked on.
            Theme.SetDpi(UiNative.DpiForPoint(Cursor.Position));
            _menu.Font = Theme.Body;
            Padding itemPad = new Padding(Theme.Px(4), Theme.Px(5), Theme.Px(4), Theme.Px(5));
            foreach (ToolStripItem it in _menu.Items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                mi.Font = Theme.Body;
                mi.Padding = itemPad;
            }
            _miSettings.Font = Theme.BodyBold;
            SyncTray();
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

            // The icon follows the taskbar's light/dark theme (not the app's), and dims for Normal.
            bool active = _config.DesktopProfile.Material != Material.Normal;
            bool lightTaskbar = Theme.SystemTaskbarLight();
            string key = (lightTaskbar ? "L" : "D") + (active ? "1" : "0") + SystemInformation.SmallIconSize.Width;
            if (key != _iconKey)
            {
                Icon old = _trayIcon.Icon;
                _trayIcon.Icon = IconFactory.CreateTrayIcon(active, lightTaskbar);
                _iconKey = key;
                if (old != null) old.Dispose();
            }

            _miStartup.Checked = _config.LaunchAtStartup;
            for (int i = 0; i < _miStyles.Length; i++)
                _miStyles[i].Checked = StyleModes[i] == _config.DesktopProfile.Material;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Light/dark theme switches arrive as General; accent color changes as Color.
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
