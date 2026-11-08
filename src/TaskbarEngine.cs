using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    public class TaskbarEngine : IDisposable
    {
        private AppConfig _config;
        private Timer _refreshTimer;
        private DynamicActivityState _lastDetectedState = DynamicActivityState.Desktop;
        private bool _hadRegionApplied = false;

        public event Action<DynamicActivityState, int> StateUpdated;

        public DynamicActivityState CurrentState
        {
            get { return _lastDetectedState; }
        }

        public int ActiveTaskbarCount { get; private set; }

        public TaskbarEngine(AppConfig config)
        {
            _config = config;
            _refreshTimer = new Timer();
            _refreshTimer.Interval = Math.Max(16, Math.Min(500, _config.RefreshRateMs));
            _refreshTimer.Tick += OnTick;
            _refreshTimer.Start();
            ApplyImmediate();
        }

        public void UpdateConfig(AppConfig config)
        {
            _config = config;
            _refreshTimer.Interval = Math.Max(16, Math.Min(500, _config.RefreshRateMs));
            ApplyImmediate();
        }

        private void OnTick(object sender, EventArgs e)
        {
            ApplyImmediate();
        }

        public void ApplyImmediate()
        {
            List<IntPtr> taskbars = FindAllTaskbars();
            ActiveTaskbarCount = taskbars.Count;

            if (!_config.Enabled)
            {
                if (_lastDetectedState != DynamicActivityState.Disabled)
                {
                    RestoreTaskbarsToDefault(taskbars);
                    _lastDetectedState = DynamicActivityState.Disabled;
                    if (StateUpdated != null) StateUpdated(_lastDetectedState, ActiveTaskbarCount);
                }
                return;
            }

            DynamicActivityState state = DetectDynamicState();
            StyleProfile activeProfile = ResolveProfileForState(state);

            bool shouldUsePill = _config.FloatingPillEnabled;
            if (shouldUsePill && _config.DockedWhenMaximized && state == DynamicActivityState.MaximizedWindow)
            {
                shouldUsePill = false;
            }

            foreach (IntPtr hwnd in taskbars)
            {
                ApplyAccentToWindow(hwnd, activeProfile);
                ApplyPillGeometry(hwnd, shouldUsePill);
            }

            if (state != _lastDetectedState)
            {
                _lastDetectedState = state;
                if (StateUpdated != null) StateUpdated(_lastDetectedState, ActiveTaskbarCount);
            }
        }

        private StyleProfile ResolveProfileForState(DynamicActivityState state)
        {
            switch (state)
            {
                case DynamicActivityState.BatterySaver:
                    return _config.BatterySaverProfile;
                case DynamicActivityState.StartOrSearchOpen:
                    return _config.StartMenuProfile;
                case DynamicActivityState.MaximizedWindow:
                    return _config.MaximizedProfile;
                case DynamicActivityState.Desktop:
                default:
                    return _config.DesktopProfile;
            }
        }

        private DynamicActivityState DetectDynamicState()
        {
            // 1. Check Battery Saver / Low Power if enabled
            if (_config.EnableBatterySaverState)
            {
                try
                {
                    PowerStatus ps = SystemInformation.PowerStatus;
                    if (ps.PowerLineStatus == PowerLineStatus.Offline && ps.BatteryLifePercent <= 0.25f)
                    {
                        return DynamicActivityState.BatterySaver;
                    }
                }
                catch { }
            }

            // 2. Check Start Menu / Search open if enabled
            if (_config.EnableStartMenuState)
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    string cls = NativeMethods.GetWindowClassName(fg);
                    if (cls == "Windows.UI.Core.CoreWindow" ||
                        cls == "LauncherTipWnd" ||
                        cls == "XamlExplorerHostIslandWindow" ||
                        cls.IndexOf("Search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("StartMenu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return DynamicActivityState.StartOrSearchOpen;
                    }
                }
            }

            // 3. Check Maximized Window if enabled
            if (_config.EnableMaximizedState)
            {
                if (IsAnyVisibleWindowMaximized())
                {
                    return DynamicActivityState.MaximizedWindow;
                }
            }

            return DynamicActivityState.Desktop;
        }

        private bool IsAnyVisibleWindowMaximized()
        {
            // Fast check on foreground window first
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && IsRegularWindowMaximized(fg))
                return true;

            bool foundMaximized = false;
            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                if (IsRegularWindowMaximized(hwnd))
                {
                    foundMaximized = true;
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            return foundMaximized;
        }

        private bool IsRegularWindowMaximized(IntPtr hwnd)
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return false;
            if (NativeMethods.IsIconic(hwnd)) return false;
            if (!NativeMethods.IsZoomed(hwnd)) return false;

            // Ignore cloaked UWP background windows
            int cloaked = 0;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0)
            {
                if (cloaked != 0) return false;
            }

            string cls = NativeMethods.GetWindowClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;

            return true;
        }

        public List<IntPtr> FindAllTaskbars()
        {
            List<IntPtr> list = new List<IntPtr>();
            IntPtr mainTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (mainTray != IntPtr.Zero)
            {
                list.Add(mainTray);
            }

            IntPtr secondary = IntPtr.Zero;
            while (true)
            {
                secondary = NativeMethods.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
                if (secondary == IntPtr.Zero) break;
                if (!list.Contains(secondary))
                    list.Add(secondary);
            }

            return list;
        }

        private void ApplyAccentToWindow(IntPtr hwnd, StyleProfile profile)
        {
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.AccentPolicy policy = new NativeMethods.AccentPolicy();
            // Flag 2 enables drawing on all borders + allows custom color in SetWindowCompositionAttribute
            policy.AccentFlags = 2;
            policy.AnimationId = 0;

            switch (profile.Mode)
            {
                case TaskbarStyleMode.Clear:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    policy.GradientColor = 0x00000000;
                    break;

                case TaskbarStyleMode.Acrylic:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    policy.GradientColor = profile.ToAbgrColor();
                    break;

                case TaskbarStyleMode.Blur:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND;
                    policy.GradientColor = profile.ToAbgrColor();
                    break;

                case TaskbarStyleMode.Tinted:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    policy.GradientColor = profile.ToAbgrColor();
                    break;

                case TaskbarStyleMode.Solid:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_GRADIENT;
                    policy.GradientColor = profile.ToAbgrColor();
                    break;

                case TaskbarStyleMode.Normal:
                default:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_DISABLED;
                    policy.GradientColor = 0;
                    break;
            }

            if (!profile.ShowTopLine && profile.Mode != TaskbarStyleMode.Normal)
            {
                // Flag 2 suppresses the default Windows taskbar top border line in transparent/acrylic modes
                policy.AccentFlags = 2;
            }
            else if (profile.ShowTopLine)
            {
                policy.AccentFlags = 0x20 | 2;
            }

            int size = Marshal.SizeOf(policy);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                NativeMethods.WindowCompositionAttributeData data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = size,
                    Data = ptr
                };
                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private void ApplyPillGeometry(IntPtr hwnd, bool enablePill)
        {
            if (hwnd == IntPtr.Zero) return;

            if (!enablePill)
            {
                if (_hadRegionApplied)
                {
                    NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
                    int cornerDefault = 0; // DWMWCP_DEFAULT
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerDefault, sizeof(int));
                }
                return;
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(hwnd, out rect)) return;

            int width = rect.Width;
            int height = rect.Height;
            if (width <= 0 || height <= 0) return;

            int marginX = Math.Max(0, Math.Min(width / 3, _config.HorizontalMargin));
            int marginY = Math.Max(0, Math.Min(height / 3, _config.VerticalMargin));
            int radius = Math.Max(0, Math.Min(64, _config.CornerRadius));

            int x1 = marginX;
            int y1 = marginY;
            int x2 = width - marginX;
            int y2 = height - marginY;

            IntPtr hRgn;
            if (radius <= 0)
            {
                hRgn = NativeMethods.CreateRectRgn(x1, y1, x2 + 1, y2 + 1);
            }
            else
            {
                int diameter = radius * 2;
                hRgn = NativeMethods.CreateRoundRectRgn(x1, y1, x2 + 1, y2 + 1, diameter, diameter);
            }

            if (hRgn != IntPtr.Zero)
            {
                // Note: SetWindowRgn takes ownership of hRgn, so do NOT call DeleteObject on hRgn if SetWindowRgn succeeds
                int res = NativeMethods.SetWindowRgn(hwnd, hRgn, true);
                if (res == 0)
                {
                    NativeMethods.DeleteObject(hRgn);
                }
                else
                {
                    _hadRegionApplied = true;
                }
            }

            // Also notify DWM on Windows 11 builds when rounded corners are requested
            int cornerPref = (radius == 0) ? 1 : (radius <= 8 ? 3 : 2); // 1=DoNotRound, 2=Round, 3=RoundSmall
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        }

        public void RestoreTaskbarsToDefault(List<IntPtr> taskbars = null)
        {
            if (taskbars == null) taskbars = FindAllTaskbars();
            StyleProfile normal = new StyleProfile { Mode = TaskbarStyleMode.Normal };
            foreach (IntPtr hwnd in taskbars)
            {
                ApplyAccentToWindow(hwnd, normal);
                NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
                int cornerDefault = 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerDefault, sizeof(int));
                NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, true);
            }
            _hadRegionApplied = false;
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
