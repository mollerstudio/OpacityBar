// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Microsoft.Win32;

namespace OpacityBar
{
    /// <summary>
    /// Picks black or white taskbar glyphs so they stay readable on the chosen background.
    /// The bar's look = the user's color/tint at its opacity over the wallpaper behind the bar
    /// (Clear = just the wallpaper). Black or white is chosen by WCAG contrast. Returns null when
    /// Windows already draws the right one, so the stock glyph colors are kept whenever possible.
    /// Reads only: the light/dark taskbar setting and the current wallpaper (never changes either).
    /// </summary>
    public static class InkContrast
    {
        public const string Black = "#FF000000";
        public const string White = "#FFFFFFFF";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private const long RefreshMs = 3000;   // Compile runs often; re-read the system at most every 3 s
        private static long _checkedAt = -RefreshMs;
        private static bool _systemLight;
        private static Color _wallpaper = Color.Black;
        private static string _wallpaperKey;

        /// <param name="rgb">The profile's color as 0xRRGGBB.</param>
        public static string PickInk(StyleProfile p, int rgb)
        {
            if (p == null || p.Material == Material.Normal) return null;
            Refresh();

            Color bg = _wallpaper;
            if (p.Material != Material.Clear)
            {
                double a = Math.Max(0, Math.Min(100, p.Opacity)) / 100.0;
                Color c = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                bg = Color.FromArgb(Mix(bg.R, c.R, a), Mix(bg.G, c.G, a), Mix(bg.B, c.B, a));
            }

            double l = Luminance(bg);
            bool wantBlack = (l + 0.05) / 0.05 > 1.05 / (l + 0.05);   // higher contrast with black than white
            bool windowsDrawsBlack = _systemLight;
            if (wantBlack == windowsDrawsBlack) return null;
            return wantBlack ? Black : White;
        }

        private static int Mix(int under, int over, double a)
        {
            return (int)Math.Round(under + (over - under) * a);
        }

        /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
        public static double Luminance(Color c)
        {
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        private static double Lin(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        // ---- System state (cached) --------------------------------------------------------

        private static void Refresh()
        {
            long now = Clock.ElapsedMilliseconds;
            if (now - _checkedAt < RefreshMs) return;
            _checkedAt = now;
            _systemLight = Theme.SystemTaskbarLight();
            try { ReadWallpaper(); }
            catch (Exception ex) { Diagnostics.Log("wallpaper read failed: " + ex.Message); }
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
    }
}
