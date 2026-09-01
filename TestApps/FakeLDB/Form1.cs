using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FakeLDB
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "/mode")
                Application.Run(new TargetForm(int.Parse(args[1])));
            else
                Application.Run(new LauncherForm());
        }
    }

    class LauncherForm : Form
    {
        static readonly string Exe = Process.GetCurrentProcess().MainModule.FileName;
        public LauncherForm()
        {
            Text = "FakeLDB Launcher"; Width = 640; Height = 400;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(20,20,20); ForeColor = Color.White;
            Controls.Add(new Label { Left=10,Top=10,Width=610,Height=18,
                Text="Each button spawns a separate process with different anti-injection defenses:",
                Font=new Font("Segoe UI",9), ForeColor=Color.Silver });
            Controls.Add(Btn(1,"Mode 1 — WDA only  (weakest)",Color.FromArgb(120,20,20),35));
            Controls.Add(Btn(2,"Mode 2 — WDA + ExtensionPointDisablePolicy",Color.FromArgb(100,0,100),83));
            Controls.Add(Btn(3,"Mode 3 — WDA + ExtensionPointDisable + MicrosoftSignedOnly",Color.FromArgb(0,70,130),131));
            Controls.Add(Btn(4,"Mode 4 — Mode 3 + Thread-Watcher poll  (race condition demo)",Color.FromArgb(80,50,0),179));
            Controls.Add(Btn(5,"Mode 5 — Mode 3 + RtlUserThreadStart hook  (real LDB technique)",Color.FromArgb(0,90,30),227));
            Controls.Add(Btn(6,"Mode 6 — Native subprocess + ACG + WDA(10ms)  (unbeatable user-mode)",Color.FromArgb(80,0,80),275));
            Controls.Add(new Label { Left=10,Top=333,Width=610,Height=30,
                Text="Use CaptureTest.exe to verify black/green. WdpHook bypasses 1-4; Mode 5 needs thread-hijack fix; Mode 6 requires kernel driver to bypass.",
                Font=new Font("Segoe UI",8), ForeColor=Color.DimGray });
        }
        Button Btn(int mode, string text, Color bg, int top)
        {
            var b = new Button { Text=text, Left=10, Top=top, Width=610, Height=42,
                BackColor=bg, ForeColor=Color.White, FlatStyle=FlatStyle.Flat,
                Font=new Font("Segoe UI",9.5f,FontStyle.Bold),
                TextAlign=ContentAlignment.MiddleLeft, Padding=new Padding(8,0,0,0) };
            b.Click += (s,e) => Process.Start(Exe, "/mode " + mode);
            return b;
        }
    }

    class TargetForm : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
        [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
        [DllImport("kernel32.dll")] static extern bool SetProcessMitigationPolicy(int p, ref uint b, uint l);
        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint m);
        [DllImport("ntdll.dll")]    static extern uint NtSetInformationProcess(IntPtr h, uint c, ref uint i, uint s);
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
        [DllImport("kernel32.dll")] static extern bool   Thread32First(IntPtr s, ref THREADENTRY32 t);
        [DllImport("kernel32.dll")] static extern bool   Thread32Next(IntPtr s, ref THREADENTRY32 t);
        [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint a, bool inh, uint tid);
        [DllImport("kernel32.dll")] static extern bool   TerminateThread(IntPtr h, uint c);
        [DllImport("kernel32.dll")] static extern bool   CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint   GetCurrentProcessId();
        [DllImport("ntdll.dll")]    static extern int    NtQueryInformationThread(IntPtr h,int c,out IntPtr i,uint s,out uint r);
        [DllImport("kernel32.dll")] static extern bool   GetModuleHandleEx(uint f, IntPtr a, out IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr h, string n);
        [DllImport("kernel32.dll")] static extern bool   VirtualProtect(IntPtr a, uint s, uint p, out uint old);
        [DllImport("kernel32.dll")] static extern void   ExitThread(uint c);

        const uint ProcessDefaultHardErrorMode=12, SEM_FAIL=1, SEM_NOOPEN=0x8000;
        const int  PolicyExtPoint=6, PolicySignature=8;
        const uint TH32CS_SNAPTHREAD=4, THREAD_ALL=0x1FFFFF;
        const int  ThreadStartAddr=9;
        const uint MOD_FROM_ADDR=4, MOD_NO_REF=2;

        [StructLayout(LayoutKind.Sequential)]
        struct THREADENTRY32 { public uint dwSize,cntUsage,th32ThreadID,th32OwnerProcessID; public int tpBasePri,tpDeltaPri; public uint dwFlags; }

        // Mode 5 delegates
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void RutsProc(IntPtr start, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint ThreadStartProc(IntPtr param);

        static readonly byte[] _origRuts = new byte[14];
        static IntPtr   _rutsAddr = IntPtr.Zero;
        static GCHandle _rutsGC;
        static volatile int _m5Killed;
        static TargetForm _inst;

        // Mode 6 — native subprocess with ACG
        Process _m6Proc;
        int     _m6HelperPid;
        Label   _m6Status;

        readonly int _mode;
        Label _lblTick, _lblWda, _lblMit;
        System.Windows.Forms.Timer _tick, _reapply;
        volatile bool _watcherStop;
        int _reapplyCount, _tickCount, _m4Killed;

        public TargetForm(int mode)
        {
            _inst = this; _mode = mode;
            Text = "FakeLDB Mode "+mode+" — "+ModeName(mode);
            Width=580; Height=400; BackColor=Color.FromArgb(18,18,18);
            StartPosition=FormStartPosition.Manual;
            Location=new System.Drawing.Point(50+mode*50, 50+mode*40);

            var title=new Label{Text="SECRET CONTENT\r\nMode "+mode+": "+ModeName(mode),
                Font=new Font("Segoe UI",12,FontStyle.Bold),ForeColor=Color.White,
                BackColor=ModeColor(mode),TextAlign=ContentAlignment.MiddleCenter,
                Dock=DockStyle.Top,Height=80};
            _lblTick=new Label{Text="TICK: 0",
                Font=new Font("Consolas",22,FontStyle.Bold),ForeColor=Color.Lime,
                BackColor=Color.FromArgb(10,10,10),TextAlign=ContentAlignment.MiddleCenter,
                Dock=DockStyle.Top,Height=52};
            _lblMit=new Label{Font=new Font("Segoe UI",7.5f),ForeColor=Color.Silver,
                BackColor=Color.FromArgb(12,12,12),TextAlign=ContentAlignment.MiddleLeft,
                Padding=new Padding(6,0,0,0),Dock=DockStyle.Bottom,Height=24};
            _lblWda=new Label{Font=new Font("Segoe UI",8.5f),ForeColor=Color.Tomato,
                BackColor=Color.FromArgb(15,15,15),TextAlign=ContentAlignment.MiddleLeft,
                Padding=new Padding(6,0,0,0),Dock=DockStyle.Bottom,Height=26};
            var info=new Label{Text=ModeInfo(mode),
                Font=new Font("Segoe UI",8.5f),ForeColor=Color.LightGray,
                BackColor=Color.FromArgb(22,22,22),Dock=DockStyle.Fill,
                Padding=new Padding(8),TextAlign=ContentAlignment.TopLeft};

            Controls.Add(info); Controls.Add(_lblTick); Controls.Add(title);
            Controls.Add(_lblMit); Controls.Add(_lblWda);

            _tick=new System.Windows.Forms.Timer{Interval=1000};
            _tick.Tick+=(s,e)=>{ _tickCount++; _lblTick.Text="TICK: "+_tickCount; RefreshWda(); RefreshMit(); };
            if (mode>=3 && mode!=6) {
                _reapply=new System.Windows.Forms.Timer{Interval=200};
                _reapply.Tick+=(s,e)=>{ SetWindowDisplayAffinity(Handle,1); _reapplyCount++; };
            }
            if (mode==6) {
                _m6Status=new Label{Font=new Font("Segoe UI",8.5f),ForeColor=Color.Cyan,
                    BackColor=Color.FromArgb(12,12,12),TextAlign=ContentAlignment.MiddleLeft,
                    Padding=new Padding(6,0,0,0),Dock=DockStyle.Bottom,Height=24};
                Controls.Add(_m6Status);
            }
            Load+=(s,e)=>{
                ApplyMitigations();
                if (mode!=6) SetWindowDisplayAffinity(Handle,1);
                RefreshWda(); RefreshMit();
                _tick.Start(); _reapply?.Start();
                if (mode==4) new Thread(WatcherLoop){IsBackground=true}.Start();
                if (mode==5) InstallRutsHook();
                if (mode==6) StartMode6Helper();
            };
            FormClosed+=(s,e)=>{
                _watcherStop=true; _tick.Stop(); _reapply?.Stop();
                if (mode==5) UninstallRutsHook();
                if (mode==6) StopMode6Helper();
            };
        }

        void ApplyMitigations()
        {
            if (_mode>=3 && _mode!=6) {
                SetErrorMode(SEM_FAIL|SEM_NOOPEN);
                uint sem=SEM_FAIL; NtSetInformationProcess((IntPtr)(-1),ProcessDefaultHardErrorMode,ref sem,4);
            }
            if (_mode>=2 && _mode!=6) { uint f=1; SetProcessMitigationPolicy(PolicyExtPoint,ref f,4); }
            if (_mode>=3 && _mode!=6) { uint f=1; SetProcessMitigationPolicy(PolicySignature,ref f,4); }
            // Mode 6: mitigations are applied inside mode6helper.exe (native process).
            // ACG cannot be set in a .NET process — the CLR JIT needs executable pages.
        }

        // ── Mode 4 ────────────────────────────────────────────────────────────
        void WatcherLoop()
        {
            Thread.Sleep(2000);
            var known=new HashSet<uint>(); uint self=GetCurrentProcessId(); SnapThreads(self,known);
            while (!_watcherStop) {
                Thread.Sleep(40);
                var cur=new HashSet<uint>(); SnapThreads(self,cur);
                foreach (uint tid in cur) {
                    if (known.Contains(tid)) continue;
                    IntPtr ht=OpenThread(THREAD_ALL,false,tid);
                    if (ht==IntPtr.Zero){known.Add(tid);continue;}
                    try {
                        IntPtr sa; uint r; NtQueryInformationThread(ht,ThreadStartAddr,out sa,8,out r);
                        IntPtr hm; bool inMod=GetModuleHandleEx(MOD_FROM_ADDR|MOD_NO_REF,sa,out hm);
                        if (!inMod){TerminateThread(ht,1);_m4Killed++;}else known.Add(tid);
                    } finally { CloseHandle(ht); }
                }
                known.IntersectWith(cur);
            }
        }
        static void SnapThreads(uint pid, HashSet<uint> set)
        {
            IntPtr s=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0); if(s==(IntPtr)(-1))return;
            try{ var te=new THREADENTRY32{dwSize=(uint)Marshal.SizeOf(typeof(THREADENTRY32))};
                 if(Thread32First(s,ref te))do{if(te.th32OwnerProcessID==pid)set.Add(te.th32ThreadID);}while(Thread32Next(s,ref te));}
            finally{CloseHandle(s);}
        }

        // ── Mode 5 ────────────────────────────────────────────────────────────
        // No trampoline: for legit threads, call startAddr(param) directly.
        // This is what RtlUserThreadStart→BaseThreadInitThunk does anyway.
        // Hook stays installed, so subsequent ManualMap attempts are also blocked.
        void InstallRutsHook()
        {
            IntPtr ntdll=GetModuleHandle("ntdll.dll");
            _rutsAddr=GetProcAddress(ntdll,"RtlUserThreadStart");
            if (_rutsAddr==IntPtr.Zero) return;

            Marshal.Copy(_rutsAddr,_origRuts,0,14);

            RutsProc hook=RutsHook;
            _rutsGC=GCHandle.Alloc(hook);
            long fn=Marshal.GetFunctionPointerForDelegate(hook).ToInt64();

            byte[] patch=new byte[14];
            patch[0]=0xFF; patch[1]=0x25;                             // JMP [RIP+0]
            Array.Copy(BitConverter.GetBytes(fn),0,patch,6,8);

            uint old;
            VirtualProtect(_rutsAddr,14,0x40,out old);
            Marshal.Copy(patch,0,_rutsAddr,14);
            VirtualProtect(_rutsAddr,14,old,out old);
        }

        static void RutsHook(IntPtr startAddr, IntPtr param)
        {
            IntPtr hMod;
            bool inModule;
            try { inModule=GetModuleHandleEx(MOD_FROM_ADDR|MOD_NO_REF,startAddr,out hMod); }
            catch { inModule=true; hMod=IntPtr.Zero; }

            if (!inModule) {
                Interlocked.Increment(ref _m5Killed);
                try { _inst?.BeginInvoke((Action)(()=>{ try{_inst?.RefreshMit();}catch{} })); } catch {}
                ExitThread(1);
                return;
            }

            // Legitimate CLR/OS thread: invoke its start function directly.
            // CLR threads call ExitThread internally so fn(param) never returns.
            try {
                var fn=Marshal.GetDelegateForFunctionPointer<ThreadStartProc>(startAddr);
                uint code=fn(param);
                ExitThread(code);
            } catch { ExitThread(0); }
        }

        void UninstallRutsHook()
        {
            if (_rutsAddr==IntPtr.Zero) return;
            uint old;
            VirtualProtect(_rutsAddr,14,0x40,out old);
            Marshal.Copy(_origRuts,0,_rutsAddr,14);
            VirtualProtect(_rutsAddr,14,old,out old);
            if (_rutsGC.IsAllocated) _rutsGC.Free();
            _rutsAddr=IntPtr.Zero;
        }

        // ── Mode 6 helper management ──────────────────────────────────────────
        void StartMode6Helper()
        {
            try {
                // mode6helper.exe ships alongside FakeLDB.exe in the same directory
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string exe = Path.Combine(dir, "mode6helper.exe");
                if (!File.Exists(exe)) {
                    if(_m6Status!=null) _m6Status.Text = "mode6helper.exe not found next to FakeLDB.exe";
                    return;
                }

                uint parentPid = GetCurrentProcessId();
                var psi = new ProcessStartInfo(exe, parentPid.ToString()) {
                    UseShellExecute=false, RedirectStandardOutput=true, CreateNoWindow=false };
                _m6Proc = Process.Start(psi);
                if (_m6Proc == null) { if(_m6Status!=null) _m6Status.Text="Helper: FAILED TO START"; return; }

                // Read PID from stdout (helper writes it as first line when window is ready)
                string line = _m6Proc.StandardOutput.ReadLine();
                _m6HelperPid = int.TryParse(line?.Trim(), out int pid) ? pid : 0;
                if(_m6Status!=null) {
                    _m6Status.Text = "Mode 6 helper: RUNNING  PID=" + _m6HelperPid +
                                     "  ACG+MicrosoftSigned+ExtPtDisable+WDA(10ms)";
                    _m6Status.ForeColor = Color.Cyan;
                }

                // Monitor helper exit on background thread
                new Thread(() => {
                    _m6Proc?.WaitForExit();
                    try { BeginInvoke((Action)(()=>{
                        if(_m6Status!=null) {
                            _m6Status.Text="Mode 6 helper: STOPPED (PID="+_m6HelperPid+")";
                            _m6Status.ForeColor=Color.Tomato;
                        }
                    })); } catch {}
                }) { IsBackground=true }.Start();
            } catch(Exception ex) {
                if(_m6Status!=null) _m6Status.Text="Mode 6 error: "+ex.Message;
            }
        }

        void StopMode6Helper()
        {
            try { if(_m6Proc!=null && !_m6Proc.HasExited) _m6Proc.Kill(); } catch {}
        }

        // ── UI ────────────────────────────────────────────────────────────────
        internal void RefreshWda()
        {
            if (_mode==6) {
                bool helperRunning = _m6Proc!=null && !_m6Proc.HasExited;
                _lblWda.Text = helperRunning
                    ? "Helper window: WDA_MONITOR (BLACK in capture) — re-applied every 10ms"
                    : "Helper not running";
                _lblWda.ForeColor = helperRunning ? Color.Tomato : Color.Gray;
                return;
            }
            GetWindowDisplayAffinity(Handle,out uint aff);
            bool clear=aff==0;
            string s="WDA=0x"+aff.ToString("X");
            if (_mode>=3) s+="  re-apply="+_reapplyCount;
            if (clear){s+="  ← CLEARED (GREEN in capture)";_lblWda.ForeColor=Color.LightGreen;}
            else      {s+="  (BLACK in capture)";          _lblWda.ForeColor=Color.Tomato;}
            _lblWda.Text=s;
        }
        internal void RefreshMit()
        {
            string s="Mitigations: WDA";
            if (_mode>=2 && _mode!=6) s+=" | ExtPointDisable";
            if (_mode>=3 && _mode!=6) s+=" | MicrosoftSignedOnly";
            if (_mode==4) s+=" | ThreadWatcher(poll) killed="+_m4Killed;
            if (_mode==5) s+=" | RUTS hook — killed="+_m5Killed+" shellcode thread(s)";
            if (_mode==6) s=" Mode 6 protections run in native subprocess (ACG incompatible with .NET CLR JIT)";
            _lblMit.Text=s;
        }
        static string ModeName(int m){
            if(m==1)return"WDA only";
            if(m==2)return"WDA + ExtensionPointDisable";
            if(m==3)return"WDA + ExtensionPointDisable + MicrosoftSignedOnly";
            if(m==4)return"Mode 3 + Thread-Watcher poll";
            if(m==5)return"Mode 3 + RtlUserThreadStart hook";
            return"Native subprocess + ACG + WDA(10ms)";
        }
        static Color ModeColor(int m){
            if(m==1)return Color.DarkRed;
            if(m==2)return Color.FromArgb(90,0,120);
            if(m==3)return Color.FromArgb(0,60,120);
            if(m==4)return Color.FromArgb(100,70,0);
            if(m==5)return Color.FromArgb(0,100,30);
            return Color.FromArgb(80,0,80);
        }
        static string ModeInfo(int m){
            if(m==1)return"WDA_MONITOR only.\nWH_GETMESSAGE injection → GREEN after WdpHook.";
            if(m==2)return"WDA + ExtensionPointDisablePolicy.\nWH_GETMESSAGE blocked. CreateRemoteThread fallback works.\nGREEN after ~3 s.";
            if(m==3)return"WDA + ExtensionPointDisable + MicrosoftSignedOnly.\nManualMap bypasses signature check → GREEN.";
            if(m==4)return"Mode 3 + thread-watcher poll every 40 ms.\nRace condition: shellcode runs in <1 ms, poll is too slow.\nWdpHook still bypasses. Shows polling limitation.";
            if(m==5)return"Mode 3 + RtlUserThreadStart hook (what real LDB agents use).\n"+
                   "EVERY thread Windows creates passes through RtlUserThreadStart in ntdll.\n"+
                   "Hook fires BEFORE shellcode runs — zero race condition.\n"+
                   "start addr NOT in any module → injected → ExitThread(1) silently.\n"+
                   "start addr in a module → CLR/OS thread → runs normally.\n"+
                   "Hook stays active for all subsequent injection attempts.\n"+
                   "Stays BLACK. Represents TOEFL, Respondus, SAT LDB user-mode agent.";
            return"Spawns mode6helper.exe — a native (non-.NET) Win32 process.\n"+
                   "ACG (ProcessDynamicCodePolicy) set before any thread:\n"+
                   "  VirtualAllocEx(PAGE_EXECUTE_READWRITE) → ERROR_ACCESS_DENIED\n"+
                   "  Thread-hijack shellcode, APC shellcode → ALL fail immediately.\n"+
                   "Also: MicrosoftSignedOnly + ExtensionPointDisable.\n"+
                   "WDA_MONITOR re-applied every 10 ms (vs WdpHook's 150 ms external clear).\n\n"+
                   "ACG cannot be used in this .NET process — CLR JIT needs executable pages.\n"+
                   "See the Mode 6 helper window for the protected content.\n"+
                   "Bypass requires a kernel driver (SSDT hook, ObRegisterCallbacks, or PPL).";
        }
    }
}
