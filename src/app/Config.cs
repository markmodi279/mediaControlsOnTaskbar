using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MediaController
{
    /// <summary>
    /// Settings, kept in %APPDATA%\MediaController\config.json and written with defaults on
    /// first run so there is a file to edit.
    ///
    /// JSON rather than the INI desktopSwitcher uses because the hotkeys are the one thing
    /// a person will want to edit by hand, and a flat file with a small nested block reads
    /// naturally in it. JavaScriptSerializer is in the box (System.Web.Extensions) so this
    /// stays dependency-free; it is used for reading and for string escaping only. Writing
    /// is by hand, because its output is one unindented line and this file is for people.
    ///
    /// A file that does not parse is left alone and defaults are used for the session. The
    /// obvious alternative - overwrite it with defaults - would silently destroy a hand-
    /// edited file over one misplaced comma.
    /// </summary>
    /// <summary>Outline of the taskbar tile: its art, its progress ring and its hover halo all follow it.</summary>
    enum TileShape { Circle, Rounded, Square }

    /// <summary>Which side of DesktopSwitcher's strip the tile docks on.</summary>
    enum TileSide { Left, Right }

    sealed class Config
    {
        public static readonly string Dir = Environment.ExpandEnvironmentVariables(@"%APPDATA%\MediaController");
        public static readonly string FilePath = Path.Combine(Dir, "config.json");
        public static readonly string LogPath = Path.Combine(Dir, "log.txt");

        /// <summary>AppUserModelId of the pinned player, or "" to follow Windows' current session.</summary>
        public string PinnedApp = "";

        /// <summary>Gap between the tile and whatever it docks against, at 96 DPI.</summary>
        public int Margin = 6;

        /// <summary>
        /// "left": the tile sits left of DesktopSwitcher's strip (the strip stays beside the
        /// clock). "right": the tile takes the slot beside the clock and the strip moves left.
        /// </summary>
        public TileSide Side = TileSide.Left;

        /// <summary>"circle", "rounded" or "square".</summary>
        public TileShape Shape = TileShape.Circle;

        /// <summary>Hovering the tile slides a play/pause button out to its left.</summary>
        public bool HoverControl = true;

        /// <summary>Corner radius of the now-playing card at 96 DPI; 0 leaves it square.</summary>
        public int PanelRadius = 12;

        /// <summary>Writes log.txt beside this file. Off by default.</summary>
        public bool Diagnostics;

        // "Ctrl+Alt+Space" style. Parsed by HotkeyWindow (milestone 6); kept as text here
        // so that a binding it cannot parse can be reported by name rather than lost.
        public string HotkeyPlayPause = "Ctrl+Alt+Space";
        public string HotkeyNext = "Ctrl+Alt+Right";
        public string HotkeyPrevious = "Ctrl+Alt+Left";

        /// <summary>Opens and closes the now-playing panel. Empty turns it off.</summary>
        public string HotkeyPanel = "Ctrl+Alt+M";

        /// <summary>
        /// Startup load: the file if it reads, otherwise defaults. A missing file is written
        /// out so there is something to edit; a broken one is left alone (see class comment).
        /// </summary>
        public static Config Load()
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new Config();
                fresh.Save();
                return fresh;
            }
            Config cfg;
            return TryRead(out cfg) ? cfg : new Config();
        }

        /// <summary>
        /// Reads the file, or says it could not. Separate from Load because a live reload must
        /// treat the two failures differently: at startup a broken file means "use defaults",
        /// but while running it usually means the editor is halfway through a save, and
        /// applying defaults then would reset every setting to nothing for a moment.
        /// </summary>
        public static bool TryRead(out Config cfg)
        {
            cfg = new Config();
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
                if (root == null) throw new FormatException("top level is not an object");

                cfg.PinnedApp = Str(root, "pinnedApp", cfg.PinnedApp);
                // Unknown keys (an old "stripWidth") are ignored here and dropped on the next Save.
                cfg.Margin = Clamp(Num(root, "margin", cfg.Margin), 0, 200);
                cfg.Side = string.Equals(Str(root, "tileSide", ""), "right", StringComparison.OrdinalIgnoreCase)
                    ? TileSide.Right : TileSide.Left;
                cfg.Shape = ParseShape(Str(root, "tileShape", ""), cfg.Shape);
                cfg.HoverControl = Bool(root, "hoverControl", cfg.HoverControl);
                cfg.PanelRadius = Clamp(Num(root, "panelRadius", cfg.PanelRadius), 0, 40);
                cfg.Diagnostics = Bool(root, "diagnostics", cfg.Diagnostics);

                object hk;
                var keys = root.TryGetValue("hotkeys", out hk) ? hk as Dictionary<string, object> : null;
                if (keys != null)
                {
                    cfg.HotkeyPlayPause = Str(keys, "playPause", cfg.HotkeyPlayPause);
                    cfg.HotkeyNext = Str(keys, "next", cfg.HotkeyNext);
                    cfg.HotkeyPrevious = Str(keys, "previous", cfg.HotkeyPrevious);
                    cfg.HotkeyPanel = Str(keys, "panel", cfg.HotkeyPanel);
                }
                return true;
            }
            catch (Exception ex)
            {
                // Logged, though the log may be off (this very file configures it).
                Log.Write("config: could not read " + FilePath + " - " + ex.Message);
                cfg = new Config();
                return false;
            }
        }

        /// <summary>Copies every setting from another instance, so holders of this one see the change.</summary>
        public void ApplyFrom(Config o)
        {
            PinnedApp = o.PinnedApp;
            Margin = o.Margin;
            Side = o.Side;
            Shape = o.Shape;
            PanelRadius = o.PanelRadius;
            HoverControl = o.HoverControl;
            Diagnostics = o.Diagnostics;
            HotkeyPlayPause = o.HotkeyPlayPause;
            HotkeyNext = o.HotkeyNext;
            HotkeyPrevious = o.HotkeyPrevious;
            HotkeyPanel = o.HotkeyPanel;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var js = new JavaScriptSerializer();
                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"pinnedApp\": ").Append(js.Serialize(PinnedApp)).Append(",\r\n");
                sb.Append("  \"margin\": ").Append(Margin).Append(",\r\n");
                sb.Append("  \"tileSide\": ").Append(js.Serialize(Side == TileSide.Right ? "right" : "left")).Append(",\r\n");
                sb.Append("  \"tileShape\": ").Append(js.Serialize(Shape.ToString().ToLowerInvariant())).Append(",\r\n");
                sb.Append("  \"hoverControl\": ").Append(HoverControl ? "true" : "false").Append(",\r\n");
                sb.Append("  \"panelRadius\": ").Append(PanelRadius).Append(",\r\n");
                sb.Append("  \"diagnostics\": ").Append(Diagnostics ? "true" : "false").Append(",\r\n");
                sb.Append("  \"hotkeys\": {\r\n");
                sb.Append("    \"playPause\": ").Append(js.Serialize(HotkeyPlayPause)).Append(",\r\n");
                sb.Append("    \"next\": ").Append(js.Serialize(HotkeyNext)).Append(",\r\n");
                sb.Append("    \"previous\": ").Append(js.Serialize(HotkeyPrevious)).Append(",\r\n");
                sb.Append("    \"panel\": ").Append(js.Serialize(HotkeyPanel)).Append("\r\n");
                sb.Append("  }\r\n");
                sb.Append("}\r\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("config: could not write " + FilePath + " - " + ex.Message);
            }
        }

        // An unrecognised word falls back to the default (circle) rather than guessing at the
        // nearest shape; the file is left as written, so the typo can be fixed in place.
        static TileShape ParseShape(string s, TileShape fallback)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "circle": return TileShape.Circle;
                case "rounded": return TileShape.Rounded;
                case "square": return TileShape.Square;
                default: return fallback;
            }
        }

        static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string ? (string)v : fallback;
        }

        static int Num(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch (Exception) { return fallback; }
        }

        static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
