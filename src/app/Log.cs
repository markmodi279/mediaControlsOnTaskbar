using System;
using System.IO;

namespace MediaController
{
    /// <summary>
    /// A rolling text log, off unless config says diagnostics = true. Failures in this
    /// app are almost all "a player vanished mid-read", which are expected and silent in
    /// normal use; the log is how you find out one is happening too often.
    /// </summary>
    static class Log
    {
        static readonly object Gate = new object();
        static string _path;
        static bool _enabled;

        public static void Init(string path, bool enabled)
        {
            _path = path;
            _enabled = enabled;
        }

        public static void Write(string message)
        {
            if (!_enabled || _path == null) return;
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    // Roll at 256 KB so a long-lived process never fills the profile.
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > 256 * 1024) File.Delete(_path);
                    File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n");
                }
                catch (Exception) { /* logging must never be the thing that fails */ }
            }
        }
    }
}
