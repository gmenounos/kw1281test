namespace BitFab.KW1281Test.Kwp2000
{
    public enum DiagnosticService : byte
    {
        startDiagnosticSession = 0x10,
        ecuReset = 0x11,
        readDiagnosticTroubleCodes = 0x13,
        clearDiagnosticInformation = 0x14,
        readStatusOfDiagnosticTroubleCodes = 0x17,
        readDiagnosticTroubleCodesByStatus = 0x18,
        readEcuIdentification = 0x1A,
        stopDiagnosticSession = 0x20,
        readDataByLocalIdentifier = 0x21,
        readMemoryByAddress = 0x23,
        securityAccess = 0x27,
        dynamicallyDefineLocalIdentifier = 0x2C,
        inputOutputControlByLocalIdentifier = 0x30,
        startRoutineByLocalIdentifier = 0x31,
        requestDownload = 0x34,
        transferData = 0x36,
        writeDataByLocalIdentifier = 0x3B,
        writeMemoryByAddress = 0x3D,
        testerPresent = 0x3E,
        startCommunication = 0x81,
        stopCommunication = 0x82,
        accessTimingParameters = 0x83,
    };
}
