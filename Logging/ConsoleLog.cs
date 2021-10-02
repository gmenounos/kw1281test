using System;

namespace BitFab.KW1281Test.Logging
{
    internal class ConsoleLog : ILog
    {
        public void Write(string message, LogDest dest)
        {
            if (dest != LogDest.File)
            {
                Console.Write(message);
            }
        }

        public void WriteLine(LogDest dest)
        {
            if (dest != LogDest.File)
            {
                Console.WriteLine();
            }
        }

        public void WriteLine(string message, LogDest dest)
        {
            if (dest != LogDest.File)
            {
                Console.WriteLine(message);
            }
        }

        public void Close()
        {
        }

        public void Dispose()
        {
        }
    }
}
