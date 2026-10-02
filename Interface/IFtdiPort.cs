namespace BitFab.KW1281Test.Interface
{
    /// <summary>
    /// An FTDI chip port: what a cable profile needs on top of <see cref="IInterface"/>.
    /// There are two implementations: through the D2XX library (FTDI's own or a cable
    /// vendor's copy of it) and over the stock Windows WinUSB driver.
    /// </summary>
    internal interface IFtdiPort : IInterface
    {
        void SetLatencyTimer(byte milliseconds);

        void PurgeBuffers();
    }
}
