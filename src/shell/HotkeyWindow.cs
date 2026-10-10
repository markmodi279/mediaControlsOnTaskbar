using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace MediaController
{
    /// <summary>
    /// Global hotkeys: a hidden message-only window that owns the RegisterHotKey calls and
    /// turns WM_HOTKEY into an event.
    ///
    /// Message-only (parent HWND_MESSAGE) because it needs a message queue and nothing else -
    /// no taskbar button, no Alt+Tab entry, no z-order.
    ///
    /// Bindings are written as text, "Ctrl+Alt+Space": zero or more of Ctrl, Alt, Shift, Win,
    /// then one key by its System.Windows.Forms.Keys name (Space, Left, F9, A, MediaNextTrack,
    /// ...) or a bare digit. Registration can fail - another program already owns the
    /// combination - and that is reported by name rather than thrown, because the other two
    /// bindings should still work.
    /// </summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public const int PlayPause = 1;
        public const int Next = 2;
        public const int Previous = 3;
        public const int TogglePanel = 4;

        readonly List<int> _registered = new List<int>();

        /// <summary>A registered hotkey fired; the argument is PlayPause, Next, Previous or TogglePanel.</summary>
        public event Action<int> Pressed;

        public HotkeyWindow()
        {
            var cp = new CreateParams();
            cp.Caption = "MediaControllerHotkeys";
            cp.Parent = Native.HWND_MESSAGE;
            CreateHandle(cp);
        }

        /// <summary>
        /// Replaces every binding. Returns one line per binding that could not be used, empty
        /// when all of them were. Safe to call again on a config change: the old ones are
        /// released first, so a rebinding to a key this process already held is not refused.
        /// </summary>
        public List<string> Register(string playPause, string next, string previous, string panel)
        {
            Unregister();
            var problems = new List<string>();
            Bind(PlayPause, "play/pause", playPause, problems);
            Bind(Next, "next", next, problems);
            Bind(Previous, "previous", previous, problems);
            Bind(TogglePanel, "panel", panel, problems);
            return problems;
        }

        void Bind(int id, string what, string text, List<string> problems)
        {
            // An empty binding is how a user turns one off, not an error.
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;

            uint mods, vk;
            if (!TryParse(text, out mods, out vk))
            {
                problems.Add(what + ": cannot read \"" + text + "\"");
                return;
            }
            if (!Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk))
            {
                problems.Add(what + ": " + text + " is already in use by another program");
                return;
            }
            _registered.Add(id);
        }

        void Unregister()
        {
            foreach (int id in _registered) Native.UnregisterHotKey(Handle, id);
            _registered.Clear();
        }

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            bool haveKey = false;

            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                if (part.Length == 0) return false;

                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= Native.MOD_CONTROL; continue;
                    case "alt": mods |= Native.MOD_ALT; continue;
                    case "shift": mods |= Native.MOD_SHIFT; continue;
                    case "win":
                    case "windows": mods |= Native.MOD_WIN; continue;
                }

                if (haveKey) return false;   // two keys is not a chord RegisterHotKey can take

                Keys key;
                if (part.Length == 1 && part[0] >= '0' && part[0] <= '9')
                    key = Keys.D0 + (part[0] - '0');    // Keys names digits D0..D9
                else if (!TryKey(part, out key))
                    return false;

                vk = (uint)key;
                haveKey = true;
            }
            return haveKey;
        }

        static bool TryKey(string name, out Keys key)
        {
            key = Keys.None;
            // Enum.Parse accepts numbers and comma lists ("Ctrl, A"), which would let junk
            // through as a "valid" key; require a defined name.
            foreach (string defined in Enum.GetNames(typeof(Keys)))
            {
                if (string.Equals(defined, name, StringComparison.OrdinalIgnoreCase))
                {
                    key = (Keys)Enum.Parse(typeof(Keys), defined);
                    return key != Keys.None;
                }
            }
            return false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Action<int> handler = Pressed;
                if (handler != null) handler((int)m.WParam);
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            Unregister();
            DestroyHandle();
        }
    }
}
