// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace OpacityBar
{
    // Legacy style model (config v3 and older). Kept so old settings files still load; migrated on load.
    public enum TaskbarStyleMode
    {
        Clear = 0,
        Acrylic = 1,
        Blur = 2,
        Tinted = 3,
        Solid = 4,
        Normal = 5
    }

    // Normal (the stock Windows taskbar), Clear, Color and Blur are the styles. Acrylic and Windows
    // exist only so old settings files still load; Clamp() turns both into Blur.
    public enum Material
    {
        Clear = 0,
        Color = 1,
        Blur = 2,
        Acrylic = 3,
        Windows = 4,
        Normal = 5
    }

    public class StyleProfile
    {
        public Material Material { get; set; }
        public string Color { get; set; }      // "#RRGGBB"
        public int Opacity { get; set; }       // 0 - 100 (Blur: strength of the tint over the blur)
        public bool HideTopLine { get; set; }
        public int BlurAmount { get; set; }    // 0 - 100, blur strength for the Blur style

        // Legacy fields (config v3). Read only by MigrateFromLegacy.
        public TaskbarStyleMode Mode { get; set; }
        public int Luminance { get; set; }
        public bool ShowTopLine { get; set; }

        public StyleProfile()
        {
            Material = Material.Clear;
            Color = "#000000";
            Opacity = 0;
            HideTopLine = true;
            BlurAmount = 30;
        }

        public StyleProfile(Material m, string color, int opacity) : this()
        {
            Material = m;
            Color = color;
            Opacity = opacity;
        }

        public StyleProfile Clone()
        {
            return (StyleProfile)MemberwiseClone();
        }

        /// <summary>Config v3 Mode/Luminance to Material/Color.</summary>
        public void MigrateFromLegacy()
        {
            switch (Mode)
            {
                case TaskbarStyleMode.Acrylic:
                case TaskbarStyleMode.Blur:
                case TaskbarStyleMode.Normal: Material = Material.Blur; break;
                case TaskbarStyleMode.Tinted: Material = Material.Color; break;
                case TaskbarStyleMode.Solid: Material = Material.Color; Opacity = 100; break;
                default: Material = Material.Clear; break;
            }
            int l = Math.Max(0, Math.Min(255, Luminance));
            Color = "#" + l.ToString("X2") + l.ToString("X2") + l.ToString("X2");
            HideTopLine = !ShowTopLine;
        }

        public void Clamp()
        {
            Opacity = Math.Max(0, Math.Min(100, Opacity));
            BlurAmount = Math.Max(0, Math.Min(100, BlurAmount));
            if (Material == Material.Acrylic || Material == Material.Windows) Material = Material.Blur;
            if (!Enum.IsDefined(typeof(Material), Material)) Material = Material.Clear;
            Color = TaskbarStyle.NormalizeHex(Color) ?? "#000000";
        }
    }

    public class AppConfig
    {
        public const int CurrentVersion = 5;
        public const int MaxRecentColors = 6;

        public int ConfigVersion { get; set; }

        /// <summary>Config v4 and older: the on/off switch. Off is migrated to Material.Normal; always true now.</summary>
        public bool Enabled { get; set; }

        /// <summary>The taskbar look (one look for every situation).</summary>
        public StyleProfile DesktopProfile { get; set; }

        /// <summary>Recently used taskbar colors ("#RRGGBB"), newest first.</summary>
        public List<string> RecentColors { get; set; }

        public int UiTheme { get; set; }           // 0 = follow Windows, 1 = dark, 2 = light
        public bool LaunchAtStartup { get; set; }  // mirrors the HKCU Run value; not trusted from the file
        public int RefreshRateMs { get; set; }

        /// <summary>v2 flag, migrated into UiTheme. (Other fields of old files are ignored when loading.)</summary>
        public bool UILightTheme { get; set; }

        /// <summary>True when Load() found no settings file (first launch).</summary>
        public static bool IsFirstRun { get; private set; }

        public AppConfig()
        {
            ConfigVersion = CurrentVersion;
            Enabled = true;
            DesktopProfile = new StyleProfile(Material.Clear, "#000000", 0);
            RecentColors = new List<string>();
            UiTheme = 0;
            LaunchAtStartup = false;
            RefreshRateMs = 250;
        }

        /// <summary>Copies every setting from another config into this instance (keeps object identity).</summary>
        public void CopyFrom(AppConfig o)
        {
            ConfigVersion = o.ConfigVersion;
            Enabled = o.Enabled;
            DesktopProfile = o.DesktopProfile.Clone();
            RecentColors = new List<string>(o.RecentColors ?? new List<string>());
            UiTheme = o.UiTheme;
            LaunchAtStartup = o.LaunchAtStartup;
            RefreshRateMs = o.RefreshRateMs;
        }

        public void Clamp()
        {
            if (UiTheme < 0 || UiTheme > 2) UiTheme = 0;
            RefreshRateMs = Math.Max(50, Math.Min(5000, RefreshRateMs));
            if (DesktopProfile == null) DesktopProfile = new StyleProfile();
            DesktopProfile.Clamp();

            List<string> clean = new List<string>();
            if (RecentColors != null)
            {
                foreach (string c in RecentColors)
                {
                    string n = TaskbarStyle.NormalizeHex(c);
                    if (n != null && !clean.Contains(n) && clean.Count < MaxRecentColors) clean.Add(n);
                }
            }
            RecentColors = clean;
        }

        // ---- Storage ---------------------------------------------------------------

        /// <summary>Portable mode is on only when a file named "portable" sits next to the exe.</summary>
        public static bool IsPortable()
        {
            try { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable")); }
            catch { return false; }
        }

        public static string GetConfigPath()
        {
            if (IsPortable()) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            return Path.Combine(AppInfo.RoamingDir, "settings.json");
        }

        /// <summary>Opacbar 1.0 kept its settings in %APPDATA%\Opacbar: copy them over once.</summary>
        private static void MigrateLegacyFile(string path)
        {
            if (IsPortable() || File.Exists(path)) return;
            try
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opacbar", "settings.json");
                if (!File.Exists(old)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(old, path, false);
            }
            catch { }
        }

        public static AppConfig Load()
        {
            AppConfig result = null;
            string path = GetConfigPath();
            MigrateLegacyFile(path);
            IsFirstRun = !File.Exists(path);
            if (!IsFirstRun)
            {
                try
                {
                    result = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    // Keep the unreadable file for the user instead of silently overwriting it.
                    Diagnostics.Log("settings unreadable (" + ex.Message + "); using defaults");
                    try { File.Copy(path, path + ".bad", true); } catch { }
                    result = null;
                }
            }

            if (result == null)
            {
                result = new AppConfig();
            }
            else
            {
                if (result.ConfigVersion < 3)
                {
                    // v2 -> v3: the boolean light theme flag became the UiTheme choice.
                    if (result.UILightTheme) result.UiTheme = 2;
                    result.UILightTheme = false;
                }
                if (result.ConfigVersion < 4 && result.DesktopProfile != null)
                {
                    // v3 -> v4: Mode/Luminance became Material/Color.
                    result.DesktopProfile.MigrateFromLegacy();
                }
                if (result.ConfigVersion < 5 && !result.Enabled && result.DesktopProfile != null)
                {
                    // v4 -> v5: the on/off switch became the "Normal" style.
                    result.DesktopProfile.Material = Material.Normal;
                }
                result.Enabled = true;
                result.ConfigVersion = CurrentVersion;
                result.Clamp();
            }

            result.LaunchAtStartup = IsStartupEnabled();
            return result;
        }

        /// <summary>Writes the settings file (via a temp file, retried briefly if it is locked) and syncs the startup entry.</summary>
        public bool Save(out string error)
        {
            error = null;
            try
            {
                Clamp();
                string path = GetConfigPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = FormatJson(new JavaScriptSerializer().Serialize(this));
                string tmp = path + ".tmp";

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.WriteAllText(tmp, json, new UTF8Encoding(false));
                        if (!File.Exists(path)) File.Move(tmp, path);
                        else
                        {
                            try { File.Replace(tmp, path, null, true); }
                            catch (IOException)
                            {
                                // File systems without Replace support: plain overwrite.
                                File.Copy(tmp, path, true);
                                File.Delete(tmp);
                            }
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt >= 4 || !(ex is IOException || ex is UnauthorizedAccessException)) throw;
                        Thread.Sleep(60); // locked by a scanner or sync tool for a moment
                    }
                }
                SetStartup(LaunchAtStartup);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---- Start with Windows ------------------------------------------------------
        // The installer writes the same value: HKCU Run, "OpacityBar" = "\"<exe>\" --minimized".

        public static bool IsStartupEnabled()
        {
            return Lifecycle.IsRunAtStartup();
        }

        public static void SetStartup(bool enable)
        {
            Lifecycle.SetRunAtStartup(System.Reflection.Assembly.GetExecutingAssembly().Location, enable);
        }

        private static string FormatJson(string json)
        {
            int indent = 0;
            bool quoted = false;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                switch (ch)
                {
                    case '{':
                    case '[':
                        sb.Append(ch);
                        if (!quoted)
                        {
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                        }
                        break;
                    case '}':
                    case ']':
                        if (!quoted)
                        {
                            sb.AppendLine();
                            indent--;
                            sb.Append(new string(' ', indent * 2));
                        }
                        sb.Append(ch);
                        break;
                    case '"':
                        sb.Append(ch);
                        bool escaped = false;
                        int index = i;
                        while (index > 0 && json[--index] == '\\')
                            escaped = !escaped;
                        if (!escaped)
                            quoted = !quoted;
                        break;
                    case ',':
                        sb.Append(ch);
                        if (!quoted)
                        {
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                        }
                        break;
                    case ':':
                        sb.Append(ch);
                        if (!quoted) sb.Append(" ");
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Saves the shared AppConfig: immediately for discrete changes, after a short pause for
    /// continuous input (slider and picker drags). A failed save is retried until it succeeds,
    /// and Flush() is called on every hide, quit and session end.
    /// </summary>
    public sealed class ConfigSaver : IDisposable
    {
        private readonly AppConfig _config;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private bool _dirty;

        public ConfigSaver(AppConfig config)
        {
            _config = config;
            _timer.Interval = 400;
            _timer.Tick += delegate { Flush(); };
        }

        /// <summary>Discrete change (click, toggle, menu): write now.</summary>
        public void SaveNow()
        {
            _dirty = true;
            Flush();
        }

        /// <summary>Continuous change (drag): write once the input pauses.</summary>
        public void SaveSoon()
        {
            _dirty = true;
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>Writes any pending change now.</summary>
        public void Flush()
        {
            _timer.Stop();
            if (!_dirty) return;
            string err;
            if (_config.Save(out err))
            {
                _dirty = false;
                return;
            }
            Diagnostics.Log("settings save failed: " + err);
            _timer.Start(); // retry shortly
        }

        public void Dispose()
        {
            Flush();
            _timer.Dispose();
        }
    }
}
