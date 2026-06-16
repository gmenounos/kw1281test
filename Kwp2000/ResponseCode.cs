namespace BitFab.KW1281Test.Kwp2000
{
    /// <summary>
    /// 
    /// </summary>
    public enum ResponseCode
    {
        generalReject = 0x10,
        serviceNotSupported = 0x11,
        subFunctionNotSupportedInvalidFormat = 0x12,
        busyRepeatRequest = 0x21,
        conditionsNotCorrectOrRequestSequenceError = 0x22,
        routineNotComplete = 0x23,
        requestOutOfRange = 0x31,
        securityAccessDenied = 0x33,
        invalidKey = 0x35,
        exceedNumberOfAttempts = 0x36,
        requiredTimeDelayNotExpired = 0x37,
        downloadNotAccepted = 0x40,
        improperDownloadType = 0x41,
        cantDownloadToSpecifiedAddress = 0x42,
        cantDownloadNumberOfBytesRequested = 0x43,
        uploadNotAccepted = 0x50,
        improperUploadType = 0x51,
        cantUploadFromSpecifiedAddress = 0x52,
        cantUploadNumberOfBytesRequested = 0x53,
        transferSuspended = 0x71,
        transferAborted = 0x72,
        illegalAddressInBlockTransfer = 0x74,
        illegalByteCountInBlockTransfer = 0x75,
        illegalBlockTransferType = 0x76,
        blockTransferDataChecksumError = 0x77,
        reqCorrectlyRcvdRspPending = 0x78,
        incorrectByteCountDuringBlockTransfer = 0x79,
        // Manufacturer-Specific 0x80-0xFF
    }
}
