using System;
using System.Globalization;

namespace BitFab.KW1281Test
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: KW1281Test [PORT] [Address]");
                return;
            }

            string portName = args[0];
            var controllerAddress = int.Parse(args[1], NumberStyles.HexNumber);

            Console.WriteLine($"Opening serial port {portName}");
            using (IInterface @interface = new Interface(portName))
            {
                IKW1281Dialog kwp1281 = new KW1281Dialog(@interface);

                Console.WriteLine("Sending wakeup message");
                var ecuInfo = kwp1281.WakeUp((byte)controllerAddress);
                Console.WriteLine($"ECU: {ecuInfo}");

                var identInfo = kwp1281.ReadIdent();
                Console.WriteLine($"Ident: {identInfo}");

                kwp1281.CustomUnlockAdditionalCommands();

                kwp1281.CustomReadSoftwareVersion();

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

                // kwp1281.CustomReadRom(0x10, 0xFF0000);

                // kwp1281.CustomReset();

                kwp1281.EndCommunication();
            }
        }
    }
}
