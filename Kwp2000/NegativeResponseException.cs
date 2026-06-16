using System;

namespace BitFab.KW1281Test.Kwp2000
{
    public class NegativeResponseException : Exception
    {
        public NegativeResponseException(Kwp2000Message kwp2000Message)
        {
            Kwp2000Message = kwp2000Message;
        }

        public Kwp2000Message Kwp2000Message { get; }
    }
}
