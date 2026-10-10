using System;
using System.IO;
using System.Threading;

namespace MediaController
{
    /// <summary>
    /// Tells the controller when config.json changes on disk, so a hand edit applies the
    /// moment it is saved.
    ///
    /// FileSystemWatcher is noisy: one save from an editor commonly raises two to four events
    /// (truncate, write, rename-over-temp), and the first can arrive while the file is empty.
    /// So events only (re)start a short timer, and Changed is raised once the file has been
    /// quiet for a moment - on the UI thread, through the context captured at construction.
    /// </summary>
    sealed class ConfigWatcher : IDisposable
    {
        const int QuietMs = 300;

        readonly FileSystemWatcher _watcher;
        readonly SynchronizationContext _ui;
        readonly System.Threading.Timer _timer;

        public event Action Changed;

        public ConfigWatcher()
        {
            _ui = SynchronizationContext.Current;
            _timer = new System.Threading.Timer(delegate { Fire(); }, null, Timeout.Infinite, Timeout.Infinite);

            Directory.CreateDirectory(Config.Dir);
            _watcher = new FileSystemWatcher(Config.Dir, Path.GetFileName(Config.FilePath));
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
            _watcher.Changed += delegate { Bump(); };
            _watcher.Created += delegate { Bump(); };
            _watcher.Renamed += delegate { Bump(); };
            _watcher.EnableRaisingEvents = true;
        }

        void Bump()
        {
            _timer.Change(QuietMs, Timeout.Infinite);
        }

        void Fire()
        {
            Action handler = Changed;
            if (handler == null) return;
            if (_ui != null) _ui.Post(delegate { handler(); }, null);
            else handler();
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}
