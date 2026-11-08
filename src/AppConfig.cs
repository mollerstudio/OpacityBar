using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Opacbar
{
    // Legacy (config v3 and older). Kept so old settings files still load; migrated to Material on load.
    public enum TaskbarStyleMode
    {
        Clear = 0,
        Acrylic = 1,
        Blur = 2,
        Tinted = 3,
        Solid = 4,
        Normal = 5
    }

    public enum Material
    {
        Clear = 0,
        Color = 1,
        Blur = 2,
        Acrylic = 3,
        Windows = 4   // stock look: no rules are sent
    }

    public class StyleProfile
    {
        public Material Material { get; set; }
        public string Color { get; set; }      // "#RRGGBB"
        public int Opacity { get; set; }       // 0 - 100
        public bool HideTopLine { get; set; }

        // Legacy fields (config v3). Read only by the one-time migration; otherwise ignored.
        public TaskbarStyleMode Mode { get; set; }
        public int Luminance { get; set; }
        public bool ShowTopLine { get; set; }

        public StyleProfile()
        {
            Material = Material.Clear;
            Color = "#000000";
            Opacity = 0;
            HideTopLine = true;
            Mode = TaskbarStyleMode.Clear;
            Luminance = 0;
            ShowTopLine = false;
        }

        public StyleProfile(Material m, string color, int opacity) : this()
        {
            Material = m;
            Color = color;
            Opacity = opacity;
        }

        public StyleProfile Clone()
        {
            return new StyleProfile
            {
                Material = this.Material,
                Color = this.Color,
                Opacity = this.Opacity,
                HideTopLine = this.HideTopLine,
                Mode = this.Mode,
                Luminance = this.Luminance,
                ShowTopLine = this.ShowTopLine
            };
        }

        /// <summary>Old Mode/Luminance model to the new one. Mode Tinted becomes Color, Solid becomes Color at 100%, Normal becomes Windows.</summary>
        public void MigrateFromLegacy()
        {
            switch (Mode)
            {
                case TaskbarStyleMode.Acrylic: Material = Material.Acrylic; break;
                case TaskbarStyleMode.Blur: Material = Material.Blur; break;
                case TaskbarStyleMode.Tinted: Material = Material.Color; break;
                case TaskbarStyleMode.Solid: Material = Material.Color; Opacity = 100; break;
                case TaskbarStyleMode.Normal: Material = Material.Windows; break;
                default: Material = Material.Clear; break;
            }
            int l = Math.Max(0, Math.Min(255, Luminance));
            Color = "#" + l.ToString("X2") + l.ToString("X2") + l.ToString("X2");
            HideTopLine = !ShowTopLine;
        }

        public void Clamp()
        {
            Opacity = Math.Max(0, Math.Min(100, Opacity));
            if (!Enum.IsDefined(typeof(Material), Material)) Material = Material.Clear;
            string n = TaskbarStyle.NormalizeHex(Color);
            Color = n ?? "#000000";
        }
    }

    public class AppConfig
    {
        public const int CurrentVersion = 4;
        public const int MaxRecentColors = 6;

        public int ConfigVersion { get; set; }

        // Master toggle
        public bool Enabled { get; set; }

        // Legacy floating-pill fields: still read from old files, ignored (real pills come later).
        public bool FloatingPillEnabled { get; set; }
        public int CornerRadius { get; set; }
        public int HorizontalMargin { get; set; }
        public int VerticalMargin { get; set; }
        public bool DockedWhenMaximized { get; set; }

        // Situation profiles
        public StyleProfile DesktopProfile { get; set; }
        public bool EnableMaximizedState { get; set; }
        public StyleProfile MaximizedProfile { get; set; }
        public bool EnableStartMenuState { get; set; }
        public StyleProfile StartMenuProfile { get; set; }
        public bool EnableBatterySaverState { get; set; }
        public StyleProfile BatterySaverProfile { get; set; }

        // Recently used taskbar colors ("#RRGGBB"), newest first
        public System.Collections.Generic.List<string> RecentColors { get; set; }

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

            DesktopProfile = new StyleProfile(Material.Clear, "#000000", 0);

            // Off by default: the bar going dark on maximize confused people.
            EnableMaximizedState = false;
            MaximizedProfile = new StyleProfile(Material.Acrylic, "#101010", 70);

            EnableStartMenuState = false;
            StartMenuProfile = new StyleProfile(Material.Acrylic, "#101010", 70);

            EnableBatterySaverState = false;
            BatterySaverProfile = new StyleProfile(Material.Color, "#080808", 100);

            RecentColors = new System.Collections.Generic.List<string>();

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
            RecentColors = new System.Collections.Generic.List<string>(o.RecentColors ?? new System.Collections.Generic.List<string>());
            UiTheme = o.UiTheme;
            UILightTheme = o.UILightTheme;
            LaunchAtStartup = o.LaunchAtStartup;
            RefreshRateMs = o.RefreshRateMs;
        }

        public void Clamp()
        {
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

            System.Collections.Generic.List<string> clean = new System.Collections.Generic.List<string>();
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
                if (result.ConfigVersion < 4)
                {
                    // v3 -> v4: Mode/Luminance became Material/Color.
                    if (result.DesktopProfile != null) result.DesktopProfile.MigrateFromLegacy();
                    if (result.MaximizedProfile != null) result.MaximizedProfile.MigrateFromLegacy();
                    if (result.StartMenuProfile != null) result.StartMenuProfile.MigrateFromLegacy();
                    if (result.BatterySaverProfile != null) result.BatterySaverProfile.MigrateFromLegacy();
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


