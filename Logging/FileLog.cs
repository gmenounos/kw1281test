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

        public void WriteLine(string message)
        {
            Console.WriteLine(message);
            _writer.WriteLine(message);
        }

        public void WriteLine()
        {
            Console.WriteLine();
            _writer.WriteLine();
        }

        public void Write(string message)
        {
            Console.Write(message);
            _writer.Write(message);
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
