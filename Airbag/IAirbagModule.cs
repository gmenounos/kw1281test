namespace KW1281Test.Airbag
{
    public interface IAirbagModule
    {
        bool IsSupportedIdent(
            string ecuIdent,
            out string reason
        );

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
        /// Очищает данные о срабатывании подушек.
        /// VW51: заполняет 0x000-0x04F (80 байт).
        /// VW61: заполняет 0x000-0x030 и 0x151-0x1EF.
        /// По умолчанию заполняет байтом 0xFF.
        /// </summary>
        void ClearCrashData(byte fillValue = 0xFF);
    }
}
