namespace BitFab.KW1281Test.Kwp2000
{
    public enum DiagnosticService : byte
    {
        startDiagnosticSession = 0x10,
        ecuReset = 0x11,
        readEcuIdentification = 0x1A,
        stopDiagnosticSession = 0x20,
        readMemoryByAddress = 0x23,
        securityAccess = 0x27,
    };
}
