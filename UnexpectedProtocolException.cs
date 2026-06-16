using System;

namespace BitFab.KW1281Test
{
    [Serializable]
    internal class UnexpectedProtocolException : Exception
    {
        public UnexpectedProtocolException()
        {
        }

        public UnexpectedProtocolException(string? message) : base(message)
        {
        }

        public UnexpectedProtocolException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}