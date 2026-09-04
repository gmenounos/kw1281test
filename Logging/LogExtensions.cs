namespace BitFab.KW1281Test.Logging
{
    internal static class LogExtensions
    {
        /// <summary>
        /// Writes a high-volume trace message to the log file only (never the console). Used by the
        /// EDC15 flash read/write loops, whose per-byte/per-packet tracing would otherwise flood the
        /// screen. The message is written verbatim (callers include their own newline).
        /// </summary>
        public static void WriteFileOnly(this ILog log, string message)
            => log.Write(message, LogDest.File);
    }
}
