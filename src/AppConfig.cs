using System;
using System.IO;
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
        public int Luminance { get; set; } // 0 (Pure Black) - 255 (Pure White)
        public bool ShowTopLine { get; set; }

        public StyleProfile()
        {
            Mode = TaskbarStyleMode.Clear;
            Opacity = 35;
            Luminance = 10;
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
            uint b = (uint)lum;
            uint g = (uint)lum;
            uint r = (uint)lum;

            return (a << 24) | (b << 16) | (g << 8) | r;
        }
    }

    public enum PillWidthPreset
    {
        FullBar = 0,
        WideMargin = 1,
        CompactPill = 2,
        CustomMargin = 3
    }

    public class AppConfig
    {
        // Master Toggle
        public bool Enabled { get; set; }

        // Floating Pill & Geometry
        public bool FloatingPillEnabled { get; set; }
        public int CornerRadius { get; set; } // 0 (Square) to 32 (Full Pill)
        public int HorizontalMargin { get; set; } // px from left and right edges (0 to 500)
        public int VerticalMargin { get; set; } // px from top and bottom of taskbar (0 to 12)
        public bool DockedWhenMaximized { get; set; } // Expand pill to full square bar when window is maximized

        // Dynamic State Profiles
        public StyleProfile DesktopProfile { get; set; }
        public bool EnableMaximizedState { get; set; }
        public StyleProfile MaximizedProfile { get; set; }
        public bool EnableStartMenuState { get; set; }
        public StyleProfile StartMenuProfile { get; set; }
        public bool EnableBatterySaverState { get; set; }
        public StyleProfile BatterySaverProfile { get; set; }

        // App Preferences
        public bool UILightTheme { get; set; } // False = Pure Monochrome Dark, True = Pure Monochrome Light
        public bool LaunchAtStartup { get; set; }
        public int RefreshRateMs { get; set; }

        public AppConfig()
        {
            Enabled = true;
            FloatingPillEnabled = false;
            CornerRadius = 16;
            HorizontalMargin = 14;
            VerticalMargin = 4;
            DockedWhenMaximized = true;

            DesktopProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Clear,
                Opacity = 25,
                Luminance = 8,
                ShowTopLine = false
            };

            EnableMaximizedState = true;
            MaximizedProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Acrylic,
                Opacity = 75,
                Luminance = 8,
                ShowTopLine = false
            };

            EnableStartMenuState = false;
            StartMenuProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Acrylic,
                Opacity = 65,
                Luminance = 12,
                ShowTopLine = false
            };

            EnableBatterySaverState = false;
            BatterySaverProfile = new StyleProfile
            {
                Mode = TaskbarStyleMode.Solid,
                Opacity = 95,
                Luminance = 5,
                ShowTopLine = false
            };

            UILightTheme = false;
            LaunchAtStartup = false;
            RefreshRateMs = 50;
        }

        public static string GetConfigPath()
        {
            string localConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opacbar.config.json");
            try
            {
                // Prefer portable local config in app folder if writable
                string testFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".write_test");
                File.WriteAllText(testFile, "1");
                File.Delete(testFile);
                return localConfig;
            }
            catch
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opacbar");
                if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
                return Path.Combine(appData, "opacbar.config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    AppConfig loaded = serializer.Deserialize<AppConfig>(json);
                    if (loaded != null)
                    {
                        if (loaded.DesktopProfile == null) loaded.DesktopProfile = new StyleProfile();
                        if (loaded.MaximizedProfile == null) loaded.MaximizedProfile = new StyleProfile { Mode = TaskbarStyleMode.Acrylic, Opacity = 75, Luminance = 8 };
                        if (loaded.StartMenuProfile == null) loaded.StartMenuProfile = new StyleProfile { Mode = TaskbarStyleMode.Acrylic, Opacity = 65, Luminance = 12 };
                        if (loaded.BatterySaverProfile == null) loaded.BatterySaverProfile = new StyleProfile { Mode = TaskbarStyleMode.Solid, Opacity = 95, Luminance = 5 };
                        loaded.LaunchAtStartup = IsStartupEnabled();
                        return loaded;
                    }
                }
            }
            catch
            {
            }
            return new AppConfig();
        }

        public bool Save(out string error)
        {
            error = null;
            try
            {
                string path = GetConfigPath();
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = FormatJson(serializer.Serialize(this));
                File.WriteAllText(path, json);
                SetStartup(this.LaunchAtStartup);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "Opacbar";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(AppRegistryName);
                        return val != null;
                    }
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
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                            key.SetValue(AppRegistryName, "\"" + exePath + "\" --minimized");
                        }
                        else
                        {
                            if (key.GetValue(AppRegistryName) != null)
                                key.DeleteValue(AppRegistryName, false);
                        }
                    }
                }
            }
            catch { }
        }

        private static string FormatJson(string json)
        {
            int indent = 0;
            bool quoted = false;
            var sb = new System.Text.StringBuilder();
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
