using System;
using System.Globalization;
using System.Text;

namespace Opacbar
{
    /// <summary>
    /// Compiles a StyleProfile into a rules document for the helper (see native/PROTOCOL.md, v2).
    /// </summary>
    public static class TaskbarStyle
    {
        public const string Header = "opacbar-rules 2";

        // Phase 1 targets. Phase 2 will refine these with the mapped taskbar tree.
        private const string FillSelector = "Windows.UI.Xaml.Shapes.Rectangle#BackgroundFill";
        private const string StrokeSelector = "Windows.UI.Xaml.Shapes.Rectangle#BackgroundStroke";

        private const string Transparent = "#00000000";

        // Blur is the Acrylic brush with a weaker tint, so it reads as a cleaner, lighter blur:
        //   TintOpacity            = opacity * 0.6
        //   TintLuminosityOpacity  = max(0.05, opacity * 0.3)
        private const double BlurTintScale = 0.6;
        private const double BlurLuminosityScale = 0.3;
        private const double BlurMinLuminosity = 0.05;

        /// <summary>A document with no rules: the helper restores everything.</summary>
        public static string EmptyDocument()
        {
            return Header;
        }

        public static string Compile(StyleProfile p)
        {
            if (p == null) return EmptyDocument();

            StringBuilder sb = new StringBuilder();
            sb.Append(Header);
            // Blur (and the legacy Acrylic / Windows values) = Windows' own frosted taskbar material, untouched.
            // It is the only blur that renders on the Win11 taskbar (verified on build 26300: XAML
            // HostBackdrop acrylic shows only its fallback color; the DWM blur accent and system backdrop
            // render black on the never-active taskbar window; fading the stock layer just ghosts a sharp
            // image over a blurred one). Its Fill is never touched.
            if (p.Material == Material.Clear)
            {
                // Clear hides Windows' layer instead of replacing its brush: an Opacity value is trivially
                // restored, so switching back to Blur always brings the original frosted look back.
                sb.Append('\n').Append(FillSelector).Append('\t').Append("Opacity").Append('\t').Append("0");
            }
            else if (p.Material == Material.Color)
            {
                sb.Append('\n').Append(FillSelector).Append('\t').Append("Fill").Append('\t').Append(BrushFor(p));
            }
            if (p.HideTopLine)
                sb.Append('\n').Append(StrokeSelector).Append('\t').Append("Fill").Append('\t').Append(Transparent);
            return sb.ToString();
        }

        /// <summary>The Fill value for a profile, or null for the stock Windows look.</summary>
        public static string BrushFor(StyleProfile p)
        {
            int rgb;
            if (!TryParseHex(p.Color, out rgb)) rgb = 0;
            double o = Math.Max(0, Math.Min(100, p.Opacity)) / 100.0;
            string rrggbb = rgb.ToString("X6");

            switch (p.Material)
            {
                case Material.Clear:
                    return Transparent;
                case Material.Color:
                    return "#" + Alpha(o) + rrggbb;
                case Material.Acrylic:
                case Material.Blur:
                    // XAML's HostBackdrop AcrylicBrush does not blur inside the taskbar island (verified on
                    // Win11 26300: it only shows its fallback color). Instead the background is made
                    // transparent and the engine applies the DWM blur/acrylic accent to the taskbar window,
                    // tinted with the chosen color + opacity (the TranslucentTB approach).
                    return Transparent;
                default:
                    return null;
            }
        }

        private static string AcrylicXaml(string rrggbb, double tint, double luminosity, double fallbackAlpha)
        {
            return "<AcrylicBrush BackgroundSource=\"HostBackdrop\" TintColor=\"#FF" + rrggbb + "\"" +
                   " TintOpacity=\"" + Num(tint) + "\"" +
                   " TintLuminosityOpacity=\"" + Num(luminosity) + "\"" +
                   " FallbackColor=\"#" + Alpha(fallbackAlpha) + rrggbb + "\"/>";
        }

        private static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
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
            rgb = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6) return false;
            for (int i = 0; i < 6; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
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
