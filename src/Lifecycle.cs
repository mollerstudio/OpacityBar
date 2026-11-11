// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace OpacityBar
{
    /// <summary>
    /// Install / uninstall plumbing shared by OpacityBar.exe (--quit, --uninstall, Start with Windows)
    /// and OpacityBarSetup.exe. Everything is per user (HKCU, %LOCALAPPDATA%): no admin rights.
    /// </summary>
    public static class Lifecycle
    {
        // ---- Running instances --------------------------------------------------------

        /// <summary>
        /// Asks running copies (OpacityBar, and Opacbar 1.0) to quit and restore the taskbar, then waits.
        /// Returns true when none is left. Copies too old to be asked are ended after the wait.
        /// </summary>
        public static bool QuitRunning(int timeoutMs)
        {
            Signal(AppInfo.QuitEventName);
            Signal(AppInfo.LegacyQuitEventName);

            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (!MutexExists(AppInfo.MutexName) && !MutexExists(AppInfo.LegacyMutexName) && OtherProcesses().Length == 0)
                    return true;
                Thread.Sleep(150);
            }
            // Still there (hung, or a build without a quit signal): end it so files can be replaced.
            foreach (Process p in OtherProcesses())
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
                p.Dispose();
            }
            return OtherProcesses().Length == 0;
        }

        private static void Signal(string name)
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(name)) ev.Set();
            }
            catch { }
        }

        private static bool MutexExists(string name)
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(name)) return true;
            }
            catch { return false; }
        }

        /// <summary>OpacityBar / Opacbar processes other than this one (tray app or helper injector).</summary>
        private static Process[] OtherProcesses()
        {
            int self = Process.GetCurrentProcess().Id;
            System.Collections.Generic.List<Process> list = new System.Collections.Generic.List<Process>();
            foreach (string n in new string[] { "OpacityBar", "Opacbar" })
            {
                foreach (Process p in Process.GetProcessesByName(n))
                {
                    if (p.Id == self) p.Dispose();
                    else list.Add(p);
                }
            }
            return list.ToArray();
        }

        // ---- Start with Windows -------------------------------------------------------

        public static bool IsRunAtStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey, false))
                    return key != null && key.GetValue(AppInfo.RunValue) != null;
            }
            catch { return false; }
        }

        /// <summary>Adds or removes HKCU Run "OpacityBar" = "&lt;exe&gt;" --minimized (and drops Opacbar 1.0's value).</summary>
        public static void SetRunAtStartup(string exePath, bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppInfo.RunKey))
                {
                    if (key == null) return;
                    if (key.GetValue(AppInfo.LegacyRunValue) != null) key.DeleteValue(AppInfo.LegacyRunValue, false);
                    if (enable)
                    {
                        string wanted = "\"" + exePath + "\" --minimized";
                        object cur = key.GetValue(AppInfo.RunValue);
                        if (cur == null || !string.Equals(cur.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                            key.SetValue(AppInfo.RunValue, wanted);
                    }
                    else if (key.GetValue(AppInfo.RunValue) != null)
                    {
                        key.DeleteValue(AppInfo.RunValue, false);
                    }
                }
            }
            catch { }
        }

        // ---- Installed copy -----------------------------------------------------------

        /// <summary>Version of the installed copy, or null when OpacityBar is not installed.</summary>
        public static string InstalledVersion()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey, false))
                {
                    if (k == null) return null;
                    if (!File.Exists(Path.Combine(AppInfo.InstallDir, AppInfo.ExeName))) return null;
                    object v = k.GetValue("DisplayVersion");
                    return v == null ? "0.0" : v.ToString();
                }
            }
            catch { return null; }
        }

        /// <summary>True when Opacbar 1.0 (the old Inno Setup installer) is installed.</summary>
        public static bool LegacyInstalled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppInfo.LegacyInnoUninstallKey, false))
                    return k != null;
            }
            catch { return false; }
        }

        /// <summary>Runs Opacbar 1.0's uninstaller silently (keeps its settings; OpacityBar migrates them).</summary>
        public static void RemoveLegacyInstall()
        {
            try
            {
                string cmd = null;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppInfo.LegacyInnoUninstallKey, false))
                {
                    if (k != null) cmd = k.GetValue("UninstallString") as string;
                }
                if (string.IsNullOrEmpty(cmd)) return;
                string exe = cmd.Trim().Trim('"');
                if (File.Exists(exe))
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi))
                    {
                        // Inno copies itself to %TEMP% and exits; the copy finishes the job.
                        p.WaitForExit(20000);
                    }
                    DateTime end = DateTime.UtcNow.AddSeconds(20);
                    while (LegacyInstalled() && DateTime.UtcNow < end) Thread.Sleep(250);
                }
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey, true))
                {
                    if (run != null && run.GetValue(AppInfo.LegacyRunValue) != null) run.DeleteValue(AppInfo.LegacyRunValue, false);
                }
            }
            catch { }
        }

        /// <summary>The "Installed apps" entry (Settings &gt; Apps), per user.</summary>
        public static void WriteUninstallEntry(string installDir, long sizeBytes)
        {
            string exe = Path.Combine(installDir, AppInfo.ExeName);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Publisher);
                k.SetValue("DisplayIcon", "\"" + exe + "\",0");
                k.SetValue("InstallLocation", installDir);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --silent");
                k.SetValue("URLInfoAbout", AppInfo.Website);
                k.SetValue("HelpLink", AppInfo.Website + "/issues");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, sizeBytes / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        public static void RemoveUninstallEntry()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false); } catch { }
        }

        // ---- Start menu shortcut (WScript.Shell, late bound: no interop assembly) -----

        public static void CreateShortcut(string lnkPath, string target, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            try
            {
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                try
                {
                    Type lt = lnk.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk); }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        public static void DeleteQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void DeleteDirQuietly(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        /// <summary>Deletes a folder, retrying while a file in it is still in use. True when it is gone.</summary>
        public static bool DeleteDirWithRetry(string dir, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                DeleteDirQuietly(dir);
                if (!Directory.Exists(dir)) return true;
                if (DateTime.UtcNow >= end) return false;
                Thread.Sleep(250);
            }
        }

        /// <summary>
        /// One-time cleanup at the next sign-in (HKCU RunOnce: Windows runs it once, then removes the entry).
        /// Used when Explorer still held the helper file during uninstall; at sign-in nothing loads it.
        /// </summary>
        public static void DeleteDirAtNextSignIn(string dir)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"))
                {
                    if (k != null)
                        k.SetValue("OpacityBarCleanup", "cmd.exe /c rd /s /q \"" + dir + "\"");
                }
            }
            catch { }
        }

        /// <summary>Cancels a pending sign-in cleanup (a reinstall before signing out must not be deleted).</summary>
        public static void CancelSignInCleanup()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (k != null && k.GetValue("OpacityBarCleanup") != null) k.DeleteValue("OpacityBarCleanup", false);
                }
            }
            catch { }
        }

        /// <summary>Deletes a folder shortly after this process exits (for removing our own install folder).</summary>
        public static void DeleteDirAfterExit(string dir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 4 >nul & rd /s /q \"" + dir + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi).Dispose();
            }
            catch { }
        }
    }
}
