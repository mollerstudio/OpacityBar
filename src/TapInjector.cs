using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Opacbar
{
    public enum TapStatus
    {
        Idle = 0,       // nothing needed yet
        Missing = 1,    // helper DLL not found / unsupported arch
        Injecting = 2,  // injection in flight or waiting for the sink to appear
        Alive = 3,      // at least one sink answered the ping
        Failed = 4      // injection failed repeatedly
    }

    // Loads OpacbarTAP.<arch>.dll into explorer.exe through the XAML diagnostics API and talks to it
    // via message-only "OpacbarTAP.Sink" windows.
    public sealed class TapHelper
    {
        private const uint WM_APP = 0x8000;
        private const uint WM_TAP_APPLY = WM_APP + 0x10;
        private const uint WM_TAP_RESTORE = WM_APP + 0x11;
        private const uint WM_TAP_PING = WM_APP + 0x12;
        private const int PING_REPLY = 0x0BAC;
        private const string SinkClass = "OpacbarTAP.Sink";
        private static readonly Guid TapClsid = new Guid("6C3B1F0A-3D52-4E8B-9A41-0B5C2E7D9F11");

        private static int _endpointCounter = 0;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private TapStatus _status = TapStatus.Idle;
        private string _detail = "";

        // desired state
        private bool _wantActive;
        private uint _wantArgb;
        private uint _wantFlags;
        private string _desiredKey = "";

        // applied state
        private string _appliedKey = "";
        private string _appliedSinkKey = "";

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

        public TapStatus Status { get { return _status; } }
        public string Detail { get { return _detail; } }

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
            string dst = Path.Combine(dir, "OpacbarTAP." + arch + "." + hash + ".dll");
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

        private static bool Send(IntPtr hwnd, uint msg, uint wParam, uint lParam, out int result)
        {
            UIntPtr res;
            IntPtr ok = NativeMethods.SendMessageTimeout(hwnd, msg, new UIntPtr(wParam), new IntPtr((int)lParam),
                NativeMethods.SMTO_ABORTIFHUNG, 500, out res);
            result = unchecked((int)res.ToUInt64());
            return ok != IntPtr.Zero;
        }

        private static List<IntPtr> AliveSinks()
        {
            List<IntPtr> alive = new List<IntPtr>();
            foreach (IntPtr s in FindSinks())
            {
                int r;
                if (Send(s, WM_TAP_PING, 0, 0, out r) && r == PING_REPLY) alive.Add(s);
            }
            return alive;
        }

        // Call from the UI timer. active=false asks the helper to restore the stock look.
        public void Sync(uint explorerPid, bool active, uint argb, uint flags)
        {
            string key = active ? ("A" + argb + "/" + flags) : "R";
            bool dirty = (key != _desiredKey);
            _desiredKey = key;
            _wantActive = active;
            _wantArgb = argb;
            _wantFlags = flags;

            long now = Clock.ElapsedMilliseconds;
            CollectInjection(explorerPid, now);

            if (!dirty && now - _lastPollMs < 1000) return;
            _lastPollMs = now;

            List<IntPtr> sinks = AliveSinks();
            if (sinks.Count > 0)
            {
                _failCount = 0;
                _status = TapStatus.Alive;
                _detail = "";
                StringBuilder sk = new StringBuilder();
                foreach (IntPtr s in sinks) sk.Append(s.ToInt64()).Append(',');
                string sinkKey = sk.ToString();
                if (_appliedKey != _desiredKey || _appliedSinkKey != sinkKey)
                {
                    SendToAll(sinks, active, argb, flags);
                    _appliedKey = _desiredKey;
                    _appliedSinkKey = sinkKey;
                }
                return;
            }

            _appliedSinkKey = "";
            _appliedKey = "";
            if (!active)
            {
                if (_status == TapStatus.Alive) _status = TapStatus.Idle;
                return;
            }

            if (_thread != null) return; // injection running; wait for it
            if (now < _nextAttemptMs) return;

            string dll;
            try { dll = PrepareDll(); }
            catch (Exception ex)
            {
                _status = TapStatus.Failed;
                _detail = "Couldn't prepare the transparency helper (" + ex.Message + ") — pill shape still works.";
                _nextAttemptMs = now + 30000;
                return;
            }
            if (dll == null)
            {
                _status = TapStatus.Missing;
                _detail = (ArchName() == null)
                    ? "This processor type isn't supported by the transparency helper — pill shape still works."
                    : "Transparency helper not found next to Opacbar.exe — pill shape still works.";
                _nextAttemptMs = now + 5000;
                return;
            }
            if (explorerPid == 0) return;

            StartInjection(dll, explorerPid);
            _status = TapStatus.Injecting;
            _detail = "Starting the transparency helper…";
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
            _thread = t;
            t.Start();
        }

        private void CollectInjection(uint pid, long now)
        {
            if (_thread == null || !_threadDone) return;
            _thread = null;

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
                    _detail = "The transparency helper loaded but never answered — pill shape still works.";
                    _nextAttemptMs = now + 30000;
                }
            }
            else
            {
                _failCount++;
                int delay = _failCount >= 5 ? 30000 : Math.Min(15000, 1000 << (_failCount - 1));
                _nextAttemptMs = now + delay;
                _status = _failCount >= 5 ? TapStatus.Failed : TapStatus.Injecting;
                _detail = "Couldn't load the transparency helper into Explorer (error 0x" +
                          _threadHr.ToString("X8") + ")" +
                          (_failCount >= 5 ? " — pill shape still works." : "; retrying…");
            }
        }

        private static void SendToAll(List<IntPtr> sinks, bool active, uint argb, uint flags)
        {
            foreach (IntPtr s in sinks)
            {
                int r;
                if (active) Send(s, WM_TAP_APPLY, argb, flags, out r);
                else Send(s, WM_TAP_RESTORE, 0, 0, out r);
            }
        }

        // Immediately restore the stock taskbar look in every live sink.
        public void Restore()
        {
            try
            {
                List<IntPtr> sinks = FindSinks();
                foreach (IntPtr s in sinks)
                {
                    int r;
                    Send(s, WM_TAP_RESTORE, 0, 0, out r);
                }
            }
            catch { }
            _wantActive = false;
            _desiredKey = "R";
            _appliedKey = "";
            _appliedSinkKey = "";
        }
    }
}
