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

namespace Opacbar
{
    public enum TapStatus
    {
        Idle = 0,       // nothing needed yet
        Missing = 1,    // helper DLL not found / unsupported arch
        Injecting = 2,  // injection in flight or waiting for the sink to appear
        Alive = 3,      // at least one up-to-date sink answered
        Failed = 4,     // injection failed repeatedly
        Outdated = 5,   // an old (v1) helper is still loaded in Explorer
        Rejected = 6    // the helper could not parse our rules
    }

    /// <summary>Hidden window that receives the helper's WM_COPYDATA tree dumps ('OTRE').</summary>
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

    // Loads OpacbarTAP.<arch>.dll into explorer.exe through the XAML diagnostics API and talks to it
    // via message-only "OpacbarTAP.Sink" windows (protocol v2, see native/PROTOCOL.md).
    public sealed class TapHelper
    {
        private const uint WM_APP = 0x8000;
        private const uint WM_TAP_RESTORE = WM_APP + 0x11;
        private const uint WM_TAP_PING = WM_APP + 0x12;
        private const uint WM_TAP_DUMP = WM_APP + 0x14;
        private const uint WM_TAP_VERSION = WM_APP + 0x15;
        private const int PING_REPLY = 0x0BAC;
        // Must equal OPAC_PROTOCOL_VERSION in native/OpacbarTAP.cpp. An older helper found in Explorer
        // triggers an automatic Explorer restart (Windows accepts only ONE injection per Explorer process:
        // later InitializeXamlDiagnosticsEx calls into the same process fail with 0x80070490).
        private const int ProtocolVersion = 4;
        private uint _injectedOkPid;            // Explorer we already injected successfully (never inject twice)
        private long _injectOkAtMs;
        private long _lastAutoRestartMs = -10000000;
        private int _autoRestarts;
        private const uint WM_TAP_STATS = WM_APP + 0x16;
        private const uint WM_TAP_UNLOAD = WM_APP + 0x17;
        private const int E_NOT_FOUND_HR = unchecked((int)0x80070490);
        private const long RulesId = 0x4F504143; // 'OPAC'
        private const long SendMinIntervalMs = 33; // ~30 sends/s while dragging
        private const string SinkClass = "OpacbarTAP.Sink";
        private const string OutdatedDetail = "Opacbar was updated, but Windows is still running the old version inside the taskbar. Restart Explorer once to load the new one; your taskbar disappears for a second and comes back.";
        private static readonly Guid TapClsid = new Guid("6C3B1F0A-3D52-4E8B-9A41-0B5C2E7D9F11");

        private static int _endpointCounter = 0;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private TapStatus _status = TapStatus.Idle;
        private string _detail = "";

        // desired state
        private bool _wantStyled;
        private string _desiredDoc = "";

        // what each sink last received
        private readonly Dictionary<long, string> _sent = new Dictionary<long, string>();
        private long _lastSendMs = -100000;
        private bool _pending;
        private bool _ruleError;

        private long _lastPollMs = -100000;
        private long _nextAttemptMs = 0;
        private int _failCount = 0;
        private string _preparedDll;

        // injection thread
        private Thread _thread;
        private volatile bool _threadDone;
        private int _threadHr;
        private string _threadError;
        private uint _injectedPid;

        // startup unload of a helper left behind by an older Opacbar (protocol >= 3 can unload itself)
        private bool _startupUnloaded;
        private bool _unloading;
        private bool _unloadFailed;
        private int _unloadTries;
        private long _unloadDeadlineMs;
        private long _nextUnloadMs;
        private long _startingSinceMs;
        private uint _lastPid;

        private volatile bool _restarting;
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

        private string PrepareDll()
        {
            if (_preparedDll != null && File.Exists(_preparedDll)) return _preparedDll;
            string arch = ArchName();
            if (arch == null) return null;

            string src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpacbarTAP." + arch + ".dll");
            if (!File.Exists(src)) return null;

            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < h.Length && sb.Length < 12; i++) sb.Append(h[i].ToString("x2"));
                hash = sb.ToString().Substring(0, 12);
            }

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opacbar", "helper");
            Directory.CreateDirectory(dir);
            // A helper that unloaded itself stays inert if injected again, so every Opacbar run injects a freshly named copy.
            string dst = Path.Combine(dir, "OpacbarTAP." + arch + "." + hash + "." + Process.GetCurrentProcess().Id + ".dll");
            try
            {
                foreach (string old in Directory.GetFiles(dir, "OpacbarTAP." + arch + ".*.dll"))
                {
                    if (!string.Equals(old, dst, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch { } }
                }
            }
            catch { }
            if (!File.Exists(dst))
            {
                string tmp = dst + "." + Process.GetCurrentProcess().Id + ".tmp";
                File.Copy(src, tmp, true);
                try { File.Move(tmp, dst); }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    if (!File.Exists(dst)) throw;
                }
            }
            _preparedDll = dst;
            return dst;
        }

        // ---- sinks and messages ------------------------------------------------------

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
            int byteCount = doc.Length * 2;
            IntPtr text = Marshal.StringToHGlobalUni(doc);
            IntPtr cds = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.COPYDATASTRUCT)));
            try
            {
                NativeMethods.COPYDATASTRUCT c = new NativeMethods.COPYDATASTRUCT();
                c.dwData = new IntPtr(RulesId);
                c.cbData = byteCount;
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

        // Call from the UI timer. styled=false means "stock look": no injection is needed and an empty document is sent.
        public void Sync(uint explorerPid, bool styled, string doc)
        {
            if (doc == null) doc = TaskbarStyle.EmptyDocument();
            bool dirty = (doc != _desiredDoc);
            _desiredDoc = doc;
            _wantStyled = styled;

            long now = Clock.ElapsedMilliseconds;
            CollectInjection(explorerPid, now);

            if (explorerPid != 0 && explorerPid != _lastPid)
            {
                // New Explorer: forget old backoff; the new shell has no helper yet.
                _lastPid = explorerPid;
                _failCount = 0;
                _nextAttemptMs = 0;
                _startingSinceMs = 0;
                _startupUnloaded = false;
                _unloading = false;
                _unloadFailed = false;
                _unloadTries = 0;
                _lastPollMs = -100000;
            }

            if (_restarting) return;
            if (!dirty && !_pending && now - _lastPollMs < 1000) return;
            _lastPollMs = now;

            List<IntPtr> good = new List<IntPtr>();
            bool anyOld = false;
            foreach (IntPtr s in FindSinks())
            {
                int r;
                if (!Send(s, WM_TAP_PING, out r) || r != PING_REPLY) continue;
                if (SinkVersion(s) >= ProtocolVersion) good.Add(s);
                else anyOld = true;
            }

            if (_unloading)
            {
                if (FindSinks().Count == 0)
                {
                    _unloading = false;
                    _startupUnloaded = true;
                    good.Clear();
                    anyOld = false;
                }
                else if (now > _unloadDeadlineMs)
                {
                    _unloading = false;
                    _unloadFailed = true;
                }
                else
                {
                    _status = TapStatus.Injecting;
                    _detail = "Updating the taskbar helper...";
                    _lastPollMs = now - 800; // poll again soon
                    return;
                }
            }

            // A current-version helper is reused as-is: unloading and re-injecting is impossible because
            // Windows refuses a second injection into the same Explorer (0x80070490).
            if (false)
            {
                if (_unloadFailed)
                {
                    _status = TapStatus.Outdated;
                    _detail = OutdatedDetail;
                    return;
                }
                if (now >= _nextUnloadMs)
                {
                    int ur;
                    bool sent = Send(good[0], WM_TAP_UNLOAD, UIntPtr.Zero, IntPtr.Zero, 5000, out ur);
                    if (sent && ur == 1)
                    {
                        _unloading = true;
                        _unloadDeadlineMs = Clock.ElapsedMilliseconds + 5000;
                        _lastPollMs = now - 800;
                        _status = TapStatus.Injecting;
                        _detail = "Updating the taskbar helper...";
                        return;
                    }
                    _unloadTries++;
                    _nextUnloadMs = Clock.ElapsedMilliseconds + 2000;
                    if (_unloadTries >= 5) _unloadFailed = true;
                }
                _status = _unloadFailed ? TapStatus.Outdated : TapStatus.Injecting;
                _detail = _unloadFailed ? OutdatedDetail : "Updating the taskbar helper...";
                _lastPollMs = now - 800;
                return;
            }

            if (good.Count > 0)
            {
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

                if (needs.Count > 0)
                {
                    if (now - _lastSendMs < SendMinIntervalMs)
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
                }
                else
                {
                    _pending = false;
                }

                if (_ruleError)
                {
                    _status = TapStatus.Rejected;
                    _detail = "The taskbar helper couldn't read the style rules.";
                }
                else
                {
                    _status = TapStatus.Alive;
                    _detail = "";
                    if (styled) RefineWithStats(good);
                }
                return;
            }

            _sent.Clear();
            _pending = false;

            if (anyOld)
            {
                if (styled)
                {
                    // Older helper still loaded in Explorer: restart Explorer automatically to load ours.
                    if (!TryAutoRestart(now, "Updating the taskbar helper (restarting Explorer)..."))
                    {
                        _status = TapStatus.Outdated;
                        _detail = OutdatedDetail;
                    }
                }
                else if (_status == TapStatus.Outdated || _status == TapStatus.Alive)
                {
                    _status = TapStatus.Idle;
                    _detail = "";
                }
                return;
            }

            if (!styled)
            {
                if (_status == TapStatus.Alive || _status == TapStatus.Outdated || _status == TapStatus.Rejected)
                {
                    _status = TapStatus.Idle;
                    _detail = "";
                }
                return;
            }

            if (_status == TapStatus.Outdated || _status == TapStatus.Rejected || _status == TapStatus.Alive)
            {
                _status = TapStatus.Idle;
                _detail = "";
            }

            if (_thread != null) return; // injection running; wait for it

            // Already injected into this Explorer: never call InitializeXamlDiagnosticsEx again (it would
            // fail with 0x80070490). Give the helper time to start; if it never answers, restart Explorer.
            if (explorerPid != 0 && explorerPid == _injectedOkPid)
            {
                if (now - _injectOkAtMs < 15000)
                {
                    _status = TapStatus.Injecting;
                    _detail = "Starting the taskbar helper...";
                    return;
                }
                if (!TryAutoRestart(now, "The taskbar helper didn't start; restarting Explorer..."))
                {
                    _status = TapStatus.Failed;
                    _detail = "The taskbar helper loaded but never answered. Use Quit, then start Opacbar again.";
                }
                return;
            }

            if (now < _nextAttemptMs) return;

            string dll;
            try { dll = PrepareDll(); }
            catch (Exception ex)
            {
                _status = TapStatus.Failed;
                _detail = "Couldn't prepare the taskbar helper (" + ex.Message + ").";
                _nextAttemptMs = now + 30000;
                return;
            }
            if (dll == null)
            {
                _status = TapStatus.Missing;
                _detail = (ArchName() == null)
                    ? "This processor type isn't supported by the taskbar helper."
                    : "Taskbar helper not found next to Opacbar.exe.";
                _nextAttemptMs = now + 5000;
                return;
            }
            if (explorerPid == 0) return;

            StartInjection(dll, explorerPid);
            _status = TapStatus.Injecting;
            _detail = "Starting the taskbar helper...";
        }

        // Uses STATS to say precisely why nothing is visible yet.
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
            {
                _status = TapStatus.Failed;
                _detail = "The taskbar helper loaded but Windows refused to let it watch the taskbar (advise failed). Restart Explorer and try again.";
            }
            else if (progress)
            {
                _status = TapStatus.Injecting;
                _detail = "The taskbar helper is still connecting to the taskbar...";
            }
            else if (taskbarElems == 0)
            {
                _status = TapStatus.Injecting;
                _detail = "The taskbar helper is running but hasn't found the taskbar yet.";
            }
        }

        private void StartInjection(string dll, uint pid)
        {
            _threadDone = false;
            _threadHr = 0;
            _threadError = null;
            _injectedPid = pid;
            string endpoint = "VisualDiagConnection" + Interlocked.Increment(ref _endpointCounter);
            Thread t = new Thread(delegate ()
            {
                try
                {
                    _threadHr = NativeMethods.InitializeXamlDiagnosticsEx(endpoint, pid, null, dll, TapClsid, null);
                }
                catch (Exception ex)
                {
                    _threadHr = unchecked((int)0x80004005);
                    _threadError = ex.Message;
                }
                _threadDone = true;
            });
            t.IsBackground = true;
            t.Name = "OpacbarTapInject";
            // InitializeXamlDiagnosticsEx fails with 0x80070490 (ERROR_NOT_FOUND) from an MTA thread on
            // Win11 ARM64 build 26300; the same call succeeds from an STA thread. Keep this STA.
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            t.Start();
        }

        private void CollectInjection(uint pid, long now)
        {
            if (_thread == null || !_threadDone) return;
            _thread = null;

            if (_threadHr >= 0)
            {
                _startupUnloaded = true; // our own copy is current
                _injectedOkPid = pid;
                _injectOkAtMs = now;
            }
            if (_threadHr == E_NOT_FOUND_HR)
            {
                // Either the taskbar is still starting, or this Explorer already received an injection
                // (an earlier Opacbar run / an old helper) and Windows refuses another one. Wait a little
                // after Explorer starts; after that, restart Explorer automatically.
                if (_startingSinceMs == 0) _startingSinceMs = now;
                if (ExplorerAgeSeconds(pid) > 20 && now - _startingSinceMs > 3000 &&
                    TryAutoRestart(now, "Reconnecting to the taskbar (restarting Explorer)..."))
                {
                    _startingSinceMs = 0;
                    return;
                }
                if (now - _startingSinceMs < 60000)
                {
                    _status = TapStatus.Injecting;
                    _detail = "Waiting for the taskbar to start...";
                    _nextAttemptMs = now + 1000;
                    return;
                }
            }
            else
            {
                _startingSinceMs = 0;
            }

            if (_threadHr >= 0)
            {
                // The sink appears asynchronously; poll soon and give it a few seconds before counting a failure.
                _status = TapStatus.Injecting;
                _lastPollMs = -100000;
                _failCount++;
                _nextAttemptMs = now + 4000;
                if (_failCount >= 5)
                {
                    _status = TapStatus.Failed;
                    _detail = "The taskbar helper loaded but never answered.";
                    _nextAttemptMs = now + 30000;
                }
            }
            else
            {
                _failCount++;
                int delay = _failCount >= 5 ? 30000 : Math.Min(15000, 1000 << (_failCount - 1));
                _nextAttemptMs = now + delay;
                _status = _failCount >= 5 ? TapStatus.Failed : TapStatus.Injecting;
                _detail = "Couldn't load the taskbar helper into Explorer (error 0x" +
                          _threadHr.ToString("X8") + ")" + (_failCount >= 5 ? "." : "; retrying...");
            }
        }

        // Immediately restore the stock taskbar look in every live sink (any protocol version).
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
        /// Exits Explorer gracefully (WM_USER+436 to the taskbar), waits up to 3 s for a new shell, and starts one if needed.
        /// Runs on a worker thread; the helper is re-injected by the normal Sync loop afterwards.
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

                    // End the shell process the way Task Manager's "Restart" does. Windows restarts a shell
                    // that was terminated (AutoRestartShell), but NOT one that exited cleanly (WM_USER+436),
                    // which previously left the user without a taskbar.
                    if (pid != 0)
                    {
                        try { using (Process p = Process.GetProcessById((int)pid)) { p.Kill(); p.WaitForExit(5000); } }
                        catch { }
                    }

                    if (!WaitForNewShell(pid, 8000))
                    {
                        // Windows did not bring the shell back: start it ourselves (full path, so we never
                        // pick up a different explorer.exe from the working directory or PATH).
                        try
                        {
                            string exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                            Process.Start(exe);
                        }
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
            t.Name = "OpacbarRestartExplorer";
            t.Start();
        }

        public bool IsRestarting { get { return _restarting; } }

        // Waits until a taskbar owned by a different process than oldPid exists.
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
        /// at most 3 automatic restarts per Opacbar session, at least 2 minutes apart.
        /// </summary>
        private bool TryAutoRestart(long now, string detail)
        {
            if (_restarting) return true;
            if (_autoRestarts >= 3 || now - _lastAutoRestartMs < 120000) return false;
            _autoRestarts++;
            _lastAutoRestartMs = now;
            _status = TapStatus.Injecting;
            _detail = detail;
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

        // ---- Taskbar map (developer) --------------------------------------------------

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

        /// <summary>Asks every sink for its XAML tree and writes opacbar-taskbar-map.txt to the Desktop. UI thread only.</summary>
        public bool SaveTaskbarMap(out string path, out string error)
        {
            path = null;
            error = null;

            List<IntPtr> good = new List<IntPtr>();
            bool anyOld = false;
            foreach (IntPtr s in FindSinks())
            {
                int r;
                if (!Send(s, WM_TAP_PING, out r) || r != PING_REPLY) continue;
                if (SinkVersion(s) >= ProtocolVersion) good.Add(s);
                else anyOld = true;
            }
            if (good.Count == 0)
            {
                error = anyOld
                    ? OutdatedDetail + ", then try again."
                    : "The taskbar helper isn't running. Pick Clear, Color, Blur or Acrylic, wait a moment, and try again.";
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
                string dir = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "Desktop");
                if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string file = Path.Combine(dir, "opacbar-taskbar-map.txt");
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

