using System;
using System.Runtime.InteropServices;

namespace MediaController
{
    /// <summary>
    /// Win32 P/Invoke surface: the subset the taskbar docking and the tile window need.
    /// Grows with the milestones.
    /// </summary>
    static class Native
    {
        // --- DPI ---------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("shcore.dll", SetLastError = true)]
        static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDPIAware();

        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

        /// <summary>
        /// Opts the process out of DPI virtualisation. MUST be called before any window
        /// is created.
        ///
        /// Explorer is DPI-aware. A DPI-unaware process that parents a window into the
        /// taskbar has its coordinates silently scaled by 1/dpiScale, so on a 125%
        /// display a tile positioned at x=1110 actually lands at x=888. Everything
        /// looks correct in our own logs and is wrong on screen.
        ///
        /// Falls back through three generations of the API; the oldest ships on Vista.
        /// </summary>
        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int index);

        const int LOGPIXELSX = 88;

        /// <summary>
        /// DPI scale for a window's monitor, 1.0 at 96 DPI. Config sizes are authored
        /// for 96 DPI and multiplied by this, so the tile matches the taskbar's own
        /// button size instead of looking cramped on a scaled display.
        /// </summary>
        public static double GetDpiScale(IntPtr hWnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hWnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch (EntryPointNotFoundException) { }

            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return 1.0;
            try
            {
                int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        // --- geometry / parenting ---------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after,
                                                 string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        public const uint GA_ROOT = 2;

        /// <summary>
        /// The top-level window that owns whatever is on screen at a point.
        ///
        /// GA_ROOT, not a bare GetParent: WindowFromPoint returns the deepest child at the
        /// point, so over an empty stretch of taskbar it answers MSTaskListWClass, not
        /// Shell_TrayWnd. Walking to the root asks the question actually meant - which
        /// window is this pixel part of.
        /// </summary>
        public static IntPtr RootWindowFromPoint(int x, int y)
        {
            var pt = new POINT();
            pt.X = x;
            pt.Y = y;

            IntPtr hit = WindowFromPoint(pt);
            if (hit == IntPtr.Zero) return IntPtr.Zero;

            IntPtr root = GetAncestor(hit, GA_ROOT);
            return root != IntPtr.Zero ? root : hit;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                               int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern uint GetPixel(IntPtr hdc, int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr icon);

        // --- painting ---------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

        public const uint TME_LEAVE = 0x00000002;

        // System timer resolution. Windows ticks at 15.6 ms unless a process asks for finer;
        // the request is process-wide and must be paired with timeEndPeriod.
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint milliseconds);

        [DllImport("user32.dll", EntryPoint = "InvalidateRect")]
        public static extern bool InvalidateRect(IntPtr hWnd, ref RECT rect, bool erase);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        /// <summary>Holding the key down fires the hotkey once, not at the keyboard repeat rate.</summary>
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;

        /// <summary>Parent for a message-only window: no UI, no z-order, just a message queue.</summary>
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        public const int WM_PAINT = 0x000F;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSELEAVE = 0x02A3;

        public static int LoWord(IntPtr value)
        {
            return unchecked((short)(long)value);
        }

        public static int HiWord(IntPtr value)
        {
            return unchecked((short)((long)value >> 16));
        }

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        public const int GWL_STYLE = -16;
        public const int WS_CHILD = unchecked((int)0x40000000);
        public const int WS_VISIBLE = unchecked((int)0x10000000);
        public const int WS_EX_NOACTIVATE = 0x08000000;

        // --- low-level mouse hook ----------------------------------------------

        // For closing the panel on a click anywhere else. See ClickWatcher.

        public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc,
                                                     IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

        public const int WH_MOUSE_LL = 14;
        public const uint PM_NOREMOVE = 0x0000;

        public const int WM_QUIT = 0x0012;
        public const int WM_APP = 0x8000;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_XBUTTONDOWN = 0x020B;

        // --- the panel: a clickable popup that never takes focus ---------------

        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;

        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_CAPTURECHANGED = 0x0215;
        public const int MA_NOACTIVATE = 3;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOREDRAW = 0x0008;
        public const uint SWP_HIDEWINDOW = 0x0080;

        public const uint LWA_ALPHA = 0x2;

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// Hands the pages this process is not using back to the OS (-1, -1 empties the working
        /// set). They fault back in on demand, so it costs a few soft faults the next time
        /// something runs, and shows as a much smaller number in Task Manager.
        /// </summary>
        public static void TrimWorkingSet()
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        // --- window location events ---------------------------------------------

        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject,
                                          int idChild, uint threadId, uint time);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
                                                    WinEventProc proc, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0;
        public const int OBJID_WINDOW = 0;
    }
}
