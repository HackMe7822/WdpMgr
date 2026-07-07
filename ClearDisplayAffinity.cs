using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClearDisplayAffinity
{
    internal static class Program
    {
        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_MONITOR = 0x00000001;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static string AffinityName(uint affinity)
        {
            switch (affinity)
            {
                case WDA_NONE: return "WDA_NONE";
                case WDA_MONITOR: return "WDA_MONITOR";
                case WDA_EXCLUDEFROMCAPTURE: return "WDA_EXCLUDEFROMCAPTURE";
                default: return "0x" + affinity.ToString("X");
            }
        }

        private static int ScanAndClear(bool listOnly)
        {
            int scanned = 0, found = 0, cleared = 0, failed = 0;

            EnumWindows((hWnd, lParam) =>
            {
                scanned++;
                uint affinity;
                if (!GetWindowDisplayAffinity(hWnd, out affinity))
                    return true; // skip windows we can't query

                if (affinity == WDA_NONE)
                    return true;

                found++;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string procName = "unknown";
                try { procName = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
                catch { /* process may have exited between enum and lookup */ }

                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) title = "(no title)";

                Console.WriteLine(string.Format("[FOUND] PID {0} ({1}) - \"{2}\" - affinity={3}",
                    pid, procName, title, AffinityName(affinity)));

                if (!listOnly)
                {
                    if (SetWindowDisplayAffinity(hWnd, WDA_NONE))
                    {
                        Console.WriteLine("        -> cleared");
                        cleared++;
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        Console.WriteLine("        -> FAILED to clear (Win32 error " + err + ")");
                        failed++;
                    }
                }

                return true;
            }, IntPtr.Zero);

            Console.WriteLine();
            Console.WriteLine(string.Format(
                "Scanned {0} windows. Found {1} with capture-exclusion set.{2}",
                scanned, found,
                listOnly ? "" : string.Format(" Cleared {0}, failed {1}.", cleared, failed)));

            return found;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("ClearDisplayAffinity - detects and clears WDA_EXCLUDEFROMCAPTURE/WDA_MONITOR on windows");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ClearDisplayAffinity.exe            Scan once and clear any capture-exclusion found");
            Console.WriteLine("  ClearDisplayAffinity.exe -list      Scan once and report only, no changes made");
            Console.WriteLine("  ClearDisplayAffinity.exe -watch     Continuously scan+clear every 2 seconds (Ctrl+C to stop)");
            Console.WriteLine("  ClearDisplayAffinity.exe -help      Show this message");
        }

        private static int Main(string[] args)
        {
            bool listOnly = false;
            bool watch = false;

            foreach (var arg in args)
            {
                var a = arg.ToLowerInvariant();
                if (a == "-list" || a == "/list" || a == "-listonly") listOnly = true;
                else if (a == "-watch" || a == "/watch") watch = true;
                else if (a == "-help" || a == "/help" || a == "-h" || a == "/?")
                {
                    PrintUsage();
                    return 0;
                }
            }

            if (!watch)
            {
                Console.WriteLine(listOnly ? "Scanning for windows with capture-exclusion set (report only)..." : "Scanning and clearing capture-exclusion on windows...");
                Console.WriteLine();
                ScanAndClear(listOnly);
                return 0;
            }

            Console.WriteLine("Watch mode: scanning every 2 seconds. Press Ctrl+C to stop.");
            Console.WriteLine();
            while (true)
            {
                Console.WriteLine("--- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ---");
                ScanAndClear(listOnly);
                Console.WriteLine();
                Thread.Sleep(2000);
            }
        }
    }
}
