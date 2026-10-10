using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace MediaController
{
    /// <summary>
    /// Wraps the Windows media session manager (SMTC) and turns it into one question the
    /// UI can ask: what is the session I am showing, and what is it doing?
    ///
    /// Which session is "the" session: the pinned app if one is pinned and still running,
    /// otherwise whatever Windows calls current (usually the last player to start). A pin
    /// whose app has closed quietly falls back to auto rather than leaving the tile dead,
    /// and takes effect again if the app comes back.
    ///
    /// Threading. WinRT raises its events on thread-pool threads, and the session objects
    /// throw if read after the player has gone. So a callback does no reading of its own: it
    /// only says "something changed", and StateChanged is raised through the SynchronizationContext
    /// captured at construction - the UI thread in the app, and in a console selftest, where
    /// there is no context, directly on the callback thread. Nothing outside this class
    /// ever holds a WinRT object.
    /// </summary>
    sealed class MediaService : IDisposable
    {
        readonly object _lock = new object();
        readonly SynchronizationContext _ui;

        GlobalSystemMediaTransportControlsSessionManager _manager;
        readonly List<GlobalSystemMediaTransportControlsSession> _hooked =
            new List<GlobalSystemMediaTransportControlsSession>();

        string _pinnedAppId = "";
        bool _disposed;

        // Set when a call failed with a COM error, meaning the manager object has gone
        // stale. See Reconnect.
        bool _stale;

        /// <summary>Raised when anything that could change the snapshot has changed.</summary>
        public event Action StateChanged;

        public MediaService()
        {
            _ui = SynchronizationContext.Current;
        }

        /// <summary>
        /// The app id the user pinned, or "" for auto-follow. Setting it re-raises
        /// StateChanged, since the snapshot may now come from a different session.
        /// </summary>
        public string PinnedAppId
        {
            get { lock (_lock) { return _pinnedAppId; } }
            set
            {
                lock (_lock) { _pinnedAppId = value ?? ""; }
                RaiseChanged();
            }
        }

        /// <summary>
        /// Connects to the session manager. Blocks briefly; call once at startup. Throws if
        /// the OS has no media session manager (Windows older than 1809), which the caller
        /// should treat as "run with a permanently dimmed tile", not as a crash.
        /// </summary>
        public void Start()
        {
            Connect();
        }

        void Connect()
        {
            _manager = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            _manager.CurrentSessionChanged += OnCurrentChanged;
            _manager.SessionsChanged += OnListChanged;
            Rehook();
            _stale = false;
        }

        /// <summary>
        /// Throws the manager away and asks Windows for a new one.
        ///
        /// Restarting Explorer makes every call on the existing manager fail with "RPC server
        /// unavailable" (0x800706BA) - the object is a proxy into the shell and the shell it
        /// pointed at is gone - and nothing recovers it. Found by killing explorer.exe with
        /// the app running: the tile came back, because the controller rebuilds that, but
        /// sat on "Nothing playing" forever with a player running. The fix is the same as for
        /// the tile: do not repair, replace.
        ///
        /// Called from Snapshot, which the controller calls every second, so a failed attempt
        /// (the shell may not be ready yet) is simply retried a second later.
        /// </summary>
        /// <summary>
        /// True for the HRESULTs a dead shell proxy produces. By HRESULT and not by type:
        /// the WinRT projection does not throw a COMException for these, so an "is
        /// COMException" test never fired and the manager was never replaced. Deliberately
        /// not "any exception" either - a persistent failure unrelated to the shell (one
        /// odd player's properties always throwing) would then reconnect on every read.
        /// </summary>
        static bool IsRpcFailure(Exception ex)
        {
            uint h = unchecked((uint)ex.HResult);
            return h == 0x800706BA      // RPC_S_SERVER_UNAVAILABLE
                || h == 0x800706BE      // RPC_S_CALL_FAILED
                || h == 0x800706BF      // RPC_S_CALL_FAILED_DNE
                || h == 0x80010108      // RPC_E_DISCONNECTED
                || h == 0x80010114;     // RPC_E_SERVERFAULT-ish: object no longer valid
        }

        void Reconnect()
        {
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    if (_manager != null)
                    {
                        _manager.CurrentSessionChanged -= OnCurrentChanged;
                        _manager.SessionsChanged -= OnListChanged;
                    }
                }
                catch (Exception) { /* the manager is dead; that is why we are here */ }
                Unhook();
                _manager = null;

                try
                {
                    Connect();
                    Log.Write("media: reconnected to the session manager");
                }
                catch (Exception ex)
                {
                    // Not raised as a change: that would wake the controller, which would
                    // read a snapshot, which would try again - a tight loop. The next tick retries.
                    _manager = null;
                    Log.Write("media: reconnect failed: " + ex.Message);
                    return;
                }
            }
            RaiseChanged();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged -= OnCurrentChanged;
                    _manager.SessionsChanged -= OnListChanged;
                }
                Unhook();
            }
        }

        // ---- reading --------------------------------------------------------------------

        /// <summary>App ids of every running media session, for the pin menu.</summary>
        public List<string> SessionAppIds()
        {
            var ids = new List<string>();
            if (_manager == null) return ids;
            try
            {
                foreach (var s in _manager.GetSessions()) ids.Add(s.SourceAppUserModelId);
            }
            catch (Exception ex)
            {
                // A session can vanish between enumeration and read. An empty menu entry
                // for one tick is better than an exception into the UI.
                Log.Write("sessions: " + ex.Message);
            }
            return ids;
        }

        /// <summary>
        /// Reads the current snapshot. Never throws and never returns null: any failure
        /// reading a session that is mid-teardown yields MediaState.None for this tick.
        /// </summary>
        public MediaState Snapshot()
        {
            if (_stale || _manager == null) Reconnect();

            try
            {
                var s = Pick();
                if (s == null) return MediaState.None;

                var st = new MediaState();
                st.HasSession = true;
                st.AppId = s.SourceAppUserModelId;

                var props = Await(s.TryGetMediaPropertiesAsync());
                st.Title = props.Title ?? "";
                st.Artist = props.Artist ?? "";
                st.Album = props.AlbumTitle ?? "";
                AttachArt(st);

                var info = s.GetPlaybackInfo();
                st.Status = info.PlaybackStatus.ToString();
                var c = info.Controls;
                st.CanPlayPause = c.IsPlayPauseToggleEnabled;
                st.CanNext = c.IsNextEnabled;
                st.CanPrevious = c.IsPreviousEnabled;

                var tl = s.GetTimelineProperties();
                st.Duration = tl.EndTime - tl.StartTime;
                st.CanSeek = c.IsPlaybackPositionEnabled && st.Duration > TimeSpan.Zero;
                st.TakenAt = Environment.TickCount;
                var pos = tl.Position - tl.StartTime;
                if (st.IsPlaying)
                {
                    // Position was valid at LastUpdatedTime, not now. Some players never
                    // update it while playing, so clamp rather than trust the arithmetic.
                    pos += DateTimeOffset.UtcNow - tl.LastUpdatedTime;
                }
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (st.Duration > TimeSpan.Zero && pos > st.Duration) pos = st.Duration;
                st.Position = pos;
                return st;
            }
            catch (Exception ex)
            {
                Log.Write("snapshot: " + ex.Message);
                if (IsRpcFailure(ex)) _stale = true;
                return MediaState.None;
            }
        }

        // ---- commands -------------------------------------------------------------------

        public bool TogglePlayPause() { return Command(delegate(GlobalSystemMediaTransportControlsSession s) { return s.TryTogglePlayPauseAsync(); }); }
        public bool Next() { return Command(delegate(GlobalSystemMediaTransportControlsSession s) { return s.TrySkipNextAsync(); }); }
        public bool Previous() { return Command(delegate(GlobalSystemMediaTransportControlsSession s) { return s.TrySkipPreviousAsync(); }); }

        bool Command(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> send)
        {
            try
            {
                var s = Pick();
                return s != null && Await(send(s));
            }
            catch (Exception ex)
            {
                Log.Write("command: " + ex.Message);
                if (IsRpcFailure(ex)) _stale = true;
                return false;
            }
        }

        /// <summary>
        /// Asks the player to jump to <paramref name="position"/> (from the start of the
        /// track). Players may accept and ignore this; callers show the position
        /// optimistically and fall back to the next snapshot.
        /// </summary>
        public bool Seek(TimeSpan position)
        {
            return Command(delegate(GlobalSystemMediaTransportControlsSession s)
            {
                var tl = s.GetTimelineProperties();
                return s.TryChangePlaybackPositionAsync((tl.StartTime + position).Ticks);
            });
        }

        /// <summary>
        /// Reads the current session's thumbnail as encoded image bytes, or null. Blocks;
        /// never call on the UI thread. AsStream() lives in the unusable
        /// System.Runtime.WindowsRuntime, so the stream is read by hand with a DataReader.
        /// </summary>
        public byte[] ReadArt(string expectedKey)
        {
            try
            {
                var s = Pick();
                if (s == null) return null;
                var props = Await(s.TryGetMediaPropertiesAsync());
                // The track may have changed since the key was made; art for a different
                // track must not be filed under this one.
                if (expectedKey != null
                    && KeyOf(s.SourceAppUserModelId, props.Title, props.Artist, props.AlbumTitle) != expectedKey)
                    return null;
                var thumb = props.Thumbnail;
                if (thumb == null) return null;
                using (var stream = Await(thumb.OpenReadAsync()))
                {
                    ulong size = stream.Size;
                    if (size == 0 || size > 4 * 1024 * 1024) return null;
                    using (var reader = new Windows.Storage.Streams.DataReader(stream))
                    {
                        Await(reader.LoadAsync((uint)size));
                        var bytes = new byte[size];
                        reader.ReadBytes(bytes);
                        return bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("art: " + ex.Message);
                if (IsRpcFailure(ex)) _stale = true;
                return null;
            }
        }

        // ---- album art ------------------------------------------------------------------
        //
        // Fetched on a pool thread, once per track: opening the thumbnail stream can take
        // a while with some players and Snapshot runs on the UI thread every second. The
        // snapshot carries the bytes once they are in, and StateChanged fires when they land.
        // A browser often publishes the title before the image, so an empty result is
        // retried a couple of times, two seconds apart, before giving up on that track.

        string _artKey = "";
        byte[] _artBytes;
        bool _artBusy;
        int _artTries;
        int _artLastTry;

        static string KeyOf(string appId, string title, string artist, string album)
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist)) return "";
            return appId + "|" + title + "|" + artist + "|" + album;
        }

        void AttachArt(MediaState st)
        {
            string key = KeyOf(st.AppId, st.Title, st.Artist, st.Album);
            bool fetch = false;
            lock (_lock)
            {
                if (key != _artKey)
                {
                    _artKey = key;
                    _artBytes = null;
                    _artTries = 0;
                }
                st.ArtKey = key;
                if (key.Length == 0) return;
                st.ArtBytes = _artBytes;
                if (_artBytes == null && !_artBusy && _artTries < 3
                    && (_artTries == 0 || unchecked(Environment.TickCount - _artLastTry) > 2000))
                {
                    _artBusy = true;
                    _artTries++;
                    _artLastTry = Environment.TickCount;
                    fetch = true;
                }
            }
            if (fetch) ThreadPool.QueueUserWorkItem(FetchArt, key);
        }

        void FetchArt(object state)
        {
            string key = (string)state;
            byte[] bytes = ReadArt(key);
            bool landed = false;
            lock (_lock)
            {
                _artBusy = false;
                if (_artKey == key && bytes != null)
                {
                    _artBytes = bytes;
                    landed = true;
                }
            }
            if (landed) RaiseChanged();
        }

        // ---- plumbing -------------------------------------------------------------------

        GlobalSystemMediaTransportControlsSession Pick()
        {
            if (_manager == null) return null;
            string pin = PinnedAppId;
            if (pin.Length > 0)
            {
                foreach (var s in _manager.GetSessions())
                    if (string.Equals(s.SourceAppUserModelId, pin, StringComparison.OrdinalIgnoreCase))
                        return s;
            }
            return _manager.GetCurrentSession();
        }

        void OnCurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            RaiseChanged();
        }

        void OnListChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            Rehook();
            RaiseChanged();
        }

        void OnPropsChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) { RaiseChanged(); }
        void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) { RaiseChanged(); }
        void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) { RaiseChanged(); }

        // Per-session events have to be re-subscribed whenever the session list changes,
        // or a player that starts after launch is never heard from. += on a WinRT event
        // only compiles with System.Runtime.InteropServices.WindowsRuntime.dll referenced
        // (it supplies EventRegistrationToken); without it the error is CS1545.
        void Rehook()
        {
            lock (_lock)
            {
                if (_disposed) return;
                Unhook();
                try
                {
                    foreach (var s in _manager.GetSessions())
                    {
                        s.MediaPropertiesChanged += OnPropsChanged;
                        s.PlaybackInfoChanged += OnPlaybackChanged;
                        s.TimelinePropertiesChanged += OnTimelineChanged;
                        _hooked.Add(s);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("rehook: " + ex.Message);
                }
            }
        }

        void Unhook()
        {
            foreach (var s in _hooked)
            {
                try
                {
                    s.MediaPropertiesChanged -= OnPropsChanged;
                    s.PlaybackInfoChanged -= OnPlaybackChanged;
                    s.TimelinePropertiesChanged -= OnTimelineChanged;
                }
                catch (Exception) { /* session already gone; nothing left to unhook */ }
            }
            _hooked.Clear();
        }

        void RaiseChanged()
        {
            Action handler = StateChanged;
            if (handler == null || _disposed) return;
            if (_ui != null) _ui.Post(delegate { if (!_disposed) handler(); }, null);
            else handler();
        }

        /// <summary>
        /// Blocks until a WinRT operation completes. AsTask() would do this, but it lives in
        /// System.Runtime.WindowsRuntime, which needs the SDK's merged Windows assembly and
        /// does not compile here. Completed needs only the winmd references.
        ///
        /// Never call from the UI thread on anything slow: it blocks. Every operation used
        /// here (properties, a transport command) completes in milliseconds.
        /// </summary>
        static T Await<T>(IAsyncOperation<T> op)
        {
            var done = new ManualResetEvent(false);
            op.Completed = delegate(IAsyncOperation<T> o, AsyncStatus status) { done.Set(); };
            if (!done.WaitOne(5000)) throw new TimeoutException("WinRT operation timed out");
            if (op.Status == AsyncStatus.Error) throw op.ErrorCode;
            return op.GetResults();
        }
    }
}
