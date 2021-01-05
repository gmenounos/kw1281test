using System;
using System.IO;

namespace BitFab.KW1281Test
{
    class Logger
    {
        internal static void Open(string filename)
        {
            _writer = new StreamWriter(filename, append: true);
        }

        internal static void Close()
        {
            _writer.Close();
        }

        internal static void WriteLine(string message)
        {
            Console.WriteLine(message);
            _writer.WriteLine(message);
        }

        internal static void WriteLine()
        {
            Console.WriteLine();
            _writer.WriteLine();
        }

        internal static void Write(string message)
        {
            Console.Write(message);
            _writer.Write(message);
        }

        private static StreamWriter _writer;
    }
}
