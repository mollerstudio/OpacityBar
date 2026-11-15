// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.IO;

namespace OpacityBar
{
    /// <summary>
    /// Name, version and well-known locations. Compiled into both OpacityBar.exe and
    /// OpacityBarSetup.exe, so the two always agree.
    /// </summary>
    public static class AppInfo
    {
        public const string Name = "OpacityBar";
        public const string Version = "1.3.1";          // also AssemblyVersion (AssemblyInfo.cs)
        public const string Publisher = "moller.studio";
        public const string PublisherUrl = "https://moller.studio";
        public const string Website = "https://github.com/mollerstudio/OpacityBar";
        public const string ExeName = "OpacityBar.exe";
        public const string HelperPrefix = "OpacityBarTAP.";   // OpacityBarTAP.<x64|arm64>.dll

        // Cross-process signals (Program.cs). The Legacy* names belong to Opacbar 1.0.
        public const string MutexName = "Local\\OpacityBar_SingleInstance";
        public const string ShowEventName = "Local\\OpacityBar_ShowFlyout";
        public const string QuitEventName = "Local\\OpacityBar_Quit";
        public const string LegacyMutexName = "Local\\Opacbar_Monochrome_SingleInstance_Mutex";
        public const string LegacyQuitEventName = "Local\\Opacbar_Quit";

        // Registry (all HKCU).
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "OpacityBar";
        public const string LegacyRunValue = "Opacbar";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpacityBar";
        public const string LegacyInnoUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E4C2B71-5D3A-4F6E-9B12-7A0C3D5E9F41}_is1";

        /// <summary>%LOCALAPPDATA%\Programs\OpacityBar: where the installer puts the app.</summary>
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name); }
        }

        /// <summary>%APPDATA%\OpacityBar: settings.</summary>
        public static string RoamingDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name); }
        }

        /// <summary>%LOCALAPPDATA%\OpacityBar: helper copies and the opt-in log.</summary>
        public static string LocalDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name); }
        }

        /// <summary>Start menu shortcut path.</summary>
        public static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); }
        }

        /// <summary>Compares dotted versions ("1.2.0" vs "1.10"). Unparseable counts as 0.</summary>
        public static int CompareVersions(string a, string b)
        {
            System.Version va, vb;
            if (!System.Version.TryParse(Normalize(a), out va)) va = new System.Version(0, 0);
            if (!System.Version.TryParse(Normalize(b), out vb)) vb = new System.Version(0, 0);
            return va.CompareTo(vb);
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0";
            return v.IndexOf('.') < 0 ? v + ".0" : v;
        }
    }
}
