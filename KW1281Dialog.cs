using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitFab.KW1281Test
{
    /// <summary>
    /// Manages a dialog with a VW controller using the KW1281 protocol.
    /// </summary>
    internal interface IKW1281Dialog
    {
        ControllerInfo ReadEcuInfo();

        void EndCommunication();

        void Login(ushort code, ushort workshopCode, byte unknown = 0x00);

        ControllerIdent ReadIdent();

        List<byte> ReadEeprom(ushort address, byte count);

        bool WriteEeprom(ushort address, List<byte> values);

        List<byte> ReadRomEeprom(ushort address, byte count);

        /// <summary>
        /// http://www.maltchev.com/kiti/VAG_guide.txt
        /// This unlocks additional custom commands $81-$AF
        /// </summary>
        void CustomUnlockAdditionalCommands();

        /// <summary>
        /// http://www.maltchev.com/kiti/VAG_guide.txt
        /// </summary>
        Dictionary<int, Block> CustomReadSoftwareVersion();

        List<byte> CustomReadMemory(uint address, byte count);

        void CustomReset();

        void SendBlock(List<byte> blockBytes);

        List<Block> SendCustom(List<byte> blockCustomBytes);

        List<byte> ReadCcmRom(byte seg, byte msb, byte lsb, byte count);

        List<byte> CustomReadNecRom(ushort address, byte count);

        /// <summary>
        /// Keep the dialog alive by sending an ACK and receiving a response.
        /// </summary>
        void KeepAlive();

        ActuatorTestResponseBlock ActuatorTest(byte value);

        List<FaultCode> ReadFaultCodes();
    }

    /// <summary>
    /// Used for commands such as ActuatorTest which need to be kept alive with ACKs while waiting
    /// for user input.
    /// </summary>
    internal class KW1281KeepAlive : IDisposable
    {
        private readonly IKW1281Dialog _kw1281Dialog;
        private volatile bool _cancel = false;
        private Task _keepAliveTask = null;

        public KW1281KeepAlive(IKW1281Dialog kw1281Dialog)
        {
            _kw1281Dialog = kw1281Dialog;
        }

        public ActuatorTestResponseBlock ActuatorTest(byte value)
        {
            Pause();
            var result = _kw1281Dialog.ActuatorTest(value);
            Resume();
            return result;
        }

        public void Dispose()
        {
            Pause();
        }

        private void Pause()
        {
            _cancel = true;
            if (_keepAliveTask != null)
            {
                _keepAliveTask.Wait();
            }
        }

        private void Resume()
        {
            _keepAliveTask = Task.Run(KeepAlive);
        }

        private void KeepAlive()
        {
            _cancel = false;
            while (!_cancel)
            {
                _kw1281Dialog.KeepAlive();
                Console.Write(".");
            }
        }
    }

    internal class KW1281Dialog : IKW1281Dialog
    {
        public ControllerInfo ReadEcuInfo()
        {
            var blocks = ReceiveBlocks();
            return new ControllerInfo(blocks.Where(b => !b.IsAckNak));
        }

        public void Login(ushort code, ushort workshopCode, byte unknown)
        {
            Logger.WriteLine("Sending Login block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.Login,
                (byte)(code >> 8),
                (byte)(code & 0xFF),
                unknown,
                (byte)(workshopCode >> 8),
                (byte)(workshopCode & 0xFF)
            });

            _ = ReceiveBlocks();
        }

        public ControllerIdent ReadIdent()
        {
            Logger.WriteLine("Sending ReadIdent block");
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
            Logger.WriteLine($"Sending ReadEeprom block (Address: ${address:X4}, Count: ${count:X2})");
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

        /// <summary>
        /// Reads a range of bytes from the CCM ROM.
        /// </summary>
        /// <param name="seg">0-15</param>
        /// <param name="msb">0-15</param>
        /// <param name="lsb">0-255</param>
        /// <param name="count">8(-12?)</param>
        /// <returns>The bytes or null if the bytes could not be read</returns>
        public List<byte> ReadCcmRom(byte seg, byte msb, byte lsb, byte count)
        {
            Logger.WriteLine(
                $"Sending ReadEeprom block (Address: ${seg:X2}{msb:X2}{lsb:X2}, Count: ${count:X2})");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ReadEeprom,
                count,
                msb,
                lsb,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(seg << 4)
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

        public bool WriteEeprom(ushort address, List<byte> values)
        {
            Logger.WriteLine($"Sending WriteEeprom block (Address: ${address:X4}, Values: {DumpBytes(values)}");

            byte count = (byte)values.Count;
            var sendBody = new List<byte>
            {
                (byte)BlockTitle.WriteEeprom,
                count,
                (byte)(address >> 8),
                (byte)(address & 0xFF),
            };
            sendBody.AddRange(values);

            SendBlock(sendBody.ToList());
            var blocks = ReceiveBlocks();

            if (blocks.Count == 1 && blocks[0] is NakBlock)
            {
                // Permissions issue
                Logger.WriteLine("WriteEeprom failed");
                return false;
            }

            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                Logger.WriteLine($"WriteEeprom returned {blocks.Count} blocks instead of 1");
                return false;
            }

            var block = blocks[0];
            if (!(block is WriteEepromResponseBlock))
            {
                Logger.WriteLine($"Expected WriteEepromResponseBlock but got {block.GetType()}");
                return false;
            }

            if (!Enumerable.SequenceEqual(block.Body, sendBody.Skip(1).Take(4)))
            {
                Logger.WriteLine("WriteEepromResponseBlock body does not match WriteEepromBlock");
                return false;
            }

            return true;
        }

        public List<byte> ReadRomEeprom(ushort address, byte count)
        {
            Logger.WriteLine($"Sending ReadRomEeprom block (Address: ${address:X4}, Count: ${count:X2})");
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

        public List<byte> CustomReadMemory(uint address, byte count)
        {
            Logger.WriteLine($"Sending Custom \"Read Memory\" block (Address: ${address:X6}, Count: ${count:X2})");
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
                throw new InvalidOperationException($"Custom \"Read Memory\" returned {blocks.Count} blocks instead of 1");
            }
            return blocks[0].Body.ToList();
        }

        /// <summary>
        /// Read the low 64KB of the cluster's NEC controller ROM.
        /// For MFA clusters, that should cover the entire ROM.
        /// For FIS clusters, the ROM is 128KB and more work is needed to retrieve the high 64KB.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public List<byte> CustomReadNecRom(ushort address, byte count)
        {
            Logger.WriteLine($"Sending Custom \"Read NEC ROM\" block (Address: ${address:X4}, Count: ${count:X2})");
            var blocks = SendCustom(new List<byte>
            {
                0xA6,
                count,
                (byte)(address & 0xFF),
                (byte)((address >> 8) & 0xFF),
            });
            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                throw new InvalidOperationException($"Custom \"Read NEC ROM\" returned {blocks.Count} blocks instead of 1");
            }
            return blocks[0].Body.ToList();
        }

        public void CustomUnlockAdditionalCommands()
        {
            Logger.WriteLine("Sending Custom \"Unlock Additional Commands\" block");
            SendCustom(new List<byte> { 0x80, 0x01, 0x02, 0x03, 0x04 });
        }

        public Dictionary<int, Block> CustomReadSoftwareVersion()
        {
            var versionBlocks = new Dictionary<int, Block>();

            Logger.WriteLine("Sending Custom \"Read Software Version\" blocks");

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
                        Logger.WriteLine($"{variation:X2}: {DumpMixedContent(block)}");
                    }
                    else
                    {
                        Logger.WriteLine($"{variation:X2}: {DumpBinaryContent(block)}");
                    }
                    versionBlocks[variation] = block;
                }
            }

            return versionBlocks;
        }

        private static string DumpMixedContent(Block block)
        {
            if (block.IsNak)
            {
                return "NAK";
            }

            return DumpMixedContent(block.Body);
        }

        /// <summary>
        /// Todo: Move to utility class
        /// </summary>
        public static string DumpMixedContent(IEnumerable<byte> content)
        {
            char mode = '?';
            var sb = new StringBuilder();
            foreach (var b in content)
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
                        sb.Append(' ');
                    }
                    mode = 'X';

                    sb.Append($"${b:X2} ");
                }
            }
            return sb.ToString();
        }

        private static string DumpBinaryContent(Block block)
        {
            if (block.IsNak)
            {
                return "NAK";
            }

            return DumpBytes(block.Body);
        }

        private static string DumpBytes(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($"${b:X2} ");
            }
            return sb.ToString();
        }

        public void CustomReset()
        {
            Logger.WriteLine("Sending Custom Reset block");
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
            Logger.WriteLine("Sending EndCommunication block");
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
                // Thread.Sleep(1); // TODO: Is this necessary?
            }

            _kwpCommon.WriteByte(0x03); // Block end, does not get ACK'd
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

        private void WriteByteAndReadAck(byte b)
        {
            _kwpCommon.WriteByte(b);
            _kwpCommon.ReadComplement(b);
        }

        private Block ReceiveBlock()
        {
            var blockBytes = new List<byte>();

            var blockLength = _kwpCommon.ReadAndAckByte();
            blockBytes.Add(blockLength);

            var blockCounter = ReadBlockCounter();
            blockBytes.Add(blockCounter);

            var blockTitle = _kwpCommon.ReadAndAckByte();
            blockBytes.Add(blockTitle);

            for (int i = 0; i < blockLength - 3; i++)
            {
                var b = _kwpCommon.ReadAndAckByte();
                blockBytes.Add(b);
            }

            var blockEnd = _kwpCommon.ReadByte();
            blockBytes.Add(blockEnd);
            if (blockEnd != 0x03)
            {
                throw new InvalidOperationException(
                    $"Received block end ${blockEnd:X2} but expected $03");
            }

            return blockTitle switch
            {
                (byte)BlockTitle.ACK => new AckBlock(blockBytes),
                (byte)BlockTitle.ActuatorTestResponse => new ActuatorTestResponseBlock(blockBytes),
                (byte)BlockTitle.AsciiData =>
                    blockBytes[3] == 0x00 ? new CodingWscBlock(blockBytes) : new AsciiDataBlock(blockBytes),
                (byte)BlockTitle.Custom => new CustomBlock(blockBytes),
                (byte)BlockTitle.NAK => new NakBlock(blockBytes),
                (byte)BlockTitle.ReadEepromResponse => new ReadEepromResponseBlock(blockBytes),
                (byte)BlockTitle.FaultCodesResponse => new FaultCodesBlock(blockBytes),
                (byte)BlockTitle.ReadRomEepromResponse => new ReadRomEepromResponse(blockBytes),
                (byte)BlockTitle.WriteEepromResponse => new WriteEepromResponseBlock(blockBytes),
                _ => new UnknownBlock(blockBytes),
            };
        }

        private void SendAckBlock()
        {
            var blockBytes = new List<byte> { (byte)BlockTitle.ACK };
            SendBlock(blockBytes);
        }

        private byte ReadBlockCounter()
        {
            var blockCounter = _kwpCommon.ReadAndAckByte();
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

        public void KeepAlive()
        {
            SendAckBlock();
            var block = ReceiveBlock();
            if (block is not AckBlock)
            {
                throw new InvalidOperationException(
                    $"Received 0x{block.Title:X2} block but expected ACK");
            }
        }

        public ActuatorTestResponseBlock ActuatorTest(byte value)
        {
            Logger.WriteLine($"Sending actuator test 0x{value:X2} block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ActuatorTest,
                value
            });

            var blocks = ReceiveBlocks();
            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                Logger.WriteLine($"ActuatorTest returned {blocks.Count} blocks instead of 1");
                return null;
            }

            var block = blocks[0];
            if (!(block is ActuatorTestResponseBlock))
            {
                Logger.WriteLine($"Expected ActuatorTestResponseBlock but got {block.GetType()}");
                return null;
            }

            return (ActuatorTestResponseBlock)block;
        }

        public List<FaultCode> ReadFaultCodes()
        {
            Logger.WriteLine($"Sending ReadFaultCodes block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.FaultCodesRead
            });

            var blocks = ReceiveBlocks();
            blocks = blocks.Where(b => !b.IsAckNak).ToList();

            var faultCodes = new List<FaultCode>();
            foreach (var block in blocks)
            {
                if (!(block is FaultCodesBlock))
                {
                    Logger.WriteLine($"Expected FaultCodesBlock but got {block.GetType()}");
                    return null;
                }

                var faultCodesBlock = (FaultCodesBlock)block;
                faultCodes.AddRange(faultCodesBlock.FaultCodes);
            }

            return faultCodes;
        }

        private byte? _blockCounter = null;

        private readonly IKwpCommon _kwpCommon;

        public KW1281Dialog(IKwpCommon kwpCommon)
        {
            _kwpCommon = kwpCommon;
        }
    }
}
