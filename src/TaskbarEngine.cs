using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Opacbar
{
    public enum DynamicActivityState
    {
        Desktop = 0,
        MaximizedWindow = 1,
        StartOrSearchOpen = 2,
        BatterySaver = 3,
        Disabled = 4
    }

    public enum EngineHealth
    {
        Ok = 0,
        Paused = 1,
        NoTaskbar = 2,
        HelperMissing = 3,
        HelperFailed = 4,
        HelperOutdated = 5
    }

    public class TaskbarEngine : IDisposable
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private AppConfig _config;
        private Timer _refreshTimer;
        private Timer _fastTimer; // follows up throttled helper sends while a color is being dragged
        private TapHelper _tap = new TapHelper();
        private DynamicActivityState _lastDetectedState = DynamicActivityState.Desktop;
        private EngineHealth _health = EngineHealth.Ok;
        private string _healthDetail = "";
        private int _lastCount = -1;

        private readonly Dictionary<IntPtr, string> _accentKeys = new Dictionary<IntPtr, string>();
        private readonly Dictionary<IntPtr, long> _accentMs = new Dictionary<IntPtr, long>();
        private readonly HashSet<IntPtr> _maximizedMonitors = new HashSet<IntPtr>();

        private int _foregroundPidCached;
        private bool _foregroundIsStartCached;
        private bool? _isWin11;

        public event Action<DynamicActivityState, int> StateUpdated;

        public DynamicActivityState CurrentState { get { return _lastDetectedState; } }
        public int ActiveTaskbarCount { get; private set; }
        public EngineHealth Health { get { return _health; } }
        public string HealthDetail { get { return _healthDetail; } }

        public TaskbarEngine(AppConfig config)
        {
            _config = config;
            _refreshTimer = new Timer();
            _refreshTimer.Interval = ClampInterval(_config.RefreshRateMs);
            _refreshTimer.Tick += OnTick;
            _refreshTimer.Start();
            _fastTimer = new Timer();
            _fastTimer.Interval = 35;
            _fastTimer.Tick += OnFastTick;
            ApplyImmediate(true);
        }

        private static int ClampInterval(int ms)
        {
            if (ms <= 0) return 250;
            return Math.Max(100, Math.Min(1000, ms));
        }

        public void UpdateConfig(AppConfig config)
        {
            _config = config;
            int iv = ClampInterval(_config.RefreshRateMs);
            if (_refreshTimer.Interval != iv) _refreshTimer.Interval = iv;
            ApplyImmediate(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try { ApplyImmediate(false); }
            catch { }
        }

        private void OnFastTick(object sender, EventArgs e)
        {
            try { ApplyImmediate(false); }
            catch { }
            if (!_tap.HasPending && _fastTimer != null) _fastTimer.Stop();
        }

        private static long NowMs() { return Clock.ElapsedMilliseconds; }

        private bool IsWindows11()
        {
            if (_isWin11.HasValue) return _isWin11.Value;
            bool r = true;
            try
            {
                NativeMethods.OSVERSIONINFOEX v = new NativeMethods.OSVERSIONINFOEX();
                v.dwOSVersionInfoSize = Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFOEX));
                if (NativeMethods.RtlGetVersion(ref v) == 0) r = v.dwBuildNumber >= 22000;
            }
            catch { }
            _isWin11 = r;
            return r;
        }

        public void ApplyImmediate(bool force = false)
        {
            long now = NowMs();
            List<IntPtr> taskbars = FindAllTaskbars();
            ActiveTaskbarCount = taskbars.Count;

            if (!_config.Enabled)
            {
                if (_lastDetectedState != DynamicActivityState.Disabled || force)
                {
                    RestoreTaskbarsToDefault(taskbars);
                    _lastDetectedState = DynamicActivityState.Disabled;
                }
                SetHealth(EngineHealth.Paused, "Opacbar is paused. The taskbar is at its default look.");
                NotifyIfChanged(DynamicActivityState.Disabled);
                return;
            }

            if (taskbars.Count == 0)
            {
                SetHealth(EngineHealth.NoTaskbar, "Can't find the Windows taskbar right now (Explorer may be restarting).");
                NotifyIfChanged(_lastDetectedState == DynamicActivityState.Disabled ? DynamicActivityState.Desktop : _lastDetectedState);
                return;
            }

            DynamicActivityState state = DetectDynamicState();
            StyleProfile profile = ResolveProfileForState(state);

            EngineHealth health = EngineHealth.Ok;
            string detail = "";
            if (IsWindows11())
            {
                // Windows 11: everything goes through the XAML helper as brushes on the taskbar background.
                bool styled = profile.Material != Material.Windows;
                uint pid = 0;
                NativeMethods.GetWindowThreadProcessId(taskbars[0], out pid);
                try { _tap.Sync(pid, styled, TaskbarStyle.Compile(profile)); }
                catch (Exception ex)
                {
                    health = EngineHealth.HelperFailed;
                    detail = "Taskbar helper error (" + ex.Message + ").";
                }

                if (health == EngineHealth.Ok && styled)
                {
                    switch (_tap.Status)
                    {
                        case TapStatus.Missing: health = EngineHealth.HelperMissing; detail = _tap.Detail; break;
                        case TapStatus.Failed: health = EngineHealth.HelperFailed; detail = _tap.Detail; break;
                        case TapStatus.Rejected: health = EngineHealth.HelperFailed; detail = _tap.Detail; break;
                        case TapStatus.Outdated: health = EngineHealth.HelperOutdated; detail = _tap.Detail; break;
                    }
                }
                if (_tap.HasPending && _fastTimer != null && !_fastTimer.Enabled) _fastTimer.Start();
            }
            else
            {
                // Windows 10 fallback: the DWM accent policy still works on the classic taskbar.
                foreach (IntPtr hwnd in taskbars)
                {
                    ApplyAccent(hwnd, profile, force, now);
                }
            }
            SetHealth(health, detail);

            NotifyIfChanged(state);
        }

        private void SetHealth(EngineHealth h, string detail)
        {
            _health = h;
            _healthDetail = detail ?? "";
        }

        private EngineHealth _notifiedHealth = EngineHealth.Ok;
        private bool _notifiedOnce;

        private void NotifyIfChanged(DynamicActivityState state)
        {
            bool changed = !_notifiedOnce || state != _lastDetectedState || ActiveTaskbarCount != _lastCount || _health != _notifiedHealth;
            _notifiedOnce = true;
            _lastDetectedState = state;
            _lastCount = ActiveTaskbarCount;
            _notifiedHealth = _health;
            if (changed && StateUpdated != null)
            {
                try { StateUpdated(_lastDetectedState, ActiveTaskbarCount); }
                catch { }
            }
        }

        private StyleProfile ResolveProfileForState(DynamicActivityState state)
        {
            switch (state)
            {
                case DynamicActivityState.BatterySaver: return _config.BatterySaverProfile;
                case DynamicActivityState.StartOrSearchOpen: return _config.StartMenuProfile;
                case DynamicActivityState.MaximizedWindow: return _config.MaximizedProfile;
                default: return _config.DesktopProfile;
            }
        }

        // ------------------------------------------------------------------ state detection

        private DynamicActivityState DetectDynamicState()
        {
            _maximizedMonitors.Clear();
            if (_config.EnableMaximizedState) CollectMaximizedMonitors();

            if (_config.EnableBatterySaverState && IsBatterySaverOn())
                return DynamicActivityState.BatterySaver;

            if (_config.EnableStartMenuState && IsStartOrSearchForeground())
                return DynamicActivityState.StartOrSearchOpen;

            if (_config.EnableMaximizedState && _maximizedMonitors.Count > 0)
                return DynamicActivityState.MaximizedWindow;

            return DynamicActivityState.Desktop;
        }

        private static bool IsBatterySaverOn()
        {
            try
            {
                NativeMethods.SYSTEM_POWER_STATUS s;
                if (NativeMethods.GetSystemPowerStatus(out s)) return s.SystemStatusFlag == 1;
            }
            catch { }
            return false;
        }

        private bool IsStartOrSearchForeground()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            if (pid == 0) return false;

            string cls = NativeMethods.GetWindowClassName(fg);
            bool coreWin = (cls == "Windows.UI.Core.CoreWindow");
            if (!coreWin) return false;

            if ((int)pid == _foregroundPidCached) return _foregroundIsStartCached;

            string name = GetProcessImageName(pid);
            bool isStart = name != null &&
                (name.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("SearchApp.exe", StringComparison.OrdinalIgnoreCase));
            _foregroundPidCached = (int)pid;
            _foregroundIsStartCached = isStart;
            return isStart;
        }

        private static string GetProcessImageName(uint pid)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(520);
                int size = sb.Capacity;
                if (!NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                string path = sb.ToString();
                int i = path.LastIndexOf('\\');
                return i >= 0 ? path.Substring(i + 1) : path;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        private void CollectMaximizedMonitors()
        {
            NativeMethods.EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (IsRegularWindowMaximized(hwnd))
                {
                    IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                    _maximizedMonitors.Add(mon);
                }
                return true;
            }, IntPtr.Zero);
        }

        private static bool IsRegularWindowMaximized(IntPtr hwnd)
        {
            if (!NativeMethods.IsZoomed(hwnd)) return false;
            if (!NativeMethods.IsWindowVisible(hwnd)) return false;
            if (NativeMethods.IsIconic(hwnd)) return false;

            int cloaked;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;

            string cls = NativeMethods.GetWindowClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;
            return true;
        }

        public List<IntPtr> FindAllTaskbars()
        {
            List<IntPtr> list = new List<IntPtr>();
            IntPtr main = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);

            IntPtr secondary = IntPtr.Zero;
            for (int guard = 0; guard < 32; guard++)
            {
                secondary = NativeMethods.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
                if (secondary == IntPtr.Zero) break;
                if (!list.Contains(secondary)) list.Add(secondary);
            }
            return list;
        }

        // ------------------------------------------------------------------ accent (Windows 10 only)

        private void ApplyAccent(IntPtr hwnd, StyleProfile p, bool force, long now)
        {
            NativeMethods.AccentPolicy policy = new NativeMethods.AccentPolicy();
            policy.AccentFlags = 2;
            policy.AnimationId = 0;
            policy.GradientColor = 0;

            int rgb;
            if (!TaskbarStyle.TryParseHex(p.Color, out rgb)) rgb = 0;
            uint r = (uint)((rgb >> 16) & 0xFF), g = (uint)((rgb >> 8) & 0xFF), b = (uint)(rgb & 0xFF);
            uint abgr = (b << 16) | (g << 8) | r;
            int alpha = (int)Math.Round(Math.Max(0, Math.Min(100, p.Opacity)) * 255.0 / 100.0);

            switch (p.Material)
            {
                case Material.Blur:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND;
                    break;
                case Material.Acrylic:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    policy.GradientColor = ((uint)Math.Max(1, alpha) << 24) | abgr; // alpha >= 1 so it doesn't fall back to black
                    break;
                case Material.Color:
                    policy.AccentState = alpha >= 255 ? NativeMethods.AccentState.ACCENT_ENABLE_GRADIENT : NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    policy.GradientColor = ((uint)alpha << 24) | abgr;
                    break;
                case Material.Windows:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_DISABLED;
                    policy.AccentFlags = 0;
                    break;
                default: // Clear
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    break;
            }

            bool stock = p.Material == Material.Windows;
            string key = policy.AccentState + "/" + policy.GradientColor;
            string prevKey;
            long prevMs;
            if (!force && stock &&
                _accentKeys.TryGetValue(hwnd, out prevKey) && prevKey == key &&
                _accentMs.TryGetValue(hwnd, out prevMs) && now - prevMs < 2000)
            {
                return; // already default; don't poke it every tick
            }
            if (!force && !stock &&
                _accentMs.TryGetValue(hwnd, out prevMs) && now - prevMs < 240 &&
                _accentKeys.TryGetValue(hwnd, out prevKey) && prevKey == key)
            {
                return; // throttle to ~4/s
            }

            SetAccent(hwnd, policy);
            _accentKeys[hwnd] = key;
            _accentMs[hwnd] = now;
        }

        private static void SetAccent(IntPtr hwnd, NativeMethods.AccentPolicy policy)
        {
            int size = Marshal.SizeOf(typeof(NativeMethods.AccentPolicy));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                NativeMethods.WindowCompositionAttributeData data = new NativeMethods.WindowCompositionAttributeData();
                data.Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY;
                data.SizeOfData = size;
                data.Data = ptr;
                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        // ------------------------------------------------------------------ restore / helper actions / dispose

        public void RestoreTaskbarsToDefault(List<IntPtr> taskbars = null)
        {
            if (taskbars == null) taskbars = FindAllTaskbars();

            try { _tap.Restore(); } catch { }

            if (!IsWindows11())
            {
                NativeMethods.AccentPolicy off = new NativeMethods.AccentPolicy();
                off.AccentState = NativeMethods.AccentState.ACCENT_DISABLED;
                foreach (IntPtr hwnd in taskbars)
                {
                    SetAccent(hwnd, off);
                    NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, true);
                }
            }
            _accentKeys.Clear();
            _accentMs.Clear();
        }

        /// <summary>Restarts Explorer so a freshly updated helper replaces an old one.</summary>
        public void RestartExplorer()
        {
            _tap.RestartExplorer();
        }

        /// <summary>Developer action: writes the taskbar XAML tree to the Desktop.</summary>
        public bool SaveTaskbarMap(out string path, out string error)
        {
            return _tap.SaveTaskbarMap(out path, out error);
        }

        public void Dispose()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }
            if (_fastTimer != null)
            {
                _fastTimer.Stop();
                _fastTimer.Dispose();
                _fastTimer = null;
            }
            RestoreTaskbarsToDefault();
            _tap.Dispose();
        }
    }
}
