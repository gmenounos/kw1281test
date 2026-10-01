namespace BitFab.KW1281Test.Airbag;

public interface IAirbagModule
{
    bool IsSupportedIdent(
        string ecuIdent,
        out string reason
    );

    /// <summary>Full EEPROM size of the module in bytes (depends on the specific version).</summary>
    int EepromSize { get; }

    void PrepareSession();

    byte[] DumpEeprom(
        int startAddress,
        int length
    );

    void LoadEeprom(
        int startAddress,
        byte[] data
    );

    /// <summary>
    /// Clears airbag deployment data.
    /// VW51: fills 0x000-0x04F (80 bytes).
    /// VW61: fills 0x000-0x030 and 0x151-0x1EF.
    /// By default fills with byte 0xFF.
    /// </summary>
    void ClearCrashData(byte fillValue = 0xFF); 
}
