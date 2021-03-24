using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BitFab.KW1281Test.Cluster
{
    class MarelliCluster
    {
        public static void DumpMem(
            IKW1281Dialog kwp1281,
            ControllerInfo ecuInfo,
            string filename,
            ushort address, ushort count)
        {
            byte entryH; // High byte of code entry point
            byte regBlockH; // High byte of register block

            if (ecuInfo.Text.Contains("M73 V07")) // Beetle 1C0920901C
            {
                entryH = 0x02;
                regBlockH = 0x08;
            }
            else if (
                ecuInfo.Text.Contains("M73 V08") || // Beetle 1C0920921G
                ecuInfo.Text.Contains("M73 D09") || // Audi TT 8N2920980A
                ecuInfo.Text.Contains("M73 D14"))   // Audi TT 8N2920980A
            {
                entryH = 0x18;
                regBlockH = 0x20;
            }
            else
            {
                Logger.WriteLine("Unsupported cluster software version");
                return;
            }

            Logger.WriteLine("Sending block 0x6C");
            kwp1281.SendBlock(new List<byte> { 0x6C });

            Thread.Sleep(250);

            Logger.WriteLine("Writing data to cluster microcontroller");
            var data = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x50, 0x34,
                entryH, 0x00, // Entry point $xx00
            };
            if (!WriteMarelliBlockAndReadAck(kwp1281.KwpCommon, data))
            {
                return;
            }

            // Now we write a small memory dump program to the 68HC12 processor

            Logger.WriteLine("Writing memory dump program to cluster microcontroller");

            var startH = (byte)(address / 256);
            var startL = (byte)(address % 256);

            var end = address + count;
            var endH = (byte)(end / 256);
            var endL = (byte)(end % 256);

            var program = new byte[]
            {
                entryH, 0x00, // Address $xx00 

                0x14, 0x50,                     // orcc #$50
                0x07, 0x32,                     // bsr FeedWatchdog

                // Set baud rate to 9600
                0xC7,                           // clrb
                0x7B, regBlockH, 0xC8,          // stab $xxC8   ; SC1BDH
                0xC6, 0x34,                     // ldab #$34
                0x7B, regBlockH, 0xC9,          // stab $xxC9   ; SC1BDL

                // Enable transmit, disable UART interrupts
                0xC6, 0x08,                     // ldab #$08
                0x7B, regBlockH, 0xCB,          // stab $xxCB   ; SC1CR2

                0xCE, startH, startL,           // ldx #start
                // SendLoop:
                0xA6, 0x30,                     // ldaa 1,X+
                0x07, 0x0F,                     // bsr SendByte
                0x8E, endH, endL,               // cpx #end
                0x26, 0xF7,                     // bne SendLoop
                // Poison the watchdog to force a reboot
                0xCC, 0x11, 0x11,               // ldd #$1111
                0x7B, regBlockH, 0x17,          // stab $xx17   ; COPRST
                0x7A, regBlockH, 0x17,          // staa $xx17   ; COPRST
                0x3D,                           // rts

                // SendByte:
                0xF6, regBlockH, 0xCC,          // ldab $xxCC   ; SC1SR1
                0x7A, regBlockH, 0xCF,          // staa $xxCF   ; SC1DRL
                // TxBusy:
                0x07, 0x06,                     // bsr FeedWatchdog
                // Loop until TC (Transmit Complete) bit is set
                0x1F, regBlockH, 0xCC, 0x40, 0xF9,   // brclr $xxCC,$40,TxBusy   ; SC1SR1
                0x3D,                           // rts

                // FeedWatchdog:
                0xCC, 0x55, 0xAA,               // ldd #$55AA
                0x7B, regBlockH, 0x17,          // stab $xx17   ; COPRST
                0x7A, regBlockH, 0x17,          // staa $xx17   ; COPRST
                0x3D,                           // rts
            };
            if (!WriteMarelliBlockAndReadAck(kwp1281.KwpCommon, program))
            {
                return;
            }

            Logger.WriteLine("Receiving memory dump");

            var mem = new List<byte>();
            for (int i = 0; i < count; i++)
            {
                var b = kwp1281.KwpCommon.ReadByte();
                mem.Add(b);
            }

            var dumpFileName = filename ?? $"marelli_mem_${address:X4}.bin";

            File.WriteAllBytes(dumpFileName, mem.ToArray());
            Logger.WriteLine($"Saved memory dump to {dumpFileName}");

            Logger.WriteLine("Done");
        }

        private static bool WriteMarelliBlockAndReadAck(IKwpCommon kwpCommon, byte[] data)
        {
            var count = (ushort)(data.Length + 2); // Count includes 2-byte checksum
            var countH = (byte)(count / 256);
            var countL = (byte)(count % 256);
            kwpCommon.WriteByte(countH);
            kwpCommon.WriteByte(countL);

            var sum = (ushort)(countH + countL);
            foreach (var b in data)
            {
                kwpCommon.WriteByte(b);
                sum += b;
            }
            kwpCommon.WriteByte((byte)(sum / 256));
            kwpCommon.WriteByte((byte)(sum % 256));

            var expectedAck = new byte[] { 0x03, 0x09, 0x00, 0x0C };

            Logger.WriteLine("Receiving ACK");
            var ack = new List<byte>();
            for (int i = 0; i < 4; i++)
            {
                var b = kwpCommon.ReadByte();
                ack.Add(b);
            }
            if (!ack.SequenceEqual(expectedAck))
            {
                Logger.WriteLine($"Expected ACK but received {Utils.Dump(ack)}");
                return false;
            }

            return true;
        }
    }
}
