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
        HelperFailed = 4
    }

    public class TaskbarEngine : IDisposable
    {
        private struct RegionState
        {
            public int Left, Top, Width, Height;
            public int Dpi;
            public int X1, Y1, X2, Y2, Diameter;
            public bool Valid;
        }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private AppConfig _config;
        private Timer _refreshTimer;
        private TapHelper _tap = new TapHelper();
        private DynamicActivityState _lastDetectedState = DynamicActivityState.Desktop;
        private EngineHealth _health = EngineHealth.Ok;
        private string _healthDetail = "";
        private int _lastCount = -1;

        private readonly Dictionary<IntPtr, RegionState> _regions = new Dictionary<IntPtr, RegionState>();
        private readonly Dictionary<IntPtr, long> _regionCheckMs = new Dictionary<IntPtr, long>();
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
            _refreshTimer.Interval = ClampInterval(_config.RefreshRateMs);
            ApplyImmediate(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try { ApplyImmediate(false); }
            catch { }
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
                SetHealth(EngineHealth.Paused, "Opacbar is paused — the taskbar is at its default look.");
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

            // ---- accent (blur / acrylic / clear) ----
            foreach (IntPtr hwnd in taskbars)
            {
                ApplyAccent(hwnd, profile, force, now);
            }

            // ---- background colour through the helper (Windows 11) ----
            EngineHealth health = EngineHealth.Ok;
            string detail = "";
            if (IsWindows11())
            {
                bool active = profile.Mode != TaskbarStyleMode.Normal;
                uint pid = 0;
                NativeMethods.GetWindowThreadProcessId(taskbars[0], out pid);
                uint argb = 0;
                uint flags = 0;
                if (active)
                {
                    int lum = Math.Max(0, Math.Min(255, profile.Luminance));
                    int alpha;
                    switch (profile.Mode)
                    {
                        case TaskbarStyleMode.Clear: alpha = 0; break;
                        case TaskbarStyleMode.Solid: alpha = 255; break;
                        default: alpha = (int)Math.Round(Math.Max(0, Math.Min(100, profile.Opacity)) * 255.0 / 100.0); break;
                    }
                    argb = ((uint)alpha << 24) | ((uint)lum << 16) | ((uint)lum << 8) | (uint)lum;
                    flags = 1u | (profile.ShowTopLine ? 0u : 2u);
                }
                try { _tap.Sync(pid, active, argb, flags); }
                catch (Exception ex)
                {
                    health = EngineHealth.HelperFailed;
                    detail = "Transparency helper error (" + ex.Message + ") — pill shape still works.";
                }

                if (health == EngineHealth.Ok && active)
                {
                    if (_tap.Status == TapStatus.Missing)
                    {
                        health = EngineHealth.HelperMissing;
                        detail = _tap.Detail;
                    }
                    else if (_tap.Status == TapStatus.Failed)
                    {
                        health = EngineHealth.HelperFailed;
                        detail = _tap.Detail;
                    }
                }
            }
            SetHealth(health, detail);

            // ---- pill / corners ----
            foreach (IntPtr hwnd in taskbars)
            {
                ApplyRegion(hwnd, force, now);
            }

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
            bool needMax = _config.EnableMaximizedState || (_config.FloatingPillEnabled && _config.DockedWhenMaximized);
            if (needMax) CollectMaximizedMonitors();

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

        // ------------------------------------------------------------------ accent

        private void ApplyAccent(IntPtr hwnd, StyleProfile p, bool force, long now)
        {
            NativeMethods.AccentPolicy policy = new NativeMethods.AccentPolicy();
            policy.AccentFlags = 2;
            policy.AnimationId = 0;
            policy.GradientColor = 0;
            int lum = Math.Max(0, Math.Min(255, p.Luminance));

            switch (p.Mode)
            {
                case TaskbarStyleMode.Blur:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND;
                    break;
                case TaskbarStyleMode.Acrylic:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    policy.GradientColor = 0x01000000u | ((uint)lum << 16) | ((uint)lum << 8) | (uint)lum;
                    break;
                case TaskbarStyleMode.Normal:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_DISABLED;
                    policy.AccentFlags = 0;
                    break;
                default: // Clear, Tinted, Solid
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    break;
            }

            string key = policy.AccentState + "/" + policy.GradientColor;
            string prevKey;
            long prevMs;
            if (!force && p.Mode == TaskbarStyleMode.Normal &&
                _accentKeys.TryGetValue(hwnd, out prevKey) && prevKey == key &&
                _accentMs.TryGetValue(hwnd, out prevMs) && now - prevMs < 2000)
            {
                return; // already default; don't poke it every tick
            }
            if (!force && p.Mode != TaskbarStyleMode.Normal &&
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

        // ------------------------------------------------------------------ region (pill)

        private void ApplyRegion(IntPtr hwnd, bool force, long now)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(hwnd, out rect)) return;
            int width = rect.Width, height = rect.Height;
            if (width <= 0 || height <= 0) return;

            bool pill = _config.FloatingPillEnabled;
            if (pill && _config.DockedWhenMaximized)
            {
                IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (_maximizedMonitors.Contains(mon)) pill = false;
            }

            RegionState prev;
            bool hadPrev = _regions.TryGetValue(hwnd, out prev) && prev.Valid;

            if (!pill)
            {
                if (hadPrev || force)
                {
                    NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
                    _regions.Remove(hwnd);
                }
                return;
            }

            int dpi = 96;
            try { dpi = (int)NativeMethods.GetDpiForWindow(hwnd); } catch { }
            if (dpi <= 0) dpi = 96;
            double scale = dpi / 96.0;

            int mx = (int)Math.Round(Math.Max(0, _config.HorizontalMargin) * scale);
            int my = (int)Math.Round(Math.Max(0, _config.VerticalMargin) * scale);
            mx = Math.Min(mx, (width * 2) / 5);
            my = Math.Min(my, height / 3);

            int x1 = mx, y1 = my, x2 = width - mx, y2 = height - my;
            int pillH = y2 - y1;
            int radius = (int)Math.Round(Math.Max(0, _config.CornerRadius) * scale);
            radius = Math.Min(radius, pillH / 2);
            int diameter = radius * 2;

            RegionState cur = new RegionState();
            cur.Left = rect.Left; cur.Top = rect.Top; cur.Width = width; cur.Height = height;
            cur.Dpi = dpi; cur.X1 = x1; cur.Y1 = y1; cur.X2 = x2; cur.Y2 = y2; cur.Diameter = diameter;
            cur.Valid = true;

            bool same = hadPrev &&
                prev.Left == cur.Left && prev.Top == cur.Top && prev.Width == cur.Width && prev.Height == cur.Height &&
                prev.Dpi == cur.Dpi && prev.X1 == cur.X1 && prev.Y1 == cur.Y1 && prev.X2 == cur.X2 &&
                prev.Y2 == cur.Y2 && prev.Diameter == cur.Diameter;

            bool needApply = force || !same;
            if (!needApply)
            {
                // safety check every ~2s: has explorer dropped or replaced our region?
                long last;
                if (!_regionCheckMs.TryGetValue(hwnd, out last) || now - last >= 2000)
                {
                    _regionCheckMs[hwnd] = now;
                    needApply = !RegionMatches(hwnd, x1, y1, x2, y2);
                }
            }
            if (!needApply) return;

            IntPtr hRgn = (diameter <= 0)
                ? NativeMethods.CreateRectRgn(x1, y1, x2, y2)
                : NativeMethods.CreateRoundRectRgn(x1, y1, x2 + 1, y2 + 1, diameter, diameter);
            if (hRgn == IntPtr.Zero) return;
            if (NativeMethods.SetWindowRgn(hwnd, hRgn, true) == 0)
            {
                NativeMethods.DeleteObject(hRgn);
                return;
            }
            _regions[hwnd] = cur;
            _regionCheckMs[hwnd] = now;
        }

        private static bool RegionMatches(IntPtr hwnd, int x1, int y1, int x2, int y2)
        {
            IntPtr tmp = NativeMethods.CreateRectRgn(0, 0, 0, 0);
            if (tmp == IntPtr.Zero) return true;
            try
            {
                int type = NativeMethods.GetWindowRgn(hwnd, tmp);
                if (type == 0) return false; // ERROR: no region set
                NativeMethods.RECT box;
                NativeMethods.GetRgnBox(tmp, out box);
                // rect regions are exclusive on right/bottom; rounded ones were built with +1
                return box.Left == x1 && box.Top == y1 &&
                       (box.Right == x2 || box.Right == x2 + 1) &&
                       (box.Bottom == y2 || box.Bottom == y2 + 1);
            }
            finally
            {
                NativeMethods.DeleteObject(tmp);
            }
        }

        // ------------------------------------------------------------------ restore / dispose

        public void RestoreTaskbarsToDefault(List<IntPtr> taskbars = null)
        {
            if (taskbars == null) taskbars = FindAllTaskbars();

            try { _tap.Restore(); } catch { }

            NativeMethods.AccentPolicy off = new NativeMethods.AccentPolicy();
            off.AccentState = NativeMethods.AccentState.ACCENT_DISABLED;
            foreach (IntPtr hwnd in taskbars)
            {
                SetAccent(hwnd, off);
                NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
                NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, true);
            }
            _regions.Clear();
            _regionCheckMs.Clear();
            _accentKeys.Clear();
            _accentMs.Clear();
        }

        public void Dispose()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }
            RestoreTaskbarsToDefault();
        }
    }
}
