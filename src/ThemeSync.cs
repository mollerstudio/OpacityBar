// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpacityBar
{
    /// <summary>
    /// Readable taskbar icons. Windows draws the taskbar's icons, search box and tray (including apps'
    /// picture icons like Windows Update) for its "Windows mode": light mode = dark icons. So when the bar
    /// OpacityBar draws is light, Windows mode is switched to Light, and to Dark when the bar is dark.
    ///
    /// Only "Windows mode" (HKCU ...\Themes\Personalize\SystemUsesLightTheme: taskbar, Start, notification
    /// panels) is changed, never "App mode". The user's own value is remembered (HKCU\Software\OpacityBar)
    /// before the first change and put back for the Normal style, on quit and on uninstall.
    ///
    /// The bar's look = the user's color/tint at its opacity over the wallpaper behind the bar (Clear = just
    /// the wallpaper), judged by WCAG luminance with a small dead band so in-between colors don't flicker.
    /// </summary>
    public static class ThemeSync
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string ValueName = "SystemUsesLightTheme";
        private const string SavedKey = @"Software\OpacityBar";
        private const string SavedName = "SavedSystemUsesLightTheme";

        // Contrast with black beats contrast with white above L = 0.179. Switch a little past it each way.
        private const double ToLightAbove = 0.22;
        private const double ToDarkBelow = 0.14;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private const long RefreshMs = 2000;   // the engine ticks often; re-check at most every 2 s
        private static long _checkedAt = -RefreshMs;
        private static Color _wallpaper = Color.Black;
        private static string _wallpaperKey;

        /// <summary>Engine tick: keeps Windows mode matching the bar. Cheap between checks.</summary>
        public static void Update(StyleProfile p, bool force)
        {
            if (p == null || p.Material == Material.Normal) { Restore(); return; }
            long now = Clock.ElapsedMilliseconds;
            if (!force && now - _checkedAt < RefreshMs) return;
            _checkedAt = now;

            try
            {
                ReadWallpaper();
                bool currentLight = ReadSystemLight();
                double l = BarLuminance(p);
                bool wantLight = currentLight ? l >= ToDarkBelow : l > ToLightAbove;
                if (wantLight == currentLight) return;

                if (ReadSaved() < 0) WriteSaved(currentLight ? 1 : 0);   // remember the user's own choice once
                SetSystemLight(wantLight);
                Diagnostics.Log("windows mode -> " + (wantLight ? "light" : "dark") + " (bar luminance " + l.ToString("0.00") + ")");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("theme sync failed: " + ex.Message);
            }
        }

        /// <summary>Puts the user's own Windows mode back (Normal style, quit, uninstall). No-op if never changed.</summary>
        public static void Restore()
        {
            try
            {
                int saved = ReadSaved();
                if (saved < 0) return;
                if (ReadSystemLight() != (saved == 1)) SetSystemLight(saved == 1);
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SavedKey, true))
                    if (k != null) k.DeleteValue(SavedName, false);
                Diagnostics.Log("windows mode restored -> " + (saved == 1 ? "light" : "dark"));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("theme restore failed: " + ex.Message);
            }
        }

        // ---- The bar's brightness -----------------------------------------------------------

        private static double BarLuminance(StyleProfile p)
        {
            Color bg = _wallpaper;
            if (p.Material != Material.Clear)
            {
                int rgb;
                if (!TaskbarStyle.TryParseHex(p.Color, out rgb)) rgb = 0;
                double a = Math.Max(0, Math.Min(100, p.Opacity)) / 100.0;
                bg = Color.FromArgb(Mix(bg.R, (rgb >> 16) & 0xFF, a), Mix(bg.G, (rgb >> 8) & 0xFF, a), Mix(bg.B, rgb & 0xFF, a));
            }
            return 0.2126 * Lin(bg.R) + 0.7152 * Lin(bg.G) + 0.0722 * Lin(bg.B);
        }

        private static int Mix(int under, int over, double a)
        {
            return (int)Math.Round(under + (over - under) * a);
        }

        private static double Lin(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>Average color of the bottom of the current wallpaper (or the solid desktop color).</summary>
        private static void ReadWallpaper()
        {
            string setting = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false))
                if (k != null) setting = k.GetValue("WallPaper") as string;

            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            bool picture = !string.IsNullOrEmpty(setting) && File.Exists(file);
            string key = picture ? file + "|" + File.GetLastWriteTimeUtc(file).Ticks : "solid";
            if (key == _wallpaperKey && picture) return;

            Color result = SolidDesktopColor();
            if (picture)
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (Image img = Image.FromStream(fs, false, false))
                using (Bitmap small = new Bitmap(32, 18))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(img, 0, 0, 32, 18);   // like "Fill": the bottom rows sit behind the bar
                    }
                    long r = 0, gr = 0, b = 0;
                    int n = 0;
                    for (int y = 16; y < 18; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            r += c.R; gr += c.G; b += c.B; n++;
                        }
                    result = Color.FromArgb((int)(r / n), (int)(gr / n), (int)(b / n));
                }
            }
            _wallpaper = result;
            _wallpaperKey = key;
        }

        private static Color SolidDesktopColor()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors", false))
                {
                    string[] p = k != null ? ((k.GetValue("Background") as string) ?? "").Split(' ') : new string[0];
                    if (p.Length == 3) return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
                }
            }
            catch { }
            return Color.Black;
        }

        // ---- Windows mode ---------------------------------------------------------------------

        private static bool ReadSystemLight()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false))
            {
                object v = k != null ? k.GetValue(ValueName) : null;
                return v is int && (int)v != 0;
            }
        }

        private static void SetSystemLight(bool light)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
                k.SetValue(ValueName, light ? 1 : 0, RegistryValueKind.DWord);
            // Tell Explorer and others that the theme changed (what the Settings app does).
            UIntPtr result;
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, UIntPtr.Zero, "ImmersiveColorSet",
                0x0002 /* SMTO_ABORTIFHUNG */, 1000, out result);
        }

        private static int ReadSaved()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SavedKey, false))
            {
                object v = k != null ? k.GetValue(SavedName) : null;
                return v is int ? (int)v : -1;
            }
        }

        private static void WriteSaved(int value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SavedKey))
                k.SetValue(SavedName, value, RegistryValueKind.DWord);
        }

        /// <summary>Uninstall: restore Windows mode and remove OpacityBar's registry key.</summary>
        public static void RemoveAll()
        {
            Restore();
            try { Registry.CurrentUser.DeleteSubKeyTree(SavedKey, false); } catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
            uint flags, uint timeout, out UIntPtr result);
    }
}
