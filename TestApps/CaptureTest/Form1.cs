using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CaptureTest
{
    static class Program
    {
        [STAThread]
        static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm()); }
    }

    class MainForm : Form
    {
        ListBox _winList;
        Label _l1, _l2, _l3;
        PictureBox _pb1, _pb2, _pb3;
        Button _btnRefresh, _btnCapture;
        List<(IntPtr hwnd, string title)> _windows = new List<(IntPtr, string)>();

        public MainForm()
        {
            Text = "WDA Capture Test — DLL Injection Verifier";
            Width = 960; Height = 640; MinimumSize = new Size(800, 500);

            var top = new Panel { Dock = DockStyle.Top, Height = 115, Padding = new Padding(6) };

            _btnRefresh = new Button { Text = "Refresh Windows", Left = 6, Top = 6, Width = 130, Height = 28 };
            _btnRefresh.Click += (s, e) => RefreshList();

            _btnCapture = new Button { Text = "▶  Capture Selected", Left = 145, Top = 6, Width = 150, Height = 28 };
            _btnCapture.Click += (s, e) => DoCapture();

            var note = new Label { Left = 305, Top = 10, Width = 630, Height = 20, Text = "Select LDB window then Capture. Green = content visible. Red = black (WDA blocked).", ForeColor = Color.DimGray };

            _winList = new ListBox { Left = 6, Top = 40, Width = 930, Height = 65, Font = new Font("Consolas", 8) };
            top.Controls.AddRange(new Control[] { _btnRefresh, _btnCapture, note, _winList });

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _l1 = MkLabel("BitBlt  (basic GDI)");
            _l2 = MkLabel("PrintWindow  (standard)");
            _l3 = MkLabel("PrintWindow  (RENDERFULLCONTENT)");
            _pb1 = MkPb(); _pb2 = MkPb(); _pb3 = MkPb();

            grid.Controls.Add(_l1, 0, 0); grid.Controls.Add(_l2, 1, 0); grid.Controls.Add(_l3, 2, 0);
            grid.Controls.Add(_pb1, 0, 1); grid.Controls.Add(_pb2, 1, 1); grid.Controls.Add(_pb3, 2, 1);

            Controls.Add(grid); Controls.Add(top);
            RefreshList();
        }

        Label MkLabel(string t) => new Label { Text = t, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        PictureBox MkPb() => new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };

        void RefreshList()
        {
            _windows.Clear(); _winList.Items.Clear();
            Win32.EnumWindows((hwnd, _) => {
                if (!Win32.IsWindowVisible(hwnd)) return true;
                int len = Win32.GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                Win32.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (string.IsNullOrEmpty(title)) return true;
                _windows.Add((hwnd, title));
                _winList.Items.Add(string.Format("[{0:X8}]  {1}", hwnd.ToInt64(), title));
                return true;
            }, IntPtr.Zero);
        }

        void DoCapture()
        {
            if (_winList.SelectedIndex < 0 || _winList.SelectedIndex >= _windows.Count) { MessageBox.Show("Select a window first."); return; }
            var hwnd = _windows[_winList.SelectedIndex].hwnd;
            Win32.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) { MessageBox.Show("Window has zero size."); return; }

            SetResult(_pb1, _l1, CaptureBitBlt(hwnd, w, h), "BitBlt");
            SetResult(_pb2, _l2, CapturePrintWindow(hwnd, w, h, 0), "PrintWindow");
            SetResult(_pb3, _l3, CapturePrintWindow(hwnd, w, h, 2), "PrintWindow+RFC");
        }

        static void SetResult(PictureBox pb, Label lbl, Bitmap bmp, string name)
        {
            if (bmp == null) { lbl.Text = name + "  —  ERROR"; lbl.BackColor = Color.Orange; pb.Image = null; return; }
            bool ok = HasContent(bmp);
            lbl.Text = name + (ok ? "  ✓  VISIBLE" : "  ✗  BLACK");
            lbl.BackColor = ok ? Color.LightGreen : Color.Salmon;
            pb.Image = bmp;
        }

        static bool HasContent(Bitmap bmp)
        {
            int step = Math.Max(1, Math.Min(bmp.Width, bmp.Height) / 16);
            for (int y = 0; y < bmp.Height; y += step)
                for (int x = 0; x < bmp.Width; x += step)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.R > 8 || c.G > 8 || c.B > 8) return true;
                }
            return false;
        }

        static Bitmap CaptureBitBlt(IntPtr hwnd, int w, int h)
        {
            IntPtr hdcSrc = Win32.GetDC(hwnd);
            if (hdcSrc == IntPtr.Zero) return null;
            IntPtr hdcDst = Win32.CreateCompatibleDC(hdcSrc);
            IntPtr hBmp = Win32.CreateCompatibleBitmap(hdcSrc, w, h);
            IntPtr old = Win32.SelectObject(hdcDst, hBmp);
            Win32.BitBlt(hdcDst, 0, 0, w, h, hdcSrc, 0, 0, 0x00CC0020);
            Win32.SelectObject(hdcDst, old);
            Win32.DeleteDC(hdcDst);
            Win32.ReleaseDC(hwnd, hdcSrc);
            var bmp = Image.FromHbitmap(hBmp);
            Win32.DeleteObject(hBmp);
            return bmp;
        }

        static Bitmap CapturePrintWindow(IntPtr hwnd, int w, int h, uint flags)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = Win32.PrintWindow(hwnd, hdc, flags);
                g.ReleaseHdc(hdc);
                if (!ok) { bmp.Dispose(); return null; }
            }
            return bmp;
        }
    }

    static class Win32
    {
        public delegate bool EnumWndProc(IntPtr hwnd, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWndProc cb, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }
}
