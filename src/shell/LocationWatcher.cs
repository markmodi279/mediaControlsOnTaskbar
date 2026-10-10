using System;

namespace MediaController
{
    /// <summary>
    /// Calls back the moment any window of one process moves or resizes.
    ///
    /// What lets the tile get out of DesktopSwitcher's way at once. Its strip grows leftward
    /// over the tile when a desktop is added, and finding that out by polling - first once a
    /// second, then every 80 ms - still left the overlap visible for a few frames. An
    /// out-of-context WinEvent hook is told by Windows when the window moves, and the callback
    /// arrives on this thread's message loop, so the tile can be repositioned before the next
    /// frame is composed.
    ///
    /// Filtered to one process, so the callback is not woken by every window on the desktop.
    /// Out of context means no DLL is injected anywhere; it is a plain notification.
    /// </summary>
    sealed class LocationWatcher : IDisposable
    {
        readonly Native.WinEventProc _proc;   // held: native code keeps only a pointer to it
        readonly Action _changed;
        IntPtr _hook;

        public uint ProcessId { get; private set; }

        public LocationWatcher(uint processId, Action changed)
        {
            ProcessId = processId;
            _changed = changed;
            _proc = OnEvent;
            _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _proc, processId, 0, Native.WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero) Log.Write("location: hook failed for pid " + processId);
        }

        void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
        {
            if (idObject == Native.OBJID_WINDOW) _changed();
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        }
    }
}
