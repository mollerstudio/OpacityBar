// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Globalization;
using System.Text;

namespace OpacityBar
{
    /// <summary>
    /// Compiles a StyleProfile into the rules document the helper applies inside Explorer
    /// (see native/PROTOCOL.md). One rule per line: selector, property, value, tab-separated.
    /// </summary>
    public static class TaskbarStyle
    {
        public const string Header = "opacbar-rules 2";

        // Any element with these names: the taskbar and its hover panels (window previews) use them.
        private const string AnyFill = "Windows.UI.Xaml.Shapes.Rectangle#BackgroundFill";
        private const string AnyStroke = "Windows.UI.Xaml.Shapes.Rectangle#BackgroundStroke";
        // Only the bar itself; hover panels keep Windows' style. Window previews use a TaskbarBackground
        // too, so the path is anchored at the bar's own frame: TaskbarFrame > RootGrid > TaskbarBackground.
        private const string BarPath = "Taskbar.TaskbarFrame > Windows.UI.Xaml.Controls.Grid#RootGrid > Taskbar.TaskbarBackground > Windows.UI.Xaml.Controls.Grid > ";
        private const string SearchBox = "SearchUx.SearchUI.SearchButtonControl";
        private const string SearchBoxLegacy = "Taskbar.SearchBoxButton";
        private const string SearchBoxFill = SearchBox + " > Windows.UI.Xaml.Controls.Border#BackgroundElement";
        private const string BarFill = BarPath + "Windows.UI.Xaml.Shapes.Rectangle#BackgroundFill";
        private const string BarStroke = BarPath + "Windows.UI.Xaml.Shapes.Rectangle#BackgroundStroke";

        /// <summary>A document with no rules: the helper restores everything.</summary>
        public static string EmptyDocument()
        {
            return Header;
        }

        public static string Compile(StyleProfile p, bool stylePopups, int searchBoxTheme)
        {
            string FillSelector = stylePopups ? AnyFill : BarFill;
            string StrokeSelector = stylePopups ? AnyStroke : BarStroke;
            if (p == null || p.Material == Material.Normal) return EmptyDocument();   // stock Windows look

            StringBuilder sb = new StringBuilder(Header);
            int rgb;
            if (!TryParseHex(p.Color, out rgb)) rgb = 0;
            string tint = "#" + Alpha(p.Opacity / 100.0) + rgb.ToString("X6");

            switch (p.Material)
            {
                case Material.Clear:
                    // Hide Windows' background layer instead of replacing its brush: an Opacity value is
                    // trivially restored, so switching style always brings the original back exactly.
                    AppendRule(sb, FillSelector, "Opacity", "0");
                    break;
                case Material.Color:
                    AppendRule(sb, FillSelector, "Fill", tint);
                    break;
                default:
                    // Blur (and the legacy Acrylic / Windows values): adjustable blur drawn by the helper
                    // (protocol v6), "@blur:<radius 0-100>[:#AARRGGBB tint]". Uses the compositor's normal
                    // backdrop (what is behind the bar on screen). ":host" (HostBackdropBrush) is NOT used:
                    // the taskbar window has no host backdrop, so it renders solid (verified on 26300).
                    // Slider 0-100 -> Gaussian standard deviation 0-25 DIPs (higher values all look the same
                    // on a 48 px bar).
                    int amount = Math.Max(0, Math.Min(100, p.BlurAmount));
                    string radius = (amount / 4.0).ToString("0.##", CultureInfo.InvariantCulture);
                    AppendRule(sb, FillSelector, "Fill", "@blur:" + radius + ":" + tint);
                    break;
            }

            // Hide the top line by fading it out. Replacing its Fill brush knocks the blur into a flat
            // fallback on Win11 26300 (verified: Blur + replaced line brush = sharp gray haze).
            if (p.HideTopLine) AppendRule(sb, StrokeSelector, "Opacity", "0");

            // Search box: force its dark or light look regardless of Windows mode (RequestedTheme is a normal
            // XAML property, so removing the rule gives Windows' own look back).
            if (searchBoxTheme == 1 || searchBoxTheme == 2)
            {
                string theme = searchBoxTheme == 1 ? "Dark" : "Light";
                AppendRule(sb, SearchBox, "RequestedTheme", theme);
                // Windows' own fill is a faint see-through white, so "dark" only looks dark over a dark bar.
                // Windows keeps re-setting that Border's brushes from code, so a solid rounded child fills it
                // instead (verified on 26300: Background/BorderBrush rules don't stick, a Child does).
                AppendRule(sb, SearchBoxFill, "Child", "<Border Background=\"" +
                    (searchBoxTheme == 1 ? "#FF2E2E2E" : "#FFFAFAFA") + "\" CornerRadius=\"16\"/>");
                // Older Windows 11 builds draw the search box with this element instead; a rule that
                // matches nothing is ignored.
                AppendRule(sb, SearchBoxLegacy, "RequestedTheme", theme);
            }

            // Readable icons: ThemeSync switches Windows mode to match the bar (see ThemeSync.cs).
            return sb.ToString();
        }

        private static void AppendRule(StringBuilder sb, string selector, string property, string value)
        {
            sb.Append('\n').Append(selector).Append('\t').Append(property).Append('\t').Append(value);
        }

        private static string Alpha(double fraction)
        {
            int a = (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * 255.0);
            return a.ToString("X2");
        }

        // ---- hex helpers ----

        /// <summary>Accepts "#RRGGBB" or "RRGGBB".</summary>
        public static bool TryParseHex(string s, out int rgb)
        {
            return ColorMath.TryParseHex(s, out rgb);
        }

        /// <summary>Returns "#RRGGBB" in upper case, or null if the text is not a valid color.</summary>
        public static string NormalizeHex(string s)
        {
            int rgb;
            if (!TryParseHex(s, out rgb)) return null;
            return "#" + rgb.ToString("X6");
        }
    }
}
