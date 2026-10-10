using System;
using System.Diagnostics;
using System.Threading;

namespace MediaController
{
    /// <summary>
    /// A steady 60 Hz source of repaints for one window, for as long as it is running.
    ///
    /// Not a WinForms Timer: that is WM_TIMER, which is low priority, coalesced with other
    /// messages and quantised to the 15.6 ms system tick, and measured here it delivered
    /// about 37 fps with gaps up to 45 ms however short the interval asked for. A thread
    /// that sleeps to a fixed schedule and only calls InvalidateRect (safe from any thread)
    /// gives an even 60 Hz; the UI thread still does all the drawing.
    ///
    /// Only running while something moves. An idle window must cost nothing, so neither the
    /// thread nor the raised timer resolution (timeBeginPeriod) exists outside Start..Stop.
    /// </summary>
    sealed class FramePump : IDisposable
    {
        readonly IntPtr _hwnd;
        readonly string _name;
        Thread _thread;
        volatile bool _running;
        Native.RECT _rect;
        bool _whole;

        public FramePump(IntPtr hwnd, string name)
        {
            _hwnd = hwnd;
            _name = name;
        }

        public bool Running { get { return _running; } }

        /// <summary>Each frame invalidates the whole window.</summary>
        public void Start()
        {
            _whole = true;
            Begin();
        }

        /// <summary>
        /// Each frame invalidates only this client rectangle. Repainting everything at 60 Hz
        /// roughly doubled the CPU a scrolling animation cost, so what does not move is not redrawn.
        /// </summary>
        public void Start(Native.RECT area)
        {
            _whole = false;
            _rect = area;
            Begin();
        }

        void Begin()
        {
            if (_running) return;
            _running = true;
            Native.timeBeginPeriod(1);
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = _name;
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            Thread t = _thread;
            _thread = null;
            if (t != null) t.Join(200);
            Native.timeEndPeriod(1);
        }

        void Loop()
        {
            const double FrameMs = 1000.0 / 60.0;
            var sw = Stopwatch.StartNew();
            double next = FrameMs;
            while (_running)
            {
                if (_whole) Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
                else Native.InvalidateRect(_hwnd, ref _rect, false);
                // Sleep to the schedule, not for a fixed interval, so a slow frame is
                // absorbed rather than shifting every frame after it.
                next += FrameMs;
                double wait = next - sw.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
                else if (wait < -FrameMs) next = sw.Elapsed.TotalMilliseconds;   // fell far behind; resync
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
