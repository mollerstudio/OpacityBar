// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpacityBar
{
    public enum EngineHealth
    {
        Ok = 0,
        Paused = 1,
        NoTaskbar = 2,
        HelperMissing = 3,
        HelperFailed = 4,
        HelperOutdated = 5,
        Connecting = 6
    }

    /// <summary>
    /// Keeps the taskbar in the configured look. Windows 11: rules for the helper inside Explorer
    /// (TapHelper). Windows 10 (build below 22000): the DWM accent policy on the taskbar windows.
    /// </summary>
    public class TaskbarEngine : IDisposable
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly AppConfig _config;
        private readonly TapHelper _tap = new TapHelper();
        private readonly bool _isWin11 = IsWindows11();
        private Timer _refreshTimer;
        private Timer _fastTimer; // follows up throttled helper sends while a slider is dragged

        private bool _paused;
        private EngineHealth _health = EngineHealth.Ok;
        private string _healthDetail = "";
        private bool _notified;
        private EngineHealth _notifiedHealth;
        private int _notifiedCount;

        // Windows 10 accent state per taskbar window: last applied key and time.
        private readonly Dictionary<IntPtr, string> _accentKeys = new Dictionary<IntPtr, string>();
        private readonly Dictionary<IntPtr, long> _accentMs = new Dictionary<IntPtr, long>();

        /// <summary>Raised when health or the number of taskbars changes.</summary>
        public event Action StateUpdated;

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
            Apply(true);
        }

        private static int ClampInterval(int ms)
        {
            if (ms <= 0) return 250;
            return Math.Max(100, Math.Min(1000, ms));
        }

        private static bool IsWindows11()
        {
            try
            {
                NativeMethods.OSVERSIONINFOEX v = new NativeMethods.OSVERSIONINFOEX();
                v.dwOSVersionInfoSize = Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFOEX));
                if (NativeMethods.RtlGetVersion(ref v) == 0) return v.dwBuildNumber >= 22000;
            }
            catch { }
            return true;
        }

        /// <summary>Call after changing the config: applies it right away.</summary>
        public void ConfigChanged()
        {
            int iv = ClampInterval(_config.RefreshRateMs);
            if (_refreshTimer != null && _refreshTimer.Interval != iv) _refreshTimer.Interval = iv;
            Apply(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try { Apply(false); }
            catch (Exception ex) { Diagnostics.Log("apply failed: " + ex.Message); }
        }

        private void OnFastTick(object sender, EventArgs e)
        {
            try { Apply(false); }
            catch (Exception ex) { Diagnostics.Log("apply failed: " + ex.Message); }
            if (!_tap.HasPending && _fastTimer != null) _fastTimer.Stop();
        }

        private void Apply(bool force)
        {
            List<IntPtr> taskbars = FindAllTaskbars();
            ActiveTaskbarCount = taskbars.Count;

            ThemeSync.Update(_config.DesktopProfile, force);   // Windows mode follows the bar (restores for Normal)

            if (_config.DesktopProfile.Material == Material.Normal)
            {
                // The stock Windows look: restore once and stay out of the way.
                if (!_paused || force) RestoreTaskbarsToDefault(taskbars);
                _paused = true;
                SetHealth(EngineHealth.Ok, "");
                return;
            }
            _paused = false;

            if (taskbars.Count == 0)
            {
                SetHealth(EngineHealth.NoTaskbar, "Can't find the Windows taskbar right now (Explorer may be restarting).");
                return;
            }

            StyleProfile look = _config.DesktopProfile;
            if (!_isWin11)
            {
                long now = Clock.ElapsedMilliseconds;
                foreach (IntPtr hwnd in taskbars) ApplyAccent(hwnd, look, force, now);
                SetHealth(EngineHealth.Ok, "");
                return;
            }

            string doc = TaskbarStyle.Compile(look);
            bool styled = doc != TaskbarStyle.EmptyDocument(); // only load the helper when a rule exists
            uint pid;
            NativeMethods.GetWindowThreadProcessId(taskbars[0], out pid);
            try { _tap.Sync(pid, styled, doc); }
            catch (Exception ex)
            {
                SetHealth(EngineHealth.HelperFailed, "Taskbar helper error (" + ex.Message + ").");
                return;
            }
            if (_tap.HasPending && _fastTimer != null && !_fastTimer.Enabled) _fastTimer.Start();

            EngineHealth health = EngineHealth.Ok;
            string detail = "";
            if (styled)
            {
                switch (_tap.Status)
                {
                    case TapStatus.Missing: health = EngineHealth.HelperMissing; detail = _tap.Detail; break;
                    case TapStatus.Failed:
                    case TapStatus.Rejected: health = EngineHealth.HelperFailed; detail = _tap.Detail; break;
                    case TapStatus.Outdated: health = EngineHealth.HelperOutdated; detail = _tap.Detail; break;
                    case TapStatus.Injecting: health = EngineHealth.Connecting; detail = _tap.Detail; break;
                    case TapStatus.Idle: health = EngineHealth.Connecting; break;
                }
            }
            SetHealth(health, detail);
        }

        private void SetHealth(EngineHealth h, string detail)
        {
            _health = h;
            _healthDetail = detail ?? "";
            bool changed = !_notified || h != _notifiedHealth || ActiveTaskbarCount != _notifiedCount;
            _notified = true;
            _notifiedHealth = h;
            _notifiedCount = ActiveTaskbarCount;
            Action handler = StateUpdated;
            if (changed && handler != null)
            {
                try { handler(); }
                catch { }
            }
        }

        public static List<IntPtr> FindAllTaskbars()
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

        // ---- Windows 10: DWM accent policy ---------------------------------------------

        private void ApplyAccent(IntPtr hwnd, StyleProfile p, bool force, long now)
        {
            NativeMethods.AccentPolicy policy = new NativeMethods.AccentPolicy();
            policy.AccentFlags = 2;

            int rgb;
            if (!TaskbarStyle.TryParseHex(p.Color, out rgb)) rgb = 0;
            uint r = (uint)((rgb >> 16) & 0xFF), g = (uint)((rgb >> 8) & 0xFF), b = (uint)(rgb & 0xFF);
            uint abgr = (b << 16) | (g << 8) | r;
            int alpha = (int)Math.Round(Math.Max(0, Math.Min(100, p.Opacity)) * 255.0 / 100.0);

            switch (p.Material)
            {
                case Material.Clear:
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    break;
                case Material.Color:
                    policy.AccentState = alpha >= 255
                        ? NativeMethods.AccentState.ACCENT_ENABLE_GRADIENT
                        : NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    policy.GradientColor = ((uint)alpha << 24) | abgr;
                    break;
                default: // Blur, lightly tinted with the chosen color (60% of the tint setting)
                    policy.AccentState = NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND;
                    policy.GradientColor = ((uint)(alpha * 6 / 10) << 24) | abgr;
                    break;
            }

            // Explorer resets the accent now and then, so it is re-applied about 4 times a second.
            string key = policy.AccentState + "/" + policy.GradientColor;
            string prevKey;
            long prevMs;
            if (!force && _accentKeys.TryGetValue(hwnd, out prevKey) && prevKey == key &&
                _accentMs.TryGetValue(hwnd, out prevMs) && now - prevMs < 240)
            {
                return;
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

        // ---- Restore / Explorer / developer -------------------------------------------

        public void RestoreTaskbarsToDefault(List<IntPtr> taskbars)
        {
            if (taskbars == null) taskbars = FindAllTaskbars();

            try { _tap.Restore(); } catch { }

            if (!_isWin11)
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

        /// <summary>Diagnostics only: writes the taskbar XAML tree to the Desktop.</summary>
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
            RestoreTaskbarsToDefault(null);
            ThemeSync.Restore();
            _tap.Dispose();
        }
    }
}
