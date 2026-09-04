namespace BitFab.KW1281Test.EDC15
{
    /// <summary>
    /// Thrown by <see cref="Edc15FlashVM"/>'s normal <c>ConnectOnce</c> (via <see cref="Edc15FlashVM.Connect"/>/
    /// <see cref="Edc15FlashVM.ConnectAuto"/>, i.e. every ordinary Read/Write Flash call) specifically
    /// when the very first, KW1281-mode wakeup pulse DID get a real response from the ECU, but that
    /// response reported a protocol version other than 1281 -- for an EDC15, in practice this means
    /// only one thing: the ECU is already stuck answering every wakeup in KWP2000 mode directly,
    /// i.e. soft-bricked (stuck in the flash loader's boot stub), most likely left that way by an
    /// earlier write that didn't complete cleanly.
    ///
    /// <para>Deliberately a DIFFERENT, more specific exception than the plain
    /// <see cref="UnableToProceedException"/> that same wakeup call (<c>KwpCommon.WakeUp</c>) throws
    /// internally when there's no response at all after its own retries -- that case is an ordinary
    /// communication failure (bad connection, wrong baud, ignition off, ...) and should NOT be
    /// treated as evidence of a soft-brick. Catching
    /// specifically this subtype, not the base <see cref="UnableToProceedException"/>, is what lets
    /// a caller distinguish "the ECU didn't answer" from "the ECU answered, but wrong" and react
    /// only to the latter.</para>
    ///
    /// <para>Callers that want to auto-recover from this (see
    /// <c>EcuFlashViewModel</c>/
    /// <c>EcuCloneViewModel</c>'s write methods) should retry
    /// using <see cref="Edc15FlashVM.WriteFlashRecovery"/> instead of the normal write path --
    /// that's the one write path that doesn't require a KW1281 wakeup first, specifically built for
    /// this exact stuck state (see its own doc comment, and KWHack33.log for a real-hardware
    /// recovery using it).</para>
    /// </summary>
    // internal, not public -- matches UnableToProceedException's own accessibility (a public
    // subclass of an internal base class is a compile error: "base class is less accessible").
    // Fine either way: every catch site is inside this same assembly (KWHack.Core's ViewModels),
    // which is all internal accessibility ever needs to reach.
    sealed class Edc15SoftBrickDetectedException : UnableToProceedException
    {
        /// <summary>The protocol version the ECU actually reported on the KW1281-mode wakeup
        /// (instead of the expected 1281) -- logged for diagnostic value, not currently used to
        /// change behavior, since any non-1281 response from an EDC15 here means the same thing
        /// regardless of the exact number.</summary>
        public int ReportedProtocolVersion { get; }

        public Edc15SoftBrickDetectedException(int reportedProtocolVersion)
        {
            ReportedProtocolVersion = reportedProtocolVersion;
        }
    }
}
