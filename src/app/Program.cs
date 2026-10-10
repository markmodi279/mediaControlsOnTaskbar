using System;
using System.Windows.Forms;

namespace MediaController
{
    /// <summary>
    /// Entry point, and nothing else. No arguments runs the app; anything else is a
    /// selftest command handled by SelfTest against the real session manager.
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Config cfg = Config.Load();
            // MC_LOG=1 forces the log on for a single run without editing the config.
            Log.Init(Config.LogPath, cfg.Diagnostics || Environment.GetEnvironmentVariable("MC_LOG") == "1");

            if (args.Length == 0) return RunApp(cfg);

            return SelfTest.Run(args);
        }

        /// <summary>
        /// Normal operation. A single instance only: two controllers would dock two tiles
        /// into the taskbar and fight over the same slot.
        /// </summary>
        static int RunApp(Config cfg)
        {
            bool isFirst;
            using (var single = new System.Threading.Mutex(true, "Local\\MediaControllerInstance", out isFirst))
            {
                if (!isFirst)
                {
                    Log.Write("startup: another instance is already running");
                    return 0;
                }

                // Before any window exists, or the tile lands at the wrong coordinates on
                // a scaled display (see Native.EnableDpiAwareness).
                Native.EnableDpiAwareness();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // MediaService captures SynchronizationContext.Current to post its events
                // back to the UI thread. WinForms only installs its context once the first
                // control exists, which is after the Controller is constructed - so install
                // it now, or every SMTC event would arrive on a thread-pool thread.
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new WindowsFormsSynchronizationContext());

                using (var controller = new Controller(cfg))
                {
                    Application.Run(controller);
                }

                GC.KeepAlive(single);
                return 0;
            }
        }
    }
}
