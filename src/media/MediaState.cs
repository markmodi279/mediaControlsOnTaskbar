using System;

namespace MediaController
{
    /// <summary>
    /// An immutable snapshot of the session being shown.
    ///
    /// A snapshot rather than a live wrapper around the WinRT session on purpose: the
    /// WinRT objects can be touched from any thread and throw if the player has gone away
    /// mid-read, so MediaService reads everything once, on its own terms, and the UI only
    /// ever sees plain values it can hold across a repaint.
    /// </summary>
    sealed class MediaState
    {
        /// <summary>The "nothing is playing" state. Never null, so the UI needs no check.</summary>
        public static readonly MediaState None = new MediaState();

        public bool HasSession;

        /// <summary>The source app's AppUserModelId, e.g. "brave.exe". Pinning keys on this.</summary>
        public string AppId = "";

        public string Title = "";
        public string Artist = "";
        public string Album = "";

        /// <summary>Playing, Paused, Stopped, Opened, Changing, Closed - SMTC's own words.</summary>
        public string Status = "";

        public bool IsPlaying { get { return Status == "Playing"; } }

        /// <summary>
        /// Position at the moment this snapshot was taken, already advanced by the time
        /// since the player last reported it. SMTC does not push position while playing,
        /// it reports a position and the time it was valid, so the extrapolation is ours.
        /// </summary>
        public TimeSpan Position;

        /// <summary>Zero when the player does not know (live streams, many browser tabs).</summary>
        public TimeSpan Duration;

        /// <summary>
        /// True when the player lets us move the position: it advertises it and knows the
        /// length. The panel's seek bar is greyed out otherwise.
        /// </summary>
        public bool CanSeek;

        /// <summary>
        /// Stopwatch-independent tick (Environment.TickCount) at which Position was valid.
        /// The tile and panel add the time since then while playing, so the bar moves
        /// without another SMTC read.
        /// </summary>
        public int TakenAt;

        /// <summary>Identifies the track's art: app + title + artist + album. Empty if none.</summary>
        public string ArtKey = "";

        /// <summary>Encoded image bytes for ArtKey, or null while fetching / unavailable. Never mutated.</summary>
        public byte[] ArtBytes;

        public bool CanPlayPause;
        public bool CanNext;
        public bool CanPrevious;

        /// <summary>The player's name for people: "Brave", "ZuneMusic".</summary>
        public string AppName { get { return FriendlyName(AppId); } }

        /// <summary>
        /// "brave.exe" -> "Brave"; "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> "ZuneMusic".
        /// The ids are what Windows keys sessions on, and are unreadable on screen.
        /// </summary>
        public static string FriendlyName(string appId)
        {
            string s = appId ?? "";
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            if (s.Length == 0) return appId;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>The title and artist on one line.</summary>
        public string Caption
        {
            get
            {
                if (!HasSession) return "";
                if (Title.Length == 0) return Artist;
                if (Artist.Length == 0) return Title;
                return Title + " - " + Artist;
            }
        }
    }
}
