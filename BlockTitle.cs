namespace kwp1281test
{
    public enum BlockTitle : byte
    {
        ReadIdent = 0x00,
        ClearErrors = 0x05,
        End = 0x06, // end output, end of communication
        GetErrors = 0x07, // get errors, all errors output
        ACK = 0x09,
        NAK = 0x0A,
        ReadEeprom = 0x19,
        WriteEeprom = 0x1A,
        Custom = 0x1B,
        GroupReading = 0x29,
        GroupReadingResponse = 0xE7,
        ReadEepromResponse = 0xEF,
        AsciiData = 0xF6,
        WriteEepromResponse = 0xF9,
        GetErrorsResponse = 0xFC,
    }
}
