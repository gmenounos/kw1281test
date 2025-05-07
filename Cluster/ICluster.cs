namespace BitFab.KW1281Test.Cluster
{
    internal interface ICluster
    {
        void UnlockForEepromReadWrite();

        string DumpEeprom(uint? address, uint? length, string? dumpFileName, string prefix = default);
    }
}
