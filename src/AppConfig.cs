using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Opacbar
{
    public enum TaskbarStyleMode
    {
        Clear = 0,
        Acrylic = 1,
        Blur = 2,
        Tinted = 3,
        Solid = 4,
        Normal = 5
    }

    public class StyleProfile
    {
        public TaskbarStyleMode Mode { get; set; }
        public int Opacity { get; set; } // 0 - 100
        public int Luminance { get; set; } // 0 (black) - 255 (white)
        public bool ShowTopLine { get; set; }

        public StyleProfile()
        {
            Mode = TaskbarStyleMode.Clear;
            Opacity = 0;
            Luminance = 0;
            ShowTopLine = false;
        }

        public StyleProfile Clone()
        {
            return new StyleProfile
            {
                Mode = this.Mode,
                Opacity = this.Opacity,
                Luminance = this.Luminance,
                ShowTopLine = this.ShowTopLine
            };
        }

        public void Clamp()
        {
            Opacity = Math.Max(0, Math.Min(100, Opacity));
            Luminance = Math.Max(0, Math.Min(255, Luminance));
            if (!Enum.IsDefined(typeof(TaskbarStyleMode), Mode)) Mode = TaskbarStyleMode.Clear;
        }

        public uint ToAbgrColor()
        {
            int alpha = (int)Math.Round((Opacity / 100.0) * 255.0);
            if (Mode == TaskbarStyleMode.Clear)
            {
                alpha = 0;
            }
            else if (Mode == TaskbarStyleMode.Solid)
            {
                alpha = 255;
            }
            else if (Mode == TaskbarStyleMode.Acrylic && alpha == 0)
            {
                // Acrylic requires alpha >= 1 on some Windows builds so it doesn't fall back to black
                alpha = 1;
            }

            alpha = Math.Max(0, Math.Min(255, alpha));
            int lum = Math.Max(0, Math.Min(255, Luminance));

            uint a = (uint)alpha;
            uint c = (uint)lum;

            return (a << 24) | (c << 16) | (c << 8) | c;
        }
    }

    public class AppConfig
    {
        public const int CurrentVersion = 3;

        public int ConfigVersion { get; set; }

        // Master toggle
        public bool Enabled { get; set; }

        // Floating bar and geometry
        public bool FloatingPillEnabled { get; set; }
        public int CornerRadius { get; set; }      // 0 - 24 px
        public int HorizontalMargin { get; set; }  // 0 - 64 px, side gap
        public int VerticalMargin { get; set; }    // 0 - 12 px, bottom gap
        public bool DockedWhenMaximized { get; set; }

        // Situation profiles
        public StyleProfile DesktopProfile { get; set; }
        public bool EnableMaximizedState { get; set; }
        public StyleProfile MaximizedProfile { get; set; }
        public bool EnableStartMenuState { get; set; }
        public StyleProfile StartMenuProfile { get; set; }
        public bool EnableBatterySaverState { get; set; }
        public StyleProfile BatterySaverProfile { get; set; }

        // App preferences
        public int UiTheme { get; set; }           // 0 = follow Windows, 1 = dark, 2 = light
        public bool UILightTheme { get; set; }     // legacy (v2), migrated into UiTheme on load
        public bool LaunchAtStartup { get; set; }
        public int RefreshRateMs { get; set; }

        /// <summary>True when Load() found no settings file (first launch).</summary>
        public static bool IsFirstRun { get; private set; }

        public AppConfig()
        {
            ConfigVersion = CurrentVersion;
            Enabled = true;

            FloatingPillEnabled = false;
            CornerRadius = 16;
            HorizontalMargin = 8;
            VerticalMargin = 4;
            DockedWhenMaximized = true;

            DesktopProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Clear,
                Opacity = 0,
                Luminance = 0,
                ShowTopLine = false
            };

            EnableMaximizedState = true;
            MaximizedProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Acrylic,
                Opacity = 70,
                Luminance = 16,
                ShowTopLine = false
            };

            EnableStartMenuState = false;
            StartMenuProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Acrylic,
                Opacity = 70,
                Luminance = 16,
                ShowTopLine = false
            };

            EnableBatterySaverState = false;
            BatterySaverProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Solid,
                Opacity = 100,
                Luminance = 8,
                ShowTopLine = false
            };

            UiTheme = 0;
            UILightTheme = false;
            LaunchAtStartup = false;
            RefreshRateMs = 250;
        }

        /// <summary>Copies every setting from another config into this instance (keeps object identity).</summary>
        public void CopyFrom(AppConfig o)
        {
            ConfigVersion = o.ConfigVersion;
            Enabled = o.Enabled;
            FloatingPillEnabled = o.FloatingPillEnabled;
            CornerRadius = o.CornerRadius;
            HorizontalMargin = o.HorizontalMargin;
            VerticalMargin = o.VerticalMargin;
            DockedWhenMaximized = o.DockedWhenMaximized;
            DesktopProfile = o.DesktopProfile.Clone();
            EnableMaximizedState = o.EnableMaximizedState;
            MaximizedProfile = o.MaximizedProfile.Clone();
            EnableStartMenuState = o.EnableStartMenuState;
            StartMenuProfile = o.StartMenuProfile.Clone();
            EnableBatterySaverState = o.EnableBatterySaverState;
            BatterySaverProfile = o.BatterySaverProfile.Clone();
            UiTheme = o.UiTheme;
            UILightTheme = o.UILightTheme;
            LaunchAtStartup = o.LaunchAtStartup;
            RefreshRateMs = o.RefreshRateMs;
        }

        public void Clamp()
        {
            CornerRadius = Math.Max(0, Math.Min(24, CornerRadius));
            HorizontalMargin = Math.Max(0, Math.Min(64, HorizontalMargin));
            VerticalMargin = Math.Max(0, Math.Min(12, VerticalMargin));
            if (UiTheme < 0 || UiTheme > 2) UiTheme = 0;
            if (RefreshRateMs < 50) RefreshRateMs = 50;
            if (RefreshRateMs > 5000) RefreshRateMs = 5000;
            if (DesktopProfile == null) DesktopProfile = new AppConfig().DesktopProfile;
            if (MaximizedProfile == null) MaximizedProfile = new AppConfig().MaximizedProfile;
            if (StartMenuProfile == null) StartMenuProfile = new AppConfig().StartMenuProfile;
            if (BatterySaverProfile == null) BatterySaverProfile = new AppConfig().BatterySaverProfile;
            DesktopProfile.Clamp();
            MaximizedProfile.Clamp();
            StartMenuProfile.Clamp();
            BatterySaverProfile.Clamp();
        }

        // ---- Storage ---------------------------------------------------------------

        private static string ExeDirectory()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>Portable mode is on only when a file named "portable" sits next to the exe.</summary>
        public static bool IsPortable()
        {
            try { return File.Exists(Path.Combine(ExeDirectory(), "portable")); }
            catch { return false; }
        }

        public static string GetConfigPath()
        {
            if (IsPortable())
            {
                return Path.Combine(ExeDirectory(), "settings.json");
            }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opacbar");
            return Path.Combine(dir, "settings.json");
        }

        public static AppConfig Load()
        {
            AppConfig result = null;
            IsFirstRun = true;
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    IsFirstRun = false;
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    result = serializer.Deserialize<AppConfig>(File.ReadAllText(path));
                }
            }
            catch
            {
                result = null;
            }

            if (result == null)
            {
                result = new AppConfig();
            }
            else
            {
                if (result.ConfigVersion < 3)
                {
                    // v2 -> v3: boolean light theme flag became the UiTheme choice.
                    if (result.UILightTheme) result.UiTheme = 2;
                    result.UILightTheme = false;
                    result.ConfigVersion = CurrentVersion;
                }
                result.Clamp();
            }

            result.LaunchAtStartup = IsStartupEnabled();
            return result;
        }

        public bool Save(out string error)
        {
            error = null;
            try
            {
                Clamp();
                string path = GetConfigPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = FormatJson(serializer.Serialize(this));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                SetStartup(this.LaunchAtStartup);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---- Start with Windows ------------------------------------------------------

        private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "Opacbar";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                {
                    if (key != null) return key.GetValue(AppRegistryName) != null;
                }
            }
            catch { }
            return false;
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        string wanted = "\"" + exePath + "\" --minimized";
                        object cur = key.GetValue(AppRegistryName);
                        if (cur == null || !string.Equals(cur.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                            key.SetValue(AppRegistryName, wanted);
                    }
                    else if (key.GetValue(AppRegistryName) != null)
                    {
                        key.DeleteValue(AppRegistryName, false);
                    }
                }
            }
            catch { }
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
                        if (!quoted)
                            sb.Append(" ");
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
