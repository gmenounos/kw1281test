using System;
using System.IO;

namespace BitFab.KW1281Test.Logging
{
    internal class FileLog : ILog
    {
        private readonly StreamWriter _writer;

        public FileLog(string filename)
        {
            _writer = new StreamWriter(filename, append: true);
        }

        public void WriteLine(string message, LogDest dest)
        {
            if (dest != LogDest.File)
            {
                Console.WriteLine(message);
            }
            if (dest != LogDest.Console)
            {
                _writer.WriteLine(message);
            }
        }

        public void WriteLine(LogDest dest)
        {
            if (dest != LogDest.File)
            {
                Console.WriteLine();
            }
            if (dest != LogDest.Console)
            {
                _writer.WriteLine();
            }
        }

        public void Write(string message, LogDest dest)
        {
            if (dest != LogDest.File)
            {
                Console.Write(message);
            }
            if (dest != LogDest.Console)
            {
                _writer.Write(message);
            }
        }

        public void Close()
        {
            _writer.Close();
        }

        public void Dispose()
        {
            Close();
        }
    }
}
