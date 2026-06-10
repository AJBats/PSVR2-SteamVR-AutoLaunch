using System;
using System.IO;

namespace PSVR2_AutoLaunch
{
    // Append-only debug log at %LOCALAPPDATA%\PSVR2AutoLaunch\app.log.
    // Best-effort: logging must never take the app down, so all errors are swallowed.
    internal static class Log
    {
        private static readonly object gate = new object();
        private static readonly string dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSVR2AutoLaunch");
        private static readonly string path = Path.Combine(dir, "app.log");

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
