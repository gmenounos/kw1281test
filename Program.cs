using System;

namespace BitFab.Kwp1281Test
{
    class Program
    {
        static void Main(string[] args)
        {
            const string portName = "COM4";

            Console.WriteLine($"Opening serial port {portName}");
            using (IInterface @interface = new Interface(portName))
            {
                IKwp1281 kwp1281 = new Kwp1281(@interface);

                Console.WriteLine("Sending wakeup message");
                kwp1281.WakeUp(0x17);

                // Receive ECU identification
                kwp1281.ReceiveBlocks();

                kwp1281.ReadIdent();

                kwp1281.CustomUnlockAdditionalCommands();

                // kwp1281.CustomReadSoftwareVersion();

#if false
                for (ushort addr = 0; addr < 2048; addr += 16)
                {
                    kwp1281.ReadEeprom(16, addr);
                }
#endif

#if false
                for (ushort addr = 0; addr < 0x100; addr += 0x10)
                {
                    kwp1281.CustomReadRom(0x10, addr);
                }
#endif

                kwp1281.CustomReadRom(0x10, 0xFF0000);

#if false
                kwp1281.CustomReset();
#endif

                kwp1281.EndCommunication();
            }
        }
    }
}
