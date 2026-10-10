using System;
using System.Threading;

namespace MediaController
{
    /// <summary>
    /// The console selftest harness: every --command the exe accepts.
    ///
    /// SMTC only has anything to report while a real player is running in the user's
    /// session, so there is nothing to stand in for it in a unit test. Each layer gets a
    /// command that drives it against the live session manager and prints what it saw.
    /// Start a player first.
    ///
    /// Only reachable from a console build - build.cmd console.
    /// </summary>
    static class SelfTest
    {
        public static int Run(string[] args)
        {
            string command = args[0].ToLowerInvariant();
            try
            {
                var svc = new MediaService();
                svc.Start();
                using (svc)
                {
                    switch (command)
                    {
                        case "--now": return Now(svc);
                        case "--sessions": return Sessions(svc);
                        case "--toggle": return Report("toggle", svc.TogglePlayPause());
                        case "--next": return Report("next", svc.Next());
                        case "--prev": return Report("prev", svc.Previous());
                        case "--watch": return Watch(svc);
                        case "--art": return Art(svc, args.Length > 1 ? args[1] : "art.png");
                        case "--seek":
                            if (args.Length < 2) { Console.Error.WriteLine("usage: --seek <seconds>"); return 2; }
                            return Seek(svc, double.Parse(args[1]));
                        default:
                            Console.Error.WriteLine("unknown command: " + command);
                            return 2;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex);
                return 1;
            }
        }

        static int Now(MediaService svc)
        {
            Print(svc.Snapshot());
            return 0;
        }

        // Fetches the thumbnail, decodes it, writes it to the path given (as PNG).
        static int Art(MediaService svc, string path)
        {
            byte[] bytes = svc.ReadArt(null);
            if (bytes == null) { Console.WriteLine("art: none"); return 1; }
            using (var ms = new System.IO.MemoryStream(bytes))
            using (var img = System.Drawing.Image.FromStream(ms))
            {
                Console.WriteLine("art: " + bytes.Length + " bytes, " + img.Width + "x" + img.Height);
                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("wrote " + path);
            using (var art = AlbumArt.FromBytes(bytes))
            {
                if (art == null) { Console.WriteLine("AlbumArt: decode failed"); return 1; }
                Console.WriteLine("AlbumArt: square " + art.Side + "px, accent " + art.Accent);
            }
            // The snapshot path: art arrives asynchronously, so wait for it.
            for (int i = 0; i < 20; i++)
            {
                var st = svc.Snapshot();
                if (st.ArtBytes != null) { Console.WriteLine("snapshot art: " + st.ArtBytes.Length + " bytes, key " + st.ArtKey); return 0; }
                Thread.Sleep(250);
            }
            Console.WriteLine("snapshot art: never arrived");
            return 1;
        }

        static int Seek(MediaService svc, double seconds)
        {
            Print(svc.Snapshot());
            bool ok = svc.Seek(TimeSpan.FromSeconds(seconds));
            Console.WriteLine("seek: " + (ok ? "accepted" : "refused"));
            Thread.Sleep(800);
            Print(svc.Snapshot());
            return ok ? 0 : 1;
        }

        static int Sessions(MediaService svc)
        {
            var ids = svc.SessionAppIds();
            Console.WriteLine("sessions: " + ids.Count);
            foreach (var id in ids) Console.WriteLine("  " + id);
            return 0;
        }

        static int Report(string what, bool ok)
        {
            Console.WriteLine(what + ": " + (ok ? "sent" : "no session or refused"));
            return ok ? 0 : 1;
        }

        // Prints every change for 30 seconds - start, pause, skip and close a player while
        // it runs and each should produce a line. This is the check that the per-session
        // event hooks survive the session list changing.
        static int Watch(MediaService svc)
        {
            svc.StateChanged += delegate { Print(svc.Snapshot()); };
            Console.WriteLine("watching for 30s...");
            Print(svc.Snapshot());
            Thread.Sleep(30000);
            return 0;
        }

        static void Print(MediaState s)
        {
            if (!s.HasSession) { Console.WriteLine("(no session)"); return; }
            Console.WriteLine(s.AppId + " | " + s.Caption + " | " + s.Status
                + " | " + Fmt(s.Position) + " / " + Fmt(s.Duration)
                + " | seek=" + s.CanSeek + " prev=" + s.CanPrevious + " play=" + s.CanPlayPause + " next=" + s.CanNext);
        }

        static string Fmt(TimeSpan t)
        {
            return ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }
    }
}
