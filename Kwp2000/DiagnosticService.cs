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
        startRoutineByLocalIdentifier = 0x31,
        requestDownload = 0x34,
        transferData = 0x36,
        writeMemoryByAddress = 0x3D,
        testerPresent = 0x3E,
        startCommunication = 0x81,
        stopCommunicatiom = 0x82,
        accessTimingParameters = 0x83,
    };
}
