using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace kwp1281test
{
    class Program
    {
        private static SerialPort _port;
        private static byte _blockCounter = 0x01;

        static void Main(string[] args)
        {
            const string portName = "COM4";

            _port = new SerialPort(portName)
            {
                BaudRate = 10400,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                RtsEnable = false,
                DtrEnable = true,
                ReadTimeout = (int)TimeSpan.FromSeconds(5).TotalMilliseconds,
                WriteTimeout = (int)TimeSpan.FromSeconds(5).TotalMilliseconds
            };

            Console.WriteLine($"Opening serial port {portName}");
            _port.Open();

            try
            {
                WakeUp(0x17);

                Console.WriteLine("Reading sync byte");
                var syncByte = _port.ReadByte();
                if (syncByte != 0x55)
                {
                    Console.WriteLine($"Unexpected sync byte: Expected 0x55, Actual 0x{syncByte:X2}");
                    return;
                }

                var keywordLsb = _port.ReadByte();
                Console.WriteLine($"Keyword Lsb 0x{keywordLsb:X2}");

                var keywordMsb = ReadAndAckByte();
                Console.WriteLine($"Keyword Msb 0x{keywordMsb:X2}");

                if (keywordLsb == 0x01 && keywordMsb == 0x8A)
                {
                    Console.WriteLine("Protocol is KW 1281 (8N1)");
                }

                // Receive ECU identification
                ReceiveBlocks();

                ReadIdent();

                CustomUnlockAdditionalCommands();

                // CustomReadSoftwareVersion();

#if false
                for (ushort addr = 0; addr < 2048; addr += 16)
                {
                    ReadEeprom(16, addr);
                }
#endif

#if false
                for (ushort addr = 0; addr < 0x100; addr += 0x10)
                {
                    CustomReadRom(0x10, addr);
                }
#endif

                CustomReadRom(0x10, 0xFF0000);

#if false
                CustomReset();
#endif

                EndCommunication();
            }
            finally
            {
                _port.Close();
            }
        }

        private static void ReadIdent()
        {
            Console.WriteLine("Sending ReadIdent block");
            SendBlock(new List<byte> { (byte)BlockTitle.ReadIdent });
            ReceiveBlocks();
        }

        private static void ReadEeprom(byte count, ushort address)
        {
            Console.WriteLine($"Sending ReadEeprom block (Count: 0x{count:X2}, Address: 0x{address:X4})");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ReadEeprom,
                count,
                (byte)(address >> 8),
                (byte)(address & 0xFF)
            });
            ReceiveBlocks();
        }

        private static void CustomReadRom(byte count, uint address)
        {
            Console.WriteLine($"Sending Custom \"Read ROM\" block (Count: 0x{count:X2}, Address: 0x{address:X6})");
            SendCustom(new List<byte>
            {
                0x86,
                count,
                (byte)(address & 0xFF),
                (byte)((address >> 8) & 0xFF),
                (byte)((address >> 16) & 0xFF),
            });
        }

        private static void CustomUnlockAdditionalCommands()
        {
            Console.WriteLine("Sending Custom \"Unlock Additional Commands\" block");
            SendCustom(new List<byte> { 0x80, 0x01, 0x02, 0x03, 0x04 });
        }

        private static void CustomReadSoftwareVersion()
        {
            Console.WriteLine("Sending Custom \"Read Software Version\" block");
            SendCustom(new List<byte> { 0x84 });
        }

        private static void CustomReset()
        {
            Console.WriteLine("Sending Custom Reset block");
            SendCustom(new List<byte> { 0x82 });
        }

        private static void SendCustom(List<byte> blockCustomBytes)
        {
            blockCustomBytes.Insert(0, (byte)BlockTitle.Custom);
            SendBlock(blockCustomBytes);
            ReceiveBlocks();
        }

        private static void EndCommunication()
        {
            Console.WriteLine("Sending EndCommunication block");
            SendBlock(new List<byte> { (byte)BlockTitle.End });
        }

        static byte[] _buf = new byte[1];

        private static void WriteByte(byte b)
        {
            _buf[0] = b;
            _port.Write(_buf, 0, 1);
            var echo = _port.ReadByte(); // Eat the echo
            Debug.Assert(echo == b);
        }

        private static void ReceiveBlocks()
        {
            while (ReceiveBlock())
            {
                SendAckBlock();
            }
        }

        private static bool ReceiveBlock()
        {
            var blockBytes = new List<byte>();

            var blockLength = ReadAndAckByte();
            blockBytes.Add(blockLength);

            var blockCounter = ReadBlockCounter();
            blockBytes.Add(blockCounter);

            var blockTitle = ReadAndAckByte();
            blockBytes.Add(blockTitle);

            for(int i = 0; i < blockLength - 3; i++)
            {
                var b = ReadAndAckByte();
                blockBytes.Add(b);
            }

            var blockEnd = ReadByte();
            blockBytes.Add(blockEnd);
            if (blockEnd != 0x03)
            {
                throw new InvalidOperationException(
                    $"Received block end 0x{blockEnd:X2} but expected 0x03");
            }

            switch (blockTitle)
            {
                case (byte)BlockTitle.AsciiData:
                    DumpAsciiDataBlock(blockBytes);
                    break;

                case (byte)BlockTitle.ACK:
                    DumpAckBlock(blockBytes);
                    return false;

                case (byte)BlockTitle.NAK:
                    DumpNakBlock(blockBytes);
                    return false;

                case (byte)BlockTitle.ReadEepromResponse:
                    DumpReadEepromResponseBlock(blockBytes);
                    break;

                case (byte)BlockTitle.Custom:
                    DumpCustom(blockBytes);
                    break;

                default:
                    DumpUnknownBlock(blockBytes);
                    break;
            }

            return true;
        }

        private static void DumpReadEepromResponseBlock(List<byte> blockBytes)
        {
            Console.Write("Received \"Read EEPROM Response\" block:");
            for (var i = 3; i < blockBytes.Count - 1; i++)
            {
                Console.Write($" {blockBytes[i]:X2}");
            }

            Console.WriteLine();
        }

        private static void DumpCustom(List<byte> blockBytes)
        {
            Console.Write("Received Custom block:");
            for (var i = 3; i < blockBytes.Count - 1; i++)
            {
                Console.Write($" {blockBytes[i]:X2}");
            }

            Console.WriteLine();
        }

        private static void DumpAsciiDataBlock(List<byte> blockBytes)
        {
            Console.Write("Received Ascii data block: \"");
            for(var i = 3; i < blockBytes.Count - 1; i++)
            {
                Console.Write((char)(blockBytes[i] & 0x7F));
            }
            Console.Write("\"");

            if (blockBytes[3] > 0x7F)
            {
                Console.Write(" (More data available via ReadIdent)");
            }

            Console.WriteLine();
        }

        private static void DumpUnknownBlock(List<byte> blockBytes)
        {
            Console.Write("Received unknown block:");
            foreach(var b in blockBytes)
            {
                Console.Write($" 0x{b:X2}");
            }
            Console.WriteLine();
        }

        private static void DumpAckBlock(List<byte> blockBytes)
        {
            Console.WriteLine("Received ACK block");
        }

        private static void DumpNakBlock(List<byte> blockBytes)
        {
            Console.WriteLine("Received NAK block");
        }

        private static void SendAckBlock()
        {
            var blockBytes = new List<byte> { (byte)BlockTitle.ACK };
            SendBlock(blockBytes);
        }

        private static void SendBlock(List<byte> blockBytes)
        {
            var blockLength = (byte)(blockBytes.Count + 2);

            blockBytes.Insert(0, _blockCounter++);
            blockBytes.Insert(0, blockLength);

            foreach(var b in blockBytes)
            {
                WriteByteAndReadAck(b);
            }

            WriteByte(0x03); // Block end, does not get ACK'd
        }

        private static void WriteByteAndReadAck(byte b)
        {
            WriteByte(b);
            ReadComplement(b);
        }

        private static byte ReadAndAckByte()
        {
            var b = ReadByte();
            WriteComplement(b);
            return b;
        }

        private static byte ReadByte()
        {
            var b = (byte)_port.ReadByte();
            return b;
        }

        private static byte ReadBlockCounter()
        {
            var blockCounter = ReadAndAckByte();
            if (blockCounter != _blockCounter)
            {
                throw new InvalidOperationException(
                    $"Received block counter 0x{blockCounter:X2} but expected 0x{_blockCounter:X2}");
            }
            _blockCounter++;
            return blockCounter;
        }

        private static void WriteComplement(byte b)
        {
            var complement = (byte)~b;
            // Console.WriteLine($"Writing complement 0x{complement:X2}");
            WriteByte(complement);
        }

        private static void ReadComplement(byte b)
        {
            var expectedComplement = (byte)~b;
            var actualComplement = ReadByte();
            if (actualComplement != expectedComplement)
            {
                throw new InvalidOperationException(
                    $"Received complement 0x{actualComplement:X2} but expected 0x{expectedComplement:X2}");
            }
        }

        private static void WakeUp(byte controllerAddress)
        {
            Console.WriteLine("Sending wakeup message");

            BitBang5Baud(controllerAddress);
        }

        /// <summary>
        /// Send a byte at 5 baud manually
        /// https://www.blafusel.de/obd/obd2_kw1281.html
        /// </summary>
        /// <param name="b"></param>
        private static void BitBang5Baud(byte b)
        {
            bool noGc = GC.TryStartNoGCRegion(1024 * 1024);

            const int bitsPerSec = 5;
            const int msecPerSec = 1000;
            const int msecPerBit = msecPerSec / bitsPerSec;

            Stopwatch stopWatch = new Stopwatch();

            Action<bool> BitBang = bit =>
            {
                Thread.Sleep((int)(msecPerBit - stopWatch.ElapsedMilliseconds));
                stopWatch.Restart();
                _port.BreakState = !bit;
            };

            BitBang(false); // Start bit

            bool parity = true; // XORed with each bit to produce odd parity
            for (int i = 0; i < 7; i++)
            {
                bool bit = (b & 1) == 1;
                parity ^= bit;
                b >>= 1;

                BitBang(bit);
            }

            BitBang(parity);

            BitBang(true); // Stop bit

            if (noGc)
            {
                GC.EndNoGCRegion();
            }

            _port.DiscardInBuffer();
        }
    }
}
