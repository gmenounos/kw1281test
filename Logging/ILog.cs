using System;

namespace BitFab.KW1281Test.Logging
{
    internal enum LogDest
    {
        All,
        Console,
        File
    }

    internal interface ILog : IDisposable
    {
        void Write(string message, LogDest dest = LogDest.All);

        void WriteLine(LogDest dest = LogDest.All);

        void WriteLine(string message, LogDest dest = LogDest.All);

        void Close();
    }
}
