using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace DwmLutGUI
{
    /// <summary>
    /// Hands the command line of a second launch to the instance that is already running.
    ///
    /// Only one instance may exist, because it owns the injected DLL and the tray icon. But simply
    /// refusing a second launch made the documented automation flags (-apply / -disable / -minimize
    /// / -exit) unusable: with autostart enabled there is almost always an instance sitting in the
    /// tray, so a script calling `DwmLutGUI.exe -apply` only ever got "Already running!" - a modal
    /// dialog with nobody there to dismiss it, which blocks the launch indefinitely.
    ///
    /// A named pipe is used rather than a window message because the running instance is normally
    /// hidden in the tray, which makes its window handle awkward to discover reliably.
    /// </summary>
    internal static class SingleInstance
    {
        // Scoped to the session: two users logged on at once each get their own instance and must
        // not be able to drive each other's.
        private static string PipeName
        {
            get
            {
                using (var self = Process.GetCurrentProcess())
                {
                    return "DwmLutGUI_args_" + self.SessionId;
                }
            }
        }

        private static Thread _listener;
        private static volatile bool _stopping;

        /// <summary>
        /// Sends the arguments to the running instance. Best-effort: a failure here simply means the
        /// second launch does nothing, which is no worse than the behaviour it replaces.
        /// </summary>
        public static bool SendToRunningInstance(IEnumerable<string> args)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    // Short timeout: the other instance may still be starting up, but a script must
                    // never be left hanging.
                    client.Connect(3000);

                    using (var writer = new StreamWriter(client))
                    {
                        foreach (var arg in args)
                        {
                            writer.WriteLine(arg);
                        }
                        writer.Flush();
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Starts listening for forwarded arguments. <paramref name="handler"/> is invoked with the
        /// received arguments; the caller is responsible for marshalling to the UI thread.
        /// </summary>
        public static void StartListener(Action<string[]> handler)
        {
            if (handler == null || _listener != null) return;

            _listener = new Thread(() =>
            {
                while (!_stopping)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(
                                   PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte))
                        {
                            server.WaitForConnection();
                            if (_stopping) return;

                            using (var reader = new StreamReader(server))
                            {
                                var payload = reader.ReadToEnd() ?? string.Empty;
                                var args = payload
                                    .Split('\n')
                                    .Select(line => line.Trim('\r'))
                                    .Where(line => line.Length > 0)
                                    .ToArray();

                                if (args.Length > 0) handler(args);
                            }
                        }
                    }
                    catch
                    {
                        // A malformed or aborted connection must never take the listener down with
                        // it - back off briefly and keep serving.
                        Thread.Sleep(200);
                    }
                }
            })
            {
                IsBackground = true,   // never keeps the process alive
                Name = "DwmLutGUI single-instance listener"
            };

            _listener.Start();
        }

        public static void Stop()
        {
            _stopping = true;
        }
    }
}
