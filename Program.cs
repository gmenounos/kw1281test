using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BitFab.KW1281Test
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                ShowUsage();
                return;
            }

            string portName = args[0];
            var controllerAddress = int.Parse(args[1], NumberStyles.HexNumber);
            var command = args[2];

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

                if (string.Compare(command, "ReadSoftwareVersion", true) == 0)
                {
                    kwp1281.CustomReadSoftwareVersion();
                }

                if (string.Compare(command, "ReadEeprom", true) == 0)
                {
                    var bytes = new List<byte>();
                    const byte blockSize = 16;
                    for (ushort addr = 0; addr < 2048; addr += blockSize)
                    {
                        var blockBytes = kwp1281.ReadEeprom(addr, blockSize);
                        bytes.AddRange(blockBytes);
                    }
                    var dumpFileName = identInfo.ToString().Replace(' ', '_') + "_eeprom.bin";
                    Console.WriteLine($"Saving EEPROM dump to {dumpFileName}");
                    File.WriteAllBytes(dumpFileName, bytes.ToArray());
                }

                if (string.Compare(command, "ReadRom", true) == 0)
                {
                    kwp1281.CustomReadRom(0x000000, 0x10);
                }

                if (string.Compare(command, "Reset", true) == 0)
                {
                    kwp1281.CustomReset();
                }

                kwp1281.EndCommunication();
            }
        }

        private static void ShowUsage()
        {
            Console.WriteLine("Usage: KW1281Test [PORT] [Address] [Command]");
            Console.WriteLine("       [Command] = ReadSoftwareVersion|Reset|ReadEeprom|ReadRom");
            Console.WriteLine("Usage: KW1281Test [PORT] [Address] ReadEeprom");
        }
    }
}
