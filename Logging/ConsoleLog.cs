using System;

namespace BitFab.KW1281Test.Logging
{
    internal class ConsoleLog : ILog
    {
        public void Write(string message)
        {
            Console.Write(message);
        }

        public void WriteLine()
        {
            Console.WriteLine();
        }

        public void WriteLine(string message)
        {
            Console.WriteLine(message);
        }

        public void Close()
        {
        }

        public void Dispose()
        {
        }
    }
}
