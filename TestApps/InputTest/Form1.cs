using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace InputTest
{
    static class Program
    {
        [STAThread]
        static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm()); }
    }

    class MainForm : Form
    {
        RichTextBox _log;
        Label _hookStatus, _lblTarget;
        Button _btnSendKeys, _btnSendClick, _btnClear;
        CheckBox _chkOnlyInjected;

        static IntPtr _hKbd = IntPtr.Zero;
        static IntPtr _hMouse = IntPtr.Zero;
        // Keep delegates alive — GC cannot collect them while hooks are installed
        static Win32.HookProc _kbdProc, _mouseProc;
        static MainForm _inst;

        public MainForm()
        {
            _inst = this;
            Text = "Input Injection Test — WdpHook Verifier";
            Width = 800; Height = 620; MinimumSize = new Size(700, 500);

            // ── top panel ────────────────────────────────────────────────────
            var top = new Panel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(6) };

            _hookStatus = new Label { Left = 6, Top = 6, Width = 340, Height = 22, Font = new Font("Segoe UI", 10, FontStyle.Bold), Text = "LL Hooks: installing..." };

            _lblTarget = new Label { Left = 6, Top = 34, Width = 700, Height = 18, ForeColor = Color.DimGray, Text = "Target: (buttons below will send input 1.5 s after click — switch focus to LDB in that window)" };

            _btnSendKeys = new Button { Left = 6, Top = 58, Width = 220, Height = 26, Text = "Send Keys  »Hello World«  (1.5 s delay)" };
            _btnSendKeys.Click += (s, e) => ScheduleSend(SendTestKeys, "Sending keys 'Hello World'…");

            _btnSendClick = new Button { Left = 234, Top = 58, Width = 220, Height = 26, Text = "Send Left Mouse Click  (1.5 s delay)" };
            _btnSendClick.Click += (s, e) => ScheduleSend(SendTestMouse, "Sending mouse LButton click…");

            _btnClear = new Button { Left = 462, Top = 58, Width = 80, Height = 26, Text = "Clear Log" };
            _btnClear.Click += (s, e) => _log.Clear();

            _chkOnlyInjected = new CheckBox { Left = 555, Top = 62, Width = 200, Height = 20, Text = "Show only INJECTED events" };

            top.Controls.AddRange(new Control[] { _hookStatus, _lblTarget, _btnSendKeys, _btnSendClick, _btnClear, _chkOnlyInjected });

            // ── legend ───────────────────────────────────────────────────────
            var legend = new Panel { Dock = DockStyle.Bottom, Height = 52 };
            var leg = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 9),
                Text = "WITHOUT WdpHook → SendInput events appear  INJECTED  → LDB blocks them (keyboard/mouse stop working)\r\n" +
                       "WITH WdpHook running → wdpcore.dll injected here → events appear  HARDWARE ✓  → LDB passes them through",
                BackColor = Color.LightYellow };
            legend.Controls.Add(leg);

            // ── log ──────────────────────────────────────────────────────────
            _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(18, 18, 18), ForeColor = Color.White };

            Controls.Add(_log); Controls.Add(legend); Controls.Add(top);

            InstallHooks();
            FormClosed += (s, e) => RemoveHooks();
        }

        // ── hook install/remove ──────────────────────────────────────────────

        void InstallHooks()
        {
            _kbdProc   = KbdHookProc;
            _mouseProc = MouseHookProc;
            _hKbd   = Win32.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _kbdProc,   IntPtr.Zero, 0);
            _hMouse = Win32.SetWindowsHookEx(14 /*WH_MOUSE_LL*/,    _mouseProc, IntPtr.Zero, 0);
            bool ok = _hKbd != IntPtr.Zero && _hMouse != IntPtr.Zero;
            _hookStatus.Text      = ok ? "LL Hooks: ACTIVE ✓" : "LL Hooks: FAILED ✗  (err " + Marshal.GetLastWin32Error() + ")";
            _hookStatus.ForeColor = ok ? Color.Green : Color.Red;
            AppendLine("Hooks installed — kbd=" + _hKbd + "  mouse=" + _hMouse, Color.Gray);
        }

        void RemoveHooks()
        {
            if (_hKbd   != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_hKbd);   _hKbd   = IntPtr.Zero; }
            if (_hMouse != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_hMouse); _hMouse = IntPtr.Zero; }
        }

        // ── LL hook callbacks (called on this thread's message pump) ─────────

        static IntPtr KbdHookProc(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && l != IntPtr.Zero)
            {
                var ks = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(l);
                bool injected = (ks.flags & 0x10) != 0;
                bool lowerIL  = (ks.flags & 0x02) != 0;
                string state  = injected ? (lowerIL ? "INJECTED  (lower-IL)" : "INJECTED") : "HARDWARE ✓";
                string line   = string.Format("KBD   vk=0x{0:X2}  {1,-15}  flags=0x{2:X3}  →  {3}",
                    ks.vkCode, WmKbdName((uint)w.ToInt64()), ks.flags, state);
                Color c = injected ? Color.Tomato : Color.LightGreen;
                bool onlyInj = false;
                _inst?.Invoke((Action)(() => { onlyInj = _inst._chkOnlyInjected.Checked; }));
                if (!onlyInj || injected)
                    _inst?.BeginInvoke((Action)(() => _inst.AppendLine(line, c)));
            }
            return Win32.CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        static IntPtr MouseHookProc(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && l != IntPtr.Zero)
            {
                var ms = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(l);
                bool injected = (ms.flags & 0x01) != 0;
                bool lowerIL  = (ms.flags & 0x02) != 0;
                uint msg = (uint)w.ToInt64();
                // Log only button-down events to avoid flood from mouse moves
                if (msg == 0x201 || msg == 0x204 || msg == 0x207)
                {
                    string btn   = msg == 0x201 ? "LButton" : msg == 0x204 ? "RButton" : "MButton";
                    string state = injected ? (lowerIL ? "INJECTED  (lower-IL)" : "INJECTED") : "HARDWARE ✓";
                    string line  = string.Format("MOUSE {0,-8} pt=({1},{2})  flags=0x{3:X}  →  {4}",
                        btn, ms.pt_x, ms.pt_y, ms.flags, state);
                    Color c = injected ? Color.Orange : Color.LightGreen;
                    bool onlyInj = false;
                    _inst?.Invoke((Action)(() => { onlyInj = _inst._chkOnlyInjected.Checked; }));
                    if (!onlyInj || injected)
                        _inst?.BeginInvoke((Action)(() => _inst.AppendLine(line, c)));
                }
            }
            return Win32.CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        // ── scheduled send (fires after 1.5 s so user can switch to LDB) ────

        void ScheduleSend(Action action, string msg)
        {
            AppendLine("─── " + msg + "  (switch to LDB now!) ───", Color.DeepSkyBlue);
            System.Threading.Timer t = null;
            t = new System.Threading.Timer(_ => { t.Dispose(); action(); }, null, 1500, System.Threading.Timeout.Infinite);
        }

        static void SendTestKeys()
        {
            string text = "Hello World";
            var inputs = new Win32.INPUT[2];
            foreach (char ch in text)
            {
                inputs[0] = new Win32.INPUT(); inputs[0].type = 1; // KEYBOARD
                inputs[0].u.ki.wScan = (ushort)ch; inputs[0].u.ki.dwFlags = 4; // KEYEVENTF_UNICODE
                inputs[1] = new Win32.INPUT(); inputs[1].type = 1;
                inputs[1].u.ki.wScan = (ushort)ch; inputs[1].u.ki.dwFlags = 4 | 2; // KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                Win32.SendInput(2, inputs, Marshal.SizeOf(typeof(Win32.INPUT)));
                Thread.Sleep(20);
            }
        }

        static void SendTestMouse()
        {
            var inputs = new Win32.INPUT[2];
            inputs[0] = new Win32.INPUT(); inputs[0].type = 0; // MOUSE
            inputs[0].u.mi.dwFlags = 0x0002; // MOUSEEVENTF_LEFTDOWN
            inputs[1] = new Win32.INPUT(); inputs[1].type = 0;
            inputs[1].u.mi.dwFlags = 0x0004; // MOUSEEVENTF_LEFTUP
            Win32.SendInput(2, inputs, Marshal.SizeOf(typeof(Win32.INPUT)));
        }

        // ── log helper ───────────────────────────────────────────────────────

        void AppendLine(string msg, Color c)
        {
            if (_log.Lines.Length > 500) { _log.Clear(); AppendLine("(log cleared — limit reached)", Color.Gray); }
            int start = _log.TextLength;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\n");
            _log.Select(start, _log.TextLength - start);
            _log.SelectionColor = c;
            _log.SelectionLength = 0;
            _log.ScrollToCaret();
        }

        static string WmKbdName(uint msg)
        {
            if (msg == 0x100) return "WM_KEYDOWN";
            if (msg == 0x101) return "WM_KEYUP";
            if (msg == 0x104) return "WM_SYSKEYDOWN";
            if (msg == 0x105) return "WM_SYSKEYUP";
            return "0x" + msg.ToString("X");
        }
    }

    static class Win32
    {
        public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public int pt_x, pt_y; public uint mouseData, flags, time; public UIntPtr extra; }

        // INPUT union — matches WINAPI INPUT (40 bytes on x64)
        [StructLayout(LayoutKind.Sequential)]  public struct MOUSEINPUT  { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]  public struct KEYBDINPUT  { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]  public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public INPUTUNION u; }
    }
}
