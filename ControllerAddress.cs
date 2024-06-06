namespace BitFab.KW1281Test
{
    /// <summary>
    /// VW controller addresses
    /// </summary>
    enum ControllerAddress
    {
        Ecu = 0x01,
        CentralElectric = 0x09,
        Cluster = 0x17,
        CanGateway = 0x19,
        Immobilizer = 0x25,
        CentralLocking = 0x35,
        Navigation = 0x37,
        CCM = 0x46,
        Radio = 0x56,
        RadioManufacturing = 0x7C,
    }
}
