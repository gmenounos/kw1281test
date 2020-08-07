using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test
{
    /// <summary>
    /// Manages a dialog with a VW controller using the KW1281 protocol.
    /// </summary>
    internal interface IKW1281Dialog
    {
        ControllerInfo WakeUp(byte controllerAddress);

        void EndCommunication();

        void Login(ushort code, ushort workshopCode, byte unknown = 0x00);

        ControllerIdent ReadIdent();

        List<byte> ReadEeprom(ushort address, byte count);

        void WriteEeprom(ushort address, byte value);

        List<byte> ReadRomEeprom(ushort address, byte count);

        /// <summary>
        /// http://www.maltchev.com/kiti/VAG_guide.txt
        /// </summary>
        void CustomUnlockAdditionalCommands();

        /// <summary>
        /// http://www.maltchev.com/kiti/VAG_guide.txt
        /// </summary>
        Dictionary<int, Block> CustomReadSoftwareVersion();

        List<byte> CustomReadRom(uint address, byte count);

        void CustomReset();

        void SendBlock(List<byte> blockBytes);

        List<Block> SendCustom(List<byte> blockCustomBytes);
    }

    internal class KW1281Dialog : IKW1281Dialog
    {
        public KW1281Dialog(IInterface @interface)
        {
            _interface = @interface;
        }

        public ControllerInfo WakeUp(byte controllerAddress)
        {
            _interface.BitBang5Baud(controllerAddress);

            Console.WriteLine("Reading sync byte");
            var syncByte = _interface.ReadByte();
            if (syncByte != 0x55)
            {
                throw new InvalidOperationException(
                    $"Unexpected sync byte: Expected $55, Actual ${syncByte:X2}");
            }

            var keywordLsb = _interface.ReadByte();
            Console.WriteLine($"Keyword Lsb ${keywordLsb:X2}");

            var keywordMsb = ReadAndAckByte();
            Console.WriteLine($"Keyword Msb ${keywordMsb:X2}");

            if (keywordLsb == 0x01 && keywordMsb == 0x8A)
            {
                Console.WriteLine("Protocol is KW 1281 (8N1)");
            }
            else if (keywordLsb == 0xE9 && keywordMsb == 0x8F)
            {
                Console.WriteLine("Protocol is KW 2025 (8N1)");
            }
            else if (keywordLsb == 0x6B && keywordMsb == 0x8F)
            {
                Console.WriteLine("Protocol is KW 2027 (8N1)");
            }

            var blocks = ReceiveBlocks();
            return new ControllerInfo(blocks.Where(b => !b.IsAckNak));
        }

        public void Login(ushort code, ushort workshopCode, byte unknown)
        {
            Console.WriteLine("Sending Login block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.Login,
                (byte)(code >> 8),
                (byte)(code & 0xFF),
                unknown,
                (byte)(workshopCode >> 8),
                (byte)(workshopCode & 0xFF)
            });

            var blocks = ReceiveBlocks();
        }

        public ControllerIdent ReadIdent()
        {
            Console.WriteLine("Sending ReadIdent block");
            SendBlock(new List<byte> { (byte)BlockTitle.ReadIdent });
            var blocks = ReceiveBlocks();
            return new ControllerIdent(blocks.Where(b => !b.IsAckNak));
        }

        /// <summary>
        /// Reads a range of bytes from the EEPROM.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="count"></param>
        /// <returns>The bytes or null if the bytes could not be read</returns>
        public List<byte> ReadEeprom(ushort address, byte count)
        {
            Console.WriteLine($"Sending ReadEeprom block (Address: ${address:X4}, Count: ${count:X2})");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ReadEeprom,
                count,
                (byte)(address >> 8),
                (byte)(address & 0xFF)
            });
            var blocks = ReceiveBlocks();

            if (blocks.Count == 1 && blocks[0] is NakBlock)
            {
                // Permissions issue
                return null;
            }

            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                throw new InvalidOperationException($"ReadEeprom returned {blocks.Count} blocks instead of 1");
            }
            return blocks[0].Body.ToList();
        }

        public void WriteEeprom(ushort address, byte value)
        {
            Console.WriteLine($"Sending WriteEeprom block (Address: ${address:X4}, Value: ${value:X2})");

            const byte count = 0x01; // Maybe we can support more in the future
            var sendBody = new byte[]
            {
                (byte)BlockTitle.WriteEeprom,
                count,
                (byte)(address >> 8),
                (byte)(address & 0xFF),
                value
            };

            SendBlock(sendBody.ToList());
            var blocks = ReceiveBlocks();

            if (blocks.Count == 1 && blocks[0] is NakBlock)
            {
                // Permissions issue
                Console.WriteLine("WriteEeprom failed");
                return;
            }

            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                Console.WriteLine($"WriteEeprom returned {blocks.Count} blocks instead of 1");
                return;
            }

            var block = blocks[0];
            if (!(block is WriteEepromResponseBlock))
            {
                Console.WriteLine($"Expected WriteEepromResponseBlock but got {block.GetType()}");
                return;
            }

            if (!Enumerable.SequenceEqual(block.Body, sendBody.Skip(1)))
            {
                Console.WriteLine("WriteEepromResponseBlock body does not match WriteEepromBlock");
                return;
            }
        }

        public List<byte> ReadRomEeprom(ushort address, byte count)
        {
            Console.WriteLine($"Sending ReadEeprom block (Address: ${address:X4}, Count: ${count:X2})");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ReadRomEeprom,
                count,
                (byte)(address >> 8),
                (byte)(address & 0xFF)
            });
            var blocks = ReceiveBlocks();

            if (blocks.Count == 1 && blocks[0] is NakBlock)
            {
                return new List<byte>();
            }

            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                throw new InvalidOperationException($"ReadRomEeprom returned {blocks.Count} blocks instead of 1");
            }
            return blocks[0].Body.ToList();
        }

        public List<byte> CustomReadRom(uint address, byte count)
        {
            Console.WriteLine($"Sending Custom \"Read ROM\" block (Address: ${address:X6}, Count: ${count:X2})");
            var blocks = SendCustom(new List<byte>
            {
                0x86,
                count,
                (byte)(address & 0xFF),
                (byte)((address >> 8) & 0xFF),
                (byte)((address >> 16) & 0xFF),
            });
            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                throw new InvalidOperationException($"Custom \"Read ROM\" returned {blocks.Count} blocks instead of 1");
            }
            return blocks[0].Body.ToList();
        }

        public void CustomUnlockAdditionalCommands()
        {
            Console.WriteLine("Sending Custom \"Unlock Additional Commands\" block");
            SendCustom(new List<byte> { 0x80, 0x01, 0x02, 0x03, 0x04 });
        }

        public Dictionary<int, Block> CustomReadSoftwareVersion()
        {
            var versionBlocks = new Dictionary<int, Block>();

            Console.WriteLine("Sending Custom \"Read Software Version\" blocks");

            // The cluster can return 4 variations of software version, specified by the 2nd byte
            // of the block:
            // 0x00 - Cluster software version
            // 0x01 - Unknown
            // 0x02 - Unknown
            // 0x03 - Unknown
            for (byte variation = 0x00; variation < 0x04; variation++)
            {
                var blocks = SendCustom(new List<byte> { 0x84, variation });
                foreach (var block in blocks.Where(b => !b.IsAckNak))
                {
                    if (variation == 0x00 || variation == 0x03)
                    {
                        Console.WriteLine($"{variation:X2}: {DumpMixedContent(block)}");
                    }
                    else
                    {
                        Console.WriteLine($"{variation:X2}: {DumpBinaryContent(block)}");
                    }
                    versionBlocks[variation] = block;
                }
            }

            return versionBlocks;
        }

        private string DumpMixedContent(Block block)
        {
            if (block.IsNak)
            {
                return "NAK";
            }

            char mode = '?';
            var sb = new StringBuilder();
            foreach(var b in block.Body)
            {
                if (b >= 32 && b <= 126)
                {
                    mode = 'A';

                    sb.Append((char)b);
                }
                else
                {
                    if (mode == 'A')
                    {
                        sb.Append(" ");
                    }
                    mode = 'X';

                    sb.Append($"${b:X2} ");
                }
            }
            return sb.ToString();
        }

        private string DumpBinaryContent(Block block)
        {
            if (block.IsNak)
            {
                return "NAK";
            }

            var sb = new StringBuilder();
            foreach (var b in block.Body)
            {
                sb.Append($"${b:X2} ");
            }
            return sb.ToString();
        }

        public void CustomReset()
        {
            Console.WriteLine("Sending Custom Reset block");
            SendCustom(new List<byte> { 0x82 });
        }

        public List<Block> SendCustom(List<byte> blockCustomBytes)
        {
            blockCustomBytes.Insert(0, (byte)BlockTitle.Custom);
            SendBlock(blockCustomBytes);
            return ReceiveBlocks();
        }

        public void EndCommunication()
        {
            Console.WriteLine("Sending EndCommunication block");
            SendBlock(new List<byte> { (byte)BlockTitle.End });
        }

        public void SendBlock(List<byte> blockBytes)
        {
            var blockLength = (byte)(blockBytes.Count + 2);

            blockBytes.Insert(0, _blockCounter.Value);
            _blockCounter++;

            blockBytes.Insert(0, blockLength);

            foreach (var b in blockBytes)
            {
                WriteByteAndReadAck(b);
            }

            _interface.WriteByte(0x03); // Block end, does not get ACK'd
        }

        private List<Block> ReceiveBlocks()
        {
            var blocks = new List<Block>();

            while (true)
            {
                var block = ReceiveBlock();
                blocks.Add(block); // TODO: Maybe don't add the block if it's an Ack
                if (block is AckBlock || block is NakBlock)
                {
                    break;
                }
                SendAckBlock();
            }

            return blocks;
        }

        private byte ReadAndAckByte()
        {
            var b = _interface.ReadByte();
            WriteComplement(b);
            return b;
        }

        private void WriteComplement(byte b)
        {
            var complement = (byte)~b;
            _interface.WriteByte(complement);
        }

        private void WriteByteAndReadAck(byte b)
        {
            _interface.WriteByte(b);
            ReadComplement(b);
        }

        private void ReadComplement(byte b)
        {
            var expectedComplement = (byte)~b;
            var actualComplement = _interface.ReadByte();
            if (actualComplement != expectedComplement)
            {
                throw new InvalidOperationException(
                    $"Received complement ${actualComplement:X2} but expected ${expectedComplement:X2}");
            }
        }

        private Block ReceiveBlock()
        {
            var blockBytes = new List<byte>();

            var blockLength = ReadAndAckByte();
            blockBytes.Add(blockLength);

            var blockCounter = ReadBlockCounter();
            blockBytes.Add(blockCounter);

            var blockTitle = ReadAndAckByte();
            blockBytes.Add(blockTitle);

            for (int i = 0; i < blockLength - 3; i++)
            {
                var b = ReadAndAckByte();
                blockBytes.Add(b);
            }

            var blockEnd = _interface.ReadByte();
            blockBytes.Add(blockEnd);
            if (blockEnd != 0x03)
            {
                throw new InvalidOperationException(
                    $"Received block end ${blockEnd:X2} but expected $03");
            }

            switch (blockTitle)
            {
                case (byte)BlockTitle.AsciiData:
                    return new AsciiDataBlock(blockBytes);

                case (byte)BlockTitle.ACK:
                    return new AckBlock(blockBytes);

                case (byte)BlockTitle.NAK:
                    return new NakBlock(blockBytes);

                case (byte)BlockTitle.ReadEepromResponse:
                    return new ReadEepromResponseBlock(blockBytes);

                case (byte)BlockTitle.WriteEepromResponse:
                    return new WriteEepromResponseBlock(blockBytes);

                case (byte)BlockTitle.ReadRomEepromResponse:
                    return new ReadRomEepromResponse(blockBytes);

                case (byte)BlockTitle.Custom:
                    return new CustomBlock(blockBytes);

                default:
                    return new UnknownBlock(blockBytes);
            }
        }

        private void SendAckBlock()
        {
            var blockBytes = new List<byte> { (byte)BlockTitle.ACK };
            SendBlock(blockBytes);
        }

        private byte ReadBlockCounter()
        {
            var blockCounter = ReadAndAckByte();
            if (!_blockCounter.HasValue)
            {
                // First block
                _blockCounter = blockCounter;
            }
            else if (blockCounter != _blockCounter)
            {
                throw new InvalidOperationException(
                    $"Received block counter ${blockCounter:X2} but expected ${_blockCounter:X2}");
            }
            _blockCounter++;
            return blockCounter;
        }

        private readonly IInterface _interface;
        private byte? _blockCounter = null;
    }
}
