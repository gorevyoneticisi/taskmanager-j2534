using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

namespace TaskmanagerBridge
{
    /// <summary>
    /// Asynchronous CAN traffic logger. All writes are offloaded to a dedicated
    /// background thread so the J2534 caller is never blocked by disk I/O.
    /// Log location: %LOCALAPPDATA%\TaskmanagerBridge\traffic.log
    /// </summary>
    public static class Sniffa
    {
        // FIX C2: %LOCALAPPDATA% is writable without elevation on all modern Windows.
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskmanagerBridge",
            "traffic.log");

        // FIX C1: bounded at 10 000 entries (~600 KB peak) to prevent unbounded
        // heap growth on high-traffic CAN buses (500 kbps = ~5 000 frames/sec).
        // FIX A5: TryAdd (non-blocking) is used in LogTraffic so that a full
        // queue drops entries rather than stalling the J2534 caller thread.
        private static readonly BlockingCollection<string> _logQueue
            = new BlockingCollection<string>(10000);

        private static readonly Thread _writerThread;

        static Sniffa()
        {
            _writerThread = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "Sniffa_Writer"
            };
            _writerThread.Start();
        }

        /// <summary>
        /// Queues one log line. Returns immediately; the background thread
        /// handles the actual write. Drops the entry silently if the queue
        /// is full to protect J2534 real-time timing.
        /// </summary>
        public static void LogTraffic(string direction, uint messageId, byte[] data)
        {
            try
            {
                string hexData = (data != null)
                    ? BitConverter.ToString(data).Replace("-", " ")
                    : "(null)";
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logLine = $"[{timestamp}] {direction,-16} | ID: 0x{messageId:X8} | DATA: {hexData}";

                _logQueue.TryAdd(logLine); // non-blocking; drops entry when full
            }
            catch
            {
                // Fail silently; logging must never affect J2534 timing.
            }
        }

        private static void ProcessQueue()
        {
            try
            {
                // FIX C2: create the directory if it does not yet exist.
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));

                using (StreamWriter writer = new StreamWriter(_logPath, append: true))
                {
                    writer.AutoFlush = true;

                    foreach (string line in _logQueue.GetConsumingEnumerable())
                    {
                        writer.WriteLine(line);
                    }
                }
            }
            catch
            {
                // Background thread fails silently.
            }
        }
    }
}
