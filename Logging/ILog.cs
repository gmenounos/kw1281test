using System;

namespace BitFab.KW1281Test.Logging
{
    internal interface ILog : IDisposable
    {
        void Write(string message);

        void WriteLine();

        void WriteLine(string message);

        void Close();
    }
}
