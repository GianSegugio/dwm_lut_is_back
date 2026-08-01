using System;
using System.IO;
using System.Security.Principal;

namespace DwmLutGUI
{
    /// <summary>
    /// Shared diagnostic log for the GUI side, primarily the EOTF gamma-fix path (which spans
    /// MainViewModel -> Injector -> EotfPatcher and restarts DWM, so a failure anywhere leaves no
    /// trace otherwise).
    ///
    /// Writes to C:\Windows\Temp\dwmlut_gui.log so it sits next to the injector's dwm_diag.log,
    /// falling back to %TEMP% if that isn't writable. Every line is flushed immediately, because
    /// the interesting failures are the ones that take the process or the desktop down with them.
    ///
    /// Set <see cref="Enabled"/> to false for release builds.
    /// </summary>
    internal static class GuiDiag
    {
        public const bool Enabled = false;

        private static readonly object Lock = new object();
        private static string _path;
        private static bool _headerWritten;

        private static string ResolvePath()
        {
            if (_path != null) return _path;

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "dwmlut_gui.log"),
                Path.Combine(Path.GetTempPath(), "dwmlut_gui.log")
            };

            foreach (var c in candidates)
            {
                try
                {
                    var dir = Path.GetDirectoryName(c);
                    if (dir != null && !Directory.Exists(dir)) continue;
                    File.AppendAllText(c, string.Empty);
                    _path = c;
                    return _path;
                }
                catch { }
            }

            _path = candidates[candidates.Length - 1];
            return _path;
        }

        private static void WriteHeaderIfNeeded()
        {
            if (_headerWritten) return;
            _headerWritten = true;

            try
            {
                var elevated = false;
                try
                {
                    using (var id = WindowsIdentity.GetCurrent())
                    {
                        elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch { }

                RawWrite("");
                RawWrite("=====================================================================");
                RawWrite("session start  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                RawWrite("  OS            " + Environment.OSVersion.Version);
                RawWrite("  process       pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                         " session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId +
                         " elevated=" + elevated + " 64bit=" + Environment.Is64BitProcess);
                RawWrite("=====================================================================");
            }
            catch { }
        }

        private static void RawWrite(string line)
        {
            try
            {
                File.AppendAllText(ResolvePath(), line + "\r\n");
            }
            catch { /* diagnostics must never disturb the app */ }
        }

        public static void Log(string msg)
        {
            if (!Enabled) return;
            lock (Lock)
            {
                WriteHeaderIfNeeded();
                RawWrite("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg);
            }
        }

        /// <summary>Logs an exception with its full type, message and stack.</summary>
        public static void LogError(string where, Exception ex)
        {
            if (!Enabled) return;
            lock (Lock)
            {
                WriteHeaderIfNeeded();
                RawWrite("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] !! EXCEPTION in " + where);
                RawWrite("     " + (ex == null ? "(null)" : ex.ToString().Replace("\n", "\n     ")));
            }
        }

        /// <summary>Logs the last Win32 error alongside a failed API call.</summary>
        public static void LogWin32(string what)
        {
            if (!Enabled) return;
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log(what + " failed, GetLastError=" + err + " (0x" + err.ToString("X") + ")");
        }
    }
}
