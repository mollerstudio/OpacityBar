// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpacityBar
{
    public enum TapStatus
    {
        Idle = 0,       // nothing needed yet
        Missing = 1,    // helper DLL not found / unsupported processor
        Injecting = 2,  // injection in flight or waiting for the helper to answer
        Alive = 3,      // at least one up-to-date sink answered
        Failed = 4,     // injection failed repeatedly
        Outdated = 5,   // an older helper is still loaded in Explorer
        Rejected = 6    // the helper could not parse our rules
    }

    /// <summary>Hidden window that is the sender of our WM_COPYDATA rules and receives the helper's tree dumps ('OTRE').</summary>
    internal sealed class TapReceiver : NativeWindow
    {
        public const long DumpId = 0x4F545245; // 'OTRE'
        public readonly List<string> Dumps = new List<string>();

        public TapReceiver()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_COPYDATA)
            {
                try
                {
                    NativeMethods.COPYDATASTRUCT c = (NativeMethods.COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.COPYDATASTRUCT));
                    if (c.dwData.ToInt64() == DumpId && c.lpData != IntPtr.Zero && c.cbData > 0)
                        Dumps.Add(Marshal.PtrToStringUni(c.lpData, c.cbData / 2));
                }
                catch { }
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        public void Close()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    /// <summary>
    /// Loads OpacityBarTAP.&lt;arch&gt;.dll into explorer.exe through the XAML Diagnostics API and talks to it
    /// through its message-only "OpacbarTAP.Sink" windows (see native/PROTOCOL.md).
    /// Rules that were hard to find (verified on Windows 11 ARM64 build 26300):
    ///   - Windows accepts only ONE injection per Explorer process; later InitializeXamlDiagnosticsEx calls
    ///     into it fail with 0x80070490. So a helper that is current is reused, never injected twice, and an
    ///     older helper means Explorer has to restart.
    ///   - 0x80070490 right after Explorer starts means the taskbar is still starting: retry in ~1 s.
    ///   - A process can also inject only once in its lifetime, so each attempt runs in a short-lived
    ///     "OpacityBar.exe --inject" child process (Program.Main, RunInjectorProcess).
    ///   - The endpoint name must be exactly "VisualDiagConnection1".
    /// </summary>
    public sealed class TapHelper
    {
        private const uint WM_APP = 0x8000;
        private const uint WM_TAP_RESTORE = WM_APP + 0x11;
        private const uint WM_TAP_PING = WM_APP + 0x12;
        private const uint WM_TAP_DUMP = WM_APP + 0x14;
        private const uint WM_TAP_VERSION = WM_APP + 0x15;
        private const uint WM_TAP_STATS = WM_APP + 0x16;
        private const uint WM_TAP_UNLOAD = WM_APP + 0x17;
        private const int PING_REPLY = 0x0BAC;

        // Must equal OPAC_PROTOCOL_VERSION in native/OpacityBarTAP.cpp. An older helper found in Explorer
        // triggers an automatic (rate-limited) Explorer restart.
        private const int ProtocolVersion = 6;

        private const int E_NOT_FOUND_HR = unchecked((int)0x80070490);
        private const long RulesId = 0x4F504143; // 'OPAC'
        private const long SendMinIntervalMs = 33; // ~30 sends/s while dragging
        private const string SinkClass = "OpacbarTAP.Sink";
        private const string Endpoint = "VisualDiagConnection1";
        private const string OutdatedDetail = "OpacityBar was updated, but Windows is still running the old version inside the taskbar. Restart Explorer once to load the new one; your taskbar disappears for a second and comes back.";
        private static readonly Guid TapClsid = new Guid("6C3B1F0A-3D52-4E8B-9A41-0B5C2E7D9F11");
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private TapStatus _status = TapStatus.Idle;
        private string _detail = "";

        // Desired state and what each sink last received.
        private string _desiredDoc = "";
        private readonly Dictionary<long, string> _sent = new Dictionary<long, string>();
        private long _lastSendMs = -100000;
        private bool _pending;
        private bool _ruleError;

        // Explorer tracking and retry timing.
        private uint _lastPid;
        private uint _injectedOkPid;         // Explorer we already injected successfully (never inject twice)
        private long _injectOkAtMs;
        private long _startingSinceMs;       // first 0x80070490 for the current Explorer
        private long _lastPollMs = -100000;
        private long _nextAttemptMs;
        private int _failCount;
        private string _preparedDll;

        // Injection thread (runs the --inject child process).
        private Thread _thread;
        private long _threadStartMs;
        private volatile int _attemptGen;
        private volatile bool _threadDone;
        private int _threadHr;

        // Explorer restarts.
        private volatile bool _restarting;
        private long _lastAutoRestartMs = -10000000;
        private int _autoRestarts;

        private TapReceiver _receiver;

        public TapStatus Status { get { return _status; } }
        public string Detail { get { return _detail; } }

        /// <summary>True while a throttled send is waiting; the engine should call Sync again soon.</summary>
        public bool HasPending { get { return _pending; } }

        public static string ArchName()
        {
            ushort proc, native;
            try
            {
                if (NativeMethods.IsWow64Process2(NativeMethods.GetCurrentProcess(), out proc, out native))
                {
                    if (native == NativeMethods.IMAGE_FILE_MACHINE_ARM64) return "arm64";
                    if (native == NativeMethods.IMAGE_FILE_MACHINE_AMD64) return "x64";
                    return null;
                }
            }
            catch { }
            return Environment.Is64BitOperatingSystem ? "x64" : null;
        }

        /// <summary>
        /// The helper DLL next to OpacityBar.exe (normally %LOCALAPPDATA%\Programs\OpacityBar). It is loaded
        /// from there directly: verified on Windows 11 26300 that Explorer reliably loads it from the install
        /// folder, while copies in other folders were sometimes ignored. While Explorer has it loaded the
        /// file is locked; the installer renames it aside on update, and uninstall cleans up at next sign-in.
        /// </summary>
        private string PrepareDll()
        {
            if (_preparedDll != null && File.Exists(_preparedDll)) return _preparedDll;
            string arch = ArchName();
            if (arch == null) return null;
            string src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppInfo.HelperPrefix + arch + ".dll");
            if (!File.Exists(src)) return null;
            _preparedDll = src;
            return src;
        }

        // ---- Sinks and messages -----------------------------------------------------------

        private static List<IntPtr> FindSinks()
        {
            List<IntPtr> list = new List<IntPtr>();
            IntPtr prev = IntPtr.Zero;
            for (int guard = 0; guard < 64; guard++)
            {
                prev = NativeMethods.FindWindowEx(NativeMethods.HWND_MESSAGE, prev, SinkClass, null);
                if (prev == IntPtr.Zero) break;
                list.Add(prev);
            }
            return list;
        }

        private static bool Send(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, uint timeoutMs, out int result)
        {
            UIntPtr res;
            IntPtr ok = NativeMethods.SendMessageTimeout(hwnd, msg, wParam, lParam,
                NativeMethods.SMTO_ABORTIFHUNG, timeoutMs, out res);
            result = unchecked((int)res.ToUInt64());
            return ok != IntPtr.Zero;
        }

        private static bool Send(IntPtr hwnd, uint msg, out int result)
        {
            return Send(hwnd, msg, UIntPtr.Zero, IntPtr.Zero, 1000, out result);
        }

        private TapReceiver Receiver
        {
            get
            {
                if (_receiver == null) _receiver = new TapReceiver();
                return _receiver;
            }
        }

        private static UIntPtr HandleArg(IntPtr h)
        {
            return new UIntPtr(unchecked((ulong)h.ToInt64()));
        }

        /// <summary>Sends a rules document as WM_COPYDATA. Returns the helper's reply, or int.MinValue if it did not answer.</summary>
        private int SendRules(IntPtr sink, string doc)
        {
            IntPtr text = Marshal.StringToHGlobalUni(doc);
            IntPtr cds = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.COPYDATASTRUCT)));
            try
            {
                NativeMethods.COPYDATASTRUCT c = new NativeMethods.COPYDATASTRUCT();
                c.dwData = new IntPtr(RulesId);
                c.cbData = doc.Length * 2;
                c.lpData = text;
                Marshal.StructureToPtr(c, cds, false);
                int r;
                if (!Send(sink, (uint)NativeMethods.WM_COPYDATA, HandleArg(Receiver.Handle), cds, 1000, out r)) return int.MinValue;
                return r;
            }
            finally
            {
                Marshal.FreeHGlobal(cds);
                Marshal.FreeHGlobal(text);
            }
        }

        private static ulong SinkStats(IntPtr sink)
        {
            UIntPtr res;
            IntPtr ok = NativeMethods.SendMessageTimeout(sink, WM_TAP_STATS, UIntPtr.Zero, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 1000, out res);
            return ok != IntPtr.Zero ? res.ToUInt64() : 0UL;
        }

        private static string StatsText(ulong st)
        {
            string s = "elements " + (st & 0xFFFF) + ", in taskbar " + ((st >> 16) & 0xFFFF);
            if ((st & (1UL << 32)) != 0) s += ", advise ok";
            if ((st & (1UL << 33)) != 0) s += ", advise FAILED";
            if ((st & (1UL << 34)) != 0) s += ", advise in progress";
            if ((st & (1UL << 35)) != 0) s += ", control sink";
            if ((st & (1UL << 36)) != 0) s += ", unloading";
            return s;
        }

        private static int SinkVersion(IntPtr sink)
        {
            int v;
            if (!Send(sink, WM_TAP_VERSION, out v)) return -1;
            return v;
        }

        /// <summary>Live sinks split into current-version ones and whether any older helper answered.</summary>
        private static List<IntPtr> FindLiveSinks(out bool anyOld)
        {
            List<IntPtr> good = new List<IntPtr>();
            anyOld = false;
            foreach (IntPtr s in FindSinks())
            {
                int r;
                if (!Send(s, WM_TAP_PING, out r) || r != PING_REPLY) continue;
                if (SinkVersion(s) >= ProtocolVersion) good.Add(s);
                else anyOld = true;
            }
            return good;
        }

        // ---- Sync (UI timer) ------------------------------------------------------------

        /// <summary>
        /// Call from the UI timer. styled=false means the stock look: no injection is needed and the
        /// empty document is sent to a helper that is already loaded.
        /// </summary>
        public void Sync(uint explorerPid, bool styled, string doc)
        {
            if (doc == null) doc = TaskbarStyle.EmptyDocument();
            bool dirty = doc != _desiredDoc;
            _desiredDoc = doc;

            long now = Clock.ElapsedMilliseconds;
            CollectInjection(explorerPid, now);

            if (explorerPid != 0 && explorerPid != _lastPid)
            {
                Diagnostics.Log("explorer pid changed " + _lastPid + " -> " + explorerPid);
                // New Explorer: forget the old backoff; the new shell has no helper yet.
                _lastPid = explorerPid;
                _failCount = 0;
                _nextAttemptMs = 0;
                _startingSinceMs = 0;
                _lastPollMs = -100000;
            }

            if (_restarting) return;
            if (!dirty && !_pending && now - _lastPollMs < 1000) return;
            _lastPollMs = now;

            bool anyOld;
            List<IntPtr> good = FindLiveSinks(out anyOld);
            if (good.Count > 0)
            {
                SendToSinks(good, doc, now);
                if (styled && _status == TapStatus.Alive) RefineWithStats(good);
                return;
            }

            _sent.Clear();
            _pending = false;

            if (anyOld)
            {
                if (styled)
                {
                    if (_status != TapStatus.Outdated) Diagnostics.Log("older helper found in explorer (need version " + ProtocolVersion + ")");
                    if (!TryAutoRestart(now, "Updating the taskbar helper (restarting Explorer)..."))
                        SetStatus(TapStatus.Outdated, OutdatedDetail);
                }
                else if (_status == TapStatus.Outdated || _status == TapStatus.Alive)
                {
                    SetStatus(TapStatus.Idle, "");
                }
                return;
            }

            if (_status == TapStatus.Alive || _status == TapStatus.Outdated || _status == TapStatus.Rejected)
                SetStatus(TapStatus.Idle, "");
            if (!styled) return;

            if (_thread != null)
            {
                // InitializeXamlDiagnosticsEx can hang (e.g. Explorer died mid-connection). Abandon a stuck
                // attempt after 10 s; its late result is ignored (generation check).
                if (now - _threadStartMs < 10000) return;
                Diagnostics.Log("inject attempt " + _attemptGen + " hung for 10 s: abandoned");
                Interlocked.Increment(ref _attemptGen);
                _thread = null;
                _failCount++;
                _nextAttemptMs = now + 2000;
                SetStatus(TapStatus.Injecting, "Connecting to the taskbar is taking long; retrying...");
                return;
            }

            // Already injected into this Explorer: never inject again. Give the helper time to start;
            // if it never answers, restart Explorer.
            if (explorerPid != 0 && explorerPid == _injectedOkPid)
            {
                if (now - _injectOkAtMs < 15000)
                {
                    SetStatus(TapStatus.Injecting, "Starting the taskbar helper...");
                    return;
                }
                if (!TryAutoRestart(now, "The taskbar helper didn't start; restarting Explorer..."))
                    SetStatus(TapStatus.Failed, "The taskbar helper loaded but never answered. Use Quit, then start OpacityBar again.");
                return;
            }

            if (now < _nextAttemptMs) return;

            string dll;
            try { dll = PrepareDll(); }
            catch (Exception ex)
            {
                SetStatus(TapStatus.Failed, "Couldn't prepare the taskbar helper (" + ex.Message + ").");
                _nextAttemptMs = now + 30000;
                return;
            }
            if (dll == null)
            {
                SetStatus(TapStatus.Missing, ArchName() == null
                    ? "This processor type isn't supported by the taskbar helper."
                    : "Taskbar helper not found next to OpacityBar.exe.");
                _nextAttemptMs = now + 5000;
                return;
            }
            if (explorerPid == 0) return;

            StartInjection(dll, explorerPid);
            SetStatus(TapStatus.Injecting, "Starting the taskbar helper...");
        }

        private void SetStatus(TapStatus status, string detail)
        {
            _status = status;
            _detail = detail;
        }

        /// <summary>Sends the document to every live sink that doesn't have it yet (throttled while dragging).</summary>
        private void SendToSinks(List<IntPtr> good, string doc, long now)
        {
            if (_status != TapStatus.Alive && _status != TapStatus.Rejected)
                Diagnostics.Log("helper connected: " + good.Count + " sink(s), version " + SinkVersion(good[0]));
            _failCount = 0;

            HashSet<long> live = new HashSet<long>();
            foreach (IntPtr s in good) live.Add(s.ToInt64());
            List<long> stale = new List<long>();
            foreach (long k in _sent.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (long k in stale) _sent.Remove(k);

            List<IntPtr> needs = new List<IntPtr>();
            foreach (IntPtr s in good)
            {
                string had;
                if (!_sent.TryGetValue(s.ToInt64(), out had) || had != doc) needs.Add(s);
            }

            if (needs.Count == 0)
            {
                _pending = false;
            }
            else if (now - _lastSendMs < SendMinIntervalMs)
            {
                _pending = true; // the engine's fast timer calls Sync again
            }
            else
            {
                _pending = false;
                _lastSendMs = now;
                bool rejected = false;
                foreach (IntPtr s in needs)
                {
                    int r = SendRules(s, doc);
                    if (r < 0 && r != int.MinValue) rejected = true;
                    if (r != int.MinValue) _sent[s.ToInt64()] = doc; // a rejected document is not resent until it changes
                }
                _ruleError = rejected;
            }

            if (_ruleError) SetStatus(TapStatus.Rejected, "The taskbar helper couldn't read the style rules.");
            else SetStatus(TapStatus.Alive, "");
        }

        /// <summary>Uses STATS to say precisely why nothing is visible yet.</summary>
        private void RefineWithStats(List<IntPtr> good)
        {
            bool failed = false, progress = false;
            uint taskbarElems = 0;
            foreach (IntPtr s in good)
            {
                ulong st = SinkStats(s);
                if ((st & (1UL << 33)) != 0) failed = true;
                if ((st & (1UL << 34)) != 0) progress = true;
                uint tb = (uint)((st >> 16) & 0xFFFF);
                if (tb > taskbarElems) taskbarElems = tb; // control sink counts are process-wide
            }
            if (failed)
                SetStatus(TapStatus.Failed, "The taskbar helper loaded but Windows refused to let it watch the taskbar (advise failed). Restart Explorer and try again.");
            else if (progress)
                SetStatus(TapStatus.Injecting, "The taskbar helper is still connecting to the taskbar...");
            else if (taskbarElems == 0)
                SetStatus(TapStatus.Injecting, "The taskbar helper is running but hasn't found the taskbar yet.");
        }

        // ---- Injection ---------------------------------------------------------------

        private void StartInjection(string dll, uint pid)
        {
            _threadDone = false;
            _threadHr = 0;
            _threadStartMs = Clock.ElapsedMilliseconds;
            int gen = Interlocked.Increment(ref _attemptGen);
            Diagnostics.Log("inject start: explorer pid " + pid + " (age " + (int)ExplorerAgeSeconds(pid) + " s), attempt " + gen);
            Thread t = new Thread(delegate ()
            {
                int hr;
                string err = null;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(
                        System.Reflection.Assembly.GetExecutingAssembly().Location,
                        "--inject " + pid + " " + Endpoint + " \"" + dll + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi))
                    {
                        if (p.WaitForExit(15000)) hr = p.ExitCode;
                        else
                        {
                            try { p.Kill(); } catch { }
                            hr = unchecked((int)0x800705B4); // ERROR_TIMEOUT
                            err = "injector process timed out";
                        }
                    }
                }
                catch (Exception ex)
                {
                    hr = unchecked((int)0x80004005);
                    err = ex.Message;
                }
                Diagnostics.Log("inject result: attempt " + gen + " hr=0x" + hr.ToString("X8") + (err != null ? " (" + err + ")" : "") +
                    (gen != _attemptGen ? " [ignored: attempt was abandoned]" : ""));
                if (gen != _attemptGen) return; // abandoned attempt: ignore its result
                _threadHr = hr;
                _threadDone = true;
            });
            t.IsBackground = true;
            t.Name = "OpacityBarTapInject";
            t.Start();
            _thread = t;
        }

        private void CollectInjection(uint pid, long now)
        {
            if (_thread == null || !_threadDone) return;
            _thread = null;

            if (_threadHr >= 0)
            {
                _injectedOkPid = pid;
                _injectOkAtMs = now;
                _startingSinceMs = 0;
                // The sink appears asynchronously; poll soon and give it a few seconds before counting a failure.
                _status = TapStatus.Injecting;
                _lastPollMs = -100000;
                _failCount++;
                _nextAttemptMs = now + 4000;
                if (_failCount >= 5)
                {
                    SetStatus(TapStatus.Failed, "The taskbar helper loaded but never answered.");
                    _nextAttemptMs = now + 30000;
                }
                return;
            }

            if (_threadHr == E_NOT_FOUND_HR)
            {
                // Either the taskbar is still starting, or this Explorer already received an injection
                // (an earlier OpacityBar run) and Windows refuses another one. Retry for a while after Explorer
                // starts; after that, restart Explorer automatically.
                if (_startingSinceMs == 0) _startingSinceMs = now;
                if (ExplorerAgeSeconds(pid) > 45 && now - _startingSinceMs > 15000 &&
                    TryAutoRestart(now, "Reconnecting to the taskbar (restarting Explorer)..."))
                {
                    _startingSinceMs = 0;
                    return;
                }
                if (now - _startingSinceMs < 60000)
                {
                    SetStatus(TapStatus.Injecting, "Waiting for the taskbar to start...");
                    _nextAttemptMs = now + 1000;
                    return;
                }
            }
            else
            {
                _startingSinceMs = 0;
            }

            _failCount++;
            int delay = _failCount >= 5 ? 30000 : Math.Min(15000, 1000 << (_failCount - 1));
            _nextAttemptMs = now + delay;
            SetStatus(_failCount >= 5 ? TapStatus.Failed : TapStatus.Injecting,
                "Couldn't load the taskbar helper into Explorer (error 0x" + _threadHr.ToString("X8") + ")" +
                (_failCount >= 5 ? "." : "; retrying..."));
        }

        /// <summary>
        /// Body of "OpacityBar.exe --inject pid endpoint dll" (no UI, no tray icon): performs exactly one
        /// injection on this STA main thread, keeps the process alive 3 s while Explorer completes the
        /// connection, and returns the HRESULT as the exit code. A watchdog ends the process if the
        /// call itself hangs.
        /// </summary>
        public static int RunInjectorProcess(string pidText, string endpoint, string dll)
        {
            uint pid;
            if (!uint.TryParse(pidText, out pid) || pid == 0 || !File.Exists(dll))
                return unchecked((int)0x80070057); // E_INVALIDARG

            Thread watchdog = new Thread(delegate ()
            {
                Thread.Sleep(10000);
                Environment.Exit(unchecked((int)0x800705B4)); // ERROR_TIMEOUT
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            int hr;
            try { hr = NativeMethods.InitializeXamlDiagnosticsEx(endpoint, pid, null, dll, TapClsid, null); }
            catch { return unchecked((int)0x80004005); }
            if (hr >= 0)
            {
                // Let Explorer finish the handshake (it calls back into this process) before exiting.
                long until = Environment.TickCount + 3000;
                while (unchecked(Environment.TickCount - until) < 0)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
            }
            return hr;
        }

        // ---- Restore / dispose ------------------------------------------------------------

        /// <summary>Immediately restores the stock taskbar look in every live sink (any protocol version).</summary>
        public void Restore()
        {
            try
            {
                foreach (IntPtr s in FindSinks())
                {
                    int r;
                    Send(s, WM_TAP_RESTORE, out r);
                }
            }
            catch { }
            _desiredDoc = "";
            _sent.Clear();
            _pending = false;
            _ruleError = false;
        }

        /// <summary>
        /// Uninstall: asks the helper in Explorer to restore the taskbar, disconnect and free itself
        /// (protocol v3+). Returns true when no helper window is left. Explorer can't load a helper
        /// again until it restarts, so this is only for leaving for good.
        /// </summary>
        public static bool UnloadFromExplorer(int timeoutMs)
        {
            try
            {
                List<IntPtr> sinks = FindSinks();
                if (sinks.Count == 0) return true;
                foreach (IntPtr s in sinks)
                {
                    int r;
                    Send(s, WM_TAP_RESTORE, out r);
                }
                int done;
                Send(sinks[0], WM_TAP_UNLOAD, UIntPtr.Zero, IntPtr.Zero, 3000, out done); // any sink starts it for all
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (FindSinks().Count == 0) return true;
                    Thread.Sleep(100);
                }
                return FindSinks().Count == 0;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_receiver != null)
            {
                try { _receiver.Close(); } catch { }
                _receiver = null;
            }
        }

        // ---- Explorer restart ---------------------------------------------------------

        /// <summary>
        /// Ends the shell process the way Task Manager's "Restart" does (Windows brings a killed shell back;
        /// one that exits cleanly via WM_USER+436 does not), waits for the new taskbar, and starts
        /// explorer.exe itself if Windows doesn't. Runs on a worker thread; the normal Sync loop injects
        /// the helper into the new Explorer afterwards.
        /// </summary>
        public void RestartExplorer()
        {
            if (_restarting) return;
            _restarting = true;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                    uint pid = 0;
                    if (tray != IntPtr.Zero) NativeMethods.GetWindowThreadProcessId(tray, out pid);
                    if (pid != 0)
                    {
                        try { using (Process p = Process.GetProcessById((int)pid)) { p.Kill(); p.WaitForExit(5000); } }
                        catch { }
                    }

                    if (!WaitForNewShell(pid, 8000))
                    {
                        // Full path, so a different explorer.exe in the working directory or PATH is never used.
                        try { Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")); }
                        catch { }
                        WaitForNewShell(pid, 15000);
                    }
                }
                catch { }
                finally
                {
                    _failCount = 0;
                    _nextAttemptMs = 0;
                    _lastPollMs = -100000;
                    _restarting = false;
                }
            });
            t.IsBackground = true;
            t.Name = "OpacityBarRestartExplorer";
            t.Start();
        }

        /// <summary>Waits until a taskbar owned by a different process than oldPid exists.</summary>
        private static bool WaitForNewShell(uint oldPid, int timeoutMs)
        {
            long deadline = Clock.ElapsedMilliseconds + timeoutMs;
            while (Clock.ElapsedMilliseconds < deadline)
            {
                IntPtr nt = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (nt != IntPtr.Zero)
                {
                    uint npid;
                    NativeMethods.GetWindowThreadProcessId(nt, out npid);
                    if (npid != 0 && npid != oldPid) return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>
        /// Restarts Explorer without asking, rate-limited so a broken helper can never cause a restart loop:
        /// at most 3 automatic restarts per OpacityBar session, at least 2 minutes apart.
        /// </summary>
        private bool TryAutoRestart(long now, string detail)
        {
            if (_restarting) return true;
            if (_autoRestarts >= 3 || now - _lastAutoRestartMs < 120000)
            {
                Diagnostics.Log("auto-restart wanted but rate-limited (" + _autoRestarts + " so far): " + detail);
                return false;
            }
            Diagnostics.Log("auto-restart #" + (_autoRestarts + 1) + ": " + detail);
            _autoRestarts++;
            _lastAutoRestartMs = now;
            SetStatus(TapStatus.Injecting, detail);
            RestartExplorer();
            return true;
        }

        private static double ExplorerAgeSeconds(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                    return (DateTime.Now - p.StartTime).TotalSeconds;
            }
            catch { return 0; }
        }

        // ---- Taskbar map (diagnostics only) ---------------------------------------------

        private static string ReadWindowsBuild()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (k != null)
                    {
                        object b = k.GetValue("CurrentBuild");
                        object u = k.GetValue("UBR");
                        string s = b != null ? b.ToString() : "?";
                        if (u != null) s += "." + u.ToString();
                        return s;
                    }
                }
            }
            catch { }
            return "unknown";
        }

        /// <summary>Asks every sink for its XAML tree and writes opacitybar-taskbar-map.txt to the Desktop. UI thread only.</summary>
        public bool SaveTaskbarMap(out string path, out string error)
        {
            path = null;
            error = null;

            bool anyOld;
            List<IntPtr> good = FindLiveSinks(out anyOld);
            if (good.Count == 0)
            {
                error = anyOld
                    ? OutdatedDetail
                    : "The taskbar helper isn't running. Pick Clear, Color or Blur, wait a moment, and try again.";
                return false;
            }

            TapReceiver rx = Receiver;
            StringBuilder sb = new StringBuilder();
            int dpi = UiNative.SystemDpi();
            sb.AppendLine("opacbar-taskbar-map");
            sb.AppendLine("Windows build: " + ReadWindowsBuild());
            sb.AppendLine("DPI: " + dpi + " (" + Math.Round(dpi * 100.0 / 96.0) + "%)");
            sb.AppendLine("Monitors: " + Screen.AllScreens.Length);
            sb.AppendLine("Helper sinks: " + good.Count);
            sb.AppendLine("Saved: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            int got = 0;
            for (int i = 0; i < good.Count; i++)
            {
                rx.Dumps.Clear();
                int r;
                Send(good[i], WM_TAP_DUMP, HandleArg(rx.Handle), IntPtr.Zero, 15000, out r);
                sb.AppendLine();
                sb.AppendLine("## sink " + (i + 1) + " of " + good.Count + " (" + StatsText(SinkStats(good[i])) + ")");
                if (rx.Dumps.Count == 0)
                {
                    sb.AppendLine("(no data)");
                    continue;
                }
                foreach (string d in rx.Dumps) { sb.AppendLine(d.TrimEnd('\r', '\n')); got++; }
            }
            if (got == 0)
            {
                error = "The helper sent no taskbar data. Try again in a moment.";
                return false;
            }

            try
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "opacitybar-taskbar-map.txt");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                path = file;
                return true;
            }
            catch (Exception ex)
            {
                error = "Couldn't write the map (" + ex.Message + ").";
                return false;
            }
        }
    }
}
