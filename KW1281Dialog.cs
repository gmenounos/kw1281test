using BitFab.KW1281Test.Blocks;
using BitFab.KW1281Test.Logging;
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
        ControllerInfo Connect();

        void EndCommunication();

        void SetDisconnected();

        void Login(ushort code, int workshopCode);

        List<ControllerIdent> ReadIdent();

        /// <summary>
        /// Corresponds to VDS-Pro function 19
        /// </summary>
        List<byte>? ReadEeprom(ushort address, byte count);

        bool WriteEeprom(ushort address, List<byte> values);

        /// <summary>
        /// Corresponds to VDS-Pro functions 21 and 22
        /// </summary>
        List<byte> ReadRomEeprom(ushort address, byte count);

        /// <summary>
        /// Corresponds to VDS-Pro functions 20 and 25
        /// </summary>
        List<byte>? ReadRam(ushort address, byte count);

        bool AdaptationRead(byte channelNumber);

        bool AdaptationTest(byte channelNumber, ushort channelValue);

        bool AdaptationSave(byte channelNumber, ushort channelValue, int workshopCode);

        void SendBlock(List<byte> blockBytes);

        List<Block> ReceiveBlocks();

        List<byte>? ReadCcmRom(byte seg, byte msb, byte lsb, byte count);

        /// <summary>
        /// Keep the dialog alive by sending an ACK and receiving a response.
        /// </summary>
        void KeepAlive();

        ActuatorTestResponseBlock? ActuatorTest(byte value);

        List<FaultCode>? ReadFaultCodes();

        /// <summary>
        /// Clear all of the controllers fault codes.
        /// </summary>
        /// <param name="controllerAddress"></param>
        /// <returns>Any remaining fault codes.</returns>
        List<FaultCode>? ClearFaultCodes(int controllerAddress);

        /// <summary>
        /// Set the controller's software coding and workshop code.
        /// </summary>
        /// <param name="controllerAddress"></param>
        /// <param name="softwareCoding"></param>
        /// <param name="workshopCode"></param>
        /// <returns>True if successful.</returns>
        bool SetSoftwareCoding(int controllerAddress, int softwareCoding, int workshopCode);

        bool GroupRead(byte groupNumber, bool useBasicSetting = false);

        public IKwpCommon KwpCommon { get; }
    }

    internal class KW1281Dialog : IKW1281Dialog
    {
        public ControllerInfo Connect()
        {
            _isConnected = true;
            var blocks = ReceiveBlocks();
            return new ControllerInfo(blocks.Where(b => !b.IsAckNak));
        }

        public void Login(ushort code, int workshopCode)
        {
            Log.WriteLine("Sending Login block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.Login,
                (byte)(code >> 8),
                (byte)(code & 0xFF),
                (byte)(workshopCode >> 16),
                (byte)((workshopCode >> 8) & 0xFF),
                (byte)(workshopCode & 0xFF)
            });

            _ = ReceiveBlocks();
        }

        public List<ControllerIdent> ReadIdent()
        {
            var idents = new List<ControllerIdent>();
            bool moreAvailable;
            do
            {
                Log.WriteLine("Sending ReadIdent block");

                SendBlock(new List<byte> { (byte)BlockTitle.ReadIdent });

                var blocks = ReceiveBlocks();
                var ident = new ControllerIdent(blocks.Where(b => !b.IsAckNak));
                idents.Add(ident);

                moreAvailable = blocks
                    .OfType<AsciiDataBlock>()
                    .Any(b => b.MoreDataAvailable);
            } while (moreAvailable);

            return idents;
        }

        /// <summary>
        /// Reads a range of bytes from the EEPROM.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="count"></param>
        /// <returns>The bytes or null if the bytes could not be read</returns>
        public List<byte>? ReadEeprom(ushort address, byte count)
        {
            Log.WriteLine($"Sending ReadEeprom block (Address: ${address:X4}, Count: ${count:X2})");
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
        /// Reads a range of bytes from the RAM.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="count"></param>
        /// <returns>The bytes or null if the bytes could not be read</returns>
        public List<byte>? ReadRam(ushort address, byte count)
        {
            Log.WriteLine($"Sending ReadRam block (Address: ${address:X4}, Count: ${count:X2})");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ReadRam,
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
        public List<byte>? ReadCcmRom(byte seg, byte msb, byte lsb, byte count)
        {
            Log.WriteLine(
                $"Sending ReadEeprom block (Address: ${seg:X2}{msb:X2}{lsb:X2}, Count: ${count:X2})");
            var block = new List<byte>
            {
                (byte)BlockTitle.ReadEeprom,
                count,
                msb,
                lsb,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(seg << 4)
            };
            // Log.WriteLine($"SEND {Utils.Dump(block)}");
            SendBlock(block);
            var blocks = ReceiveBlocks();

            if (blocks.Count == 1 && blocks[0] is NakBlock)
            {
                // Log.WriteLine($"RECV {Utils.Dump(blocks.First().Bytes)}");
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
            Log.WriteLine($"Sending WriteEeprom block (Address: ${address:X4}, Values: {Utils.DumpBytes(values)}");

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
                Log.WriteLine("WriteEeprom failed");
                return false;
            }

            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                Log.WriteLine($"WriteEeprom returned {blocks.Count} blocks instead of 1");
                return false;
            }

            var block = blocks[0];
            if (block is not WriteEepromResponseBlock)
            {
                Log.WriteLine($"Expected WriteEepromResponseBlock but got {block.GetType()}");
                return false;
            }

            if (!Enumerable.SequenceEqual(block.Body, sendBody.Skip(1).Take(4)))
            {
                Log.WriteLine("WriteEepromResponseBlock body does not match WriteEepromBlock");
                return false;
            }

            return true;
        }

        public List<byte> ReadRomEeprom(ushort address, byte count)
        {
            Log.WriteLine($"Sending ReadRomEeprom block (Address: ${address:X4}, Count: ${count:X2})");
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

        public void EndCommunication()
        {
            if (_isConnected)
            {
                Log.WriteLine("Sending EndCommunication block");
                SendBlock(new List<byte> { (byte)BlockTitle.End });
                _isConnected = false;
            }
        }

        public void SetDisconnected()
        {
            _isConnected = false;
            _blockCounter = null;
        }

        public void SendBlock(List<byte> blockBytes)
        {
            var blockLength = (byte)(blockBytes.Count + 2);

            blockBytes.Insert(0, _blockCounter!.Value);
            _blockCounter++;

            blockBytes.Insert(0, blockLength);

            Thread.Sleep(1);

            foreach (var b in blockBytes)
            {
                WriteByteAndReadAck(b);
                Thread.Sleep(1);
            }

            KwpCommon.WriteByte(0x03); // Block end, does not get ACK'd
        }

        public List<Block> ReceiveBlocks()
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
            KwpCommon.WriteByte(b);
            KwpCommon.ReadComplement(b);
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

            var blockEnd = KwpCommon.ReadByte();
            blockBytes.Add(blockEnd);
            if (blockEnd != 0x03)
            {
                throw new InvalidOperationException(
                    $"Received block end ${blockEnd:X2} but expected $03");
            }

            return (BlockTitle)blockTitle switch
            {
                BlockTitle.ACK => new AckBlock(blockBytes),
                BlockTitle.GroupReadResponseWithText => new GroupReadResponseWithTextBlock(blockBytes),
                BlockTitle.ActuatorTestResponse => new ActuatorTestResponseBlock(blockBytes),
                BlockTitle.AsciiData =>
                    blockBytes[3] == 0x00 ? new CodingWscBlock(blockBytes) : new AsciiDataBlock(blockBytes),
                BlockTitle.Custom => new CustomBlock(blockBytes),
                BlockTitle.NAK => new NakBlock(blockBytes),
                BlockTitle.ReadEepromResponse => new ReadEepromResponseBlock(blockBytes),
                BlockTitle.FaultCodesResponse => new FaultCodesBlock(blockBytes),
                BlockTitle.ReadRomEepromResponse => new ReadRomEepromResponse(blockBytes),
                BlockTitle.WriteEepromResponse => new WriteEepromResponseBlock(blockBytes),
                BlockTitle.AdaptationResponse => new AdaptationResponseBlock(blockBytes),
                BlockTitle.GroupReadResponse => new GroupReadResponseBlock(blockBytes),
                BlockTitle.RawDataReadResponse => new RawDataReadResponseBlock(blockBytes),
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

        private byte ReadAndAckByte()
        {
            var b = KwpCommon.ReadByte();
            Thread.Sleep(1);
            var complement = (byte)~b;
            KwpCommon.WriteByte(complement);
            return b;
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

        public ActuatorTestResponseBlock? ActuatorTest(byte value)
        {
            Log.WriteLine($"Sending actuator test 0x{value:X2} block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.ActuatorTest,
                value
            });

            var blocks = ReceiveBlocks();
            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                Log.WriteLine($"ActuatorTest returned {blocks.Count} blocks instead of 1");
                return null;
            }

            var block = blocks[0];
            if (block is not ActuatorTestResponseBlock)
            {
                Log.WriteLine($"Expected ActuatorTestResponseBlock but got {block.GetType()}");
                return null;
            }

            return (ActuatorTestResponseBlock)block;
        }

        public List<FaultCode>? ReadFaultCodes()
        {
            Log.WriteLine($"Sending ReadFaultCodes block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.FaultCodesRead
            });

            var blocks = ReceiveBlocks();
            blocks = blocks.Where(b => !b.IsAckNak).ToList();

            var faultCodes = new List<FaultCode>();
            foreach (var block in blocks)
            {
                if (block is not FaultCodesBlock)
                {
                    Log.WriteLine($"Expected FaultCodesBlock but got {block.GetType()}");
                    return null;
                }

                var faultCodesBlock = (FaultCodesBlock)block;
                faultCodes.AddRange(faultCodesBlock.FaultCodes);
            }

            return faultCodes;
        }

        public List<FaultCode>? ClearFaultCodes(int controllerAddress)
        {
            Log.WriteLine($"Sending ClearFaultCodes block");
            SendBlock(new List<byte>
            {
                (byte)BlockTitle.FaultCodesDelete
            });

            var blocks = ReceiveBlocks();
            blocks = blocks.Where(b => !b.IsAckNak).ToList();

            var faultCodes = new List<FaultCode>();
            foreach (var block in blocks)
            {
                if (block is not FaultCodesBlock)
                {
                    Log.WriteLine($"Expected FaultCodesBlock but got {block.GetType()}");
                    return null;
                }

                var faultCodesBlock = (FaultCodesBlock)block;
                faultCodes.AddRange(faultCodesBlock.FaultCodes);
            }

            return faultCodes;
        }

        public bool SetSoftwareCoding(int controllerAddress, int softwareCoding, int workshopCode)
        {
            // Workshop codes > 65535 overflow into the low bit of the software coding
            var bytes = new List<byte>
            {
                (byte)BlockTitle.SoftwareCoding,
                (byte)((softwareCoding * 2) / 256),
                (byte)((softwareCoding * 2) % 256),
                (byte)((workshopCode & 65535) / 256),
                (byte)(workshopCode % 256)
            };

            if (workshopCode > 65535)
            {
                bytes[2]++;
            }

            Log.WriteLine($"Sending SoftwareCoding block");
            SendBlock(bytes);

            var blocks = ReceiveBlocks();
            if (blocks.Count == 1 && blocks[0] is NakBlock)
            {
                return false;
            }

            var controllerInfo = new ControllerInfo(blocks.Where(b => !b.IsAckNak));
            return
                controllerInfo.SoftwareCoding == softwareCoding &&
                controllerInfo.WorkshopCode == workshopCode;
        }

        public bool AdaptationRead(byte channelNumber)
        {
            var bytes = new List<byte>
            {
                (byte)BlockTitle.AdaptationRead,
                channelNumber
            };

            Log.WriteLine($"Sending AdaptationRead block");
            SendBlock(bytes);

            return ReceiveAdaptationBlock();
        }

        public bool AdaptationTest(byte channelNumber, ushort channelValue)
        {
            var bytes = new List<byte>
            {
                (byte)BlockTitle.AdaptationTest,
                channelNumber,
                (byte)(channelValue / 256),
                (byte)(channelValue % 256)
            };

            Log.WriteLine($"Sending AdaptationTest block");
            SendBlock(bytes);

            return ReceiveAdaptationBlock();
        }

        public bool AdaptationSave(byte channelNumber, ushort channelValue, int workshopCode)
        {
            var bytes = new List<byte>
            {
                (byte)BlockTitle.AdaptationSave,
                channelNumber,
                (byte)(channelValue / 256),
                (byte)(channelValue % 256),
                (byte)(workshopCode >> 16),
                (byte)((workshopCode >> 8) & 0xFF),
                (byte)(workshopCode & 0xFF)
            };

            Log.WriteLine($"Sending AdaptationSave block");
            SendBlock(bytes);

            return ReceiveAdaptationBlock();
        }

        private bool ReceiveAdaptationBlock()
        {
            var responseBlock = ReceiveBlock();
            if (responseBlock is NakBlock)
            {
                Log.WriteLine($"Received a NAK.");
                return false;
            }

            if (responseBlock is not AdaptationResponseBlock adaptationResponse)
            {
                Log.WriteLine($"Expected an Adaptation response block but received a ${responseBlock.Title:X2} block.");
                return false;
            }

            Log.WriteLine($"Adaptation value: {adaptationResponse.ChannelValue}");

            return true;
        }

        public bool GroupRead(byte groupNumber, bool useBasicSetting = false)
        {
            if (groupNumber == 0)
            {
                return RawDataRead(useBasicSetting);
            }

            if (useBasicSetting)
            {
                Log.WriteLine($"Sending Basic Setting Read blocks...");
            }
            else
            {
                Log.WriteLine($"Sending Group Read blocks...");
            }

            GroupReadResponseWithTextBlock? textBlock = null;

            Log.WriteLine("[Up arrow | Down arrow | Q to quit]", LogDest.Console);
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);
                    if (keyInfo.Key == ConsoleKey.UpArrow)
                    {
                        if (groupNumber < 255)
                        {
                            groupNumber++;
                        }
                    }
                    else if (keyInfo.Key == ConsoleKey.DownArrow)
                    {
                        if (groupNumber > 1)
                        {
                            groupNumber--;
                        }
                    }
                    else if (keyInfo.Key == ConsoleKey.Q)
                    {
                        break;
                    }
                }

                var bytes = new List<byte>
                {
                    (byte)(useBasicSetting ? BlockTitle.BasicSettingRead : BlockTitle.GroupRead),
                    groupNumber
                };
                SendBlock(bytes);

                var responseBlock = ReceiveBlock();
                if (responseBlock is NakBlock)
                {
                    Overlay($"Group {groupNumber:D3}: Not Available");
                }
                else if (responseBlock is GroupReadResponseWithTextBlock groupReadResponseWithText)
                {
                    Log.WriteLine($"{groupReadResponseWithText}", LogDest.File);
                    textBlock = groupReadResponseWithText;
                }
                else if (responseBlock is GroupReadResponseBlock groupReading)
                {
                    Overlay($"Group {groupNumber:D3}: {groupReading}");
                }
                else if (responseBlock is RawDataReadResponseBlock rawData)
                {
                    if (textBlock != null && rawData.Body.Count > 0)
                    {
                        var sb = new StringBuilder($"Group {groupNumber:D3}: ");
                        sb.Append(textBlock.GetText(rawData.Body[0]));
                        sb.Append(Utils.DumpDecimal(rawData.Body.Skip(1)));
                        Overlay(sb.ToString());
                    }
                    else
                    {
                        Overlay($"Group {groupNumber:D3}: {rawData}");
                    }
                }
                else
                {
                    Log.WriteLine($"Expected a Group Reading response block but received a ${responseBlock.Title:X2} block.");
                    return false;
                }
            }
            Log.WriteLine(LogDest.Console);

            return true;
        }

        private bool RawDataRead(bool useBasicSetting)
        {
            if (useBasicSetting)
            {
                Log.WriteLine($"Sending Basic Setting Raw Data Read block");
            }
            else
            {
                Log.WriteLine($"Sending Raw Data Read block");
            }

            Log.WriteLine("[Press a key to quit]", LogDest.Console);
            while (!Console.KeyAvailable)
            {
                var bytes = new List<byte>
                {
                    (byte)(useBasicSetting ? BlockTitle.BasicSettingRawDataRead : BlockTitle.RawDataRead)
                };
                SendBlock(bytes);

                var responseBlock = ReceiveBlock();

                if (responseBlock is not RawDataReadResponseBlock rawDataReadResponse)
                {
                    Log.WriteLine($"Expected a Raw Data Read response block but received a ${responseBlock.Title:X2} block.");
                    return false;
                }

                Overlay(rawDataReadResponse.ToString());
            }
            Log.WriteLine(LogDest.Console);

            return true;
        }

        /// <summary>
        /// Erase the current console line and replace it with message.
        /// Also writes the message to the log.
        /// </summary>
        private static void Overlay(string message)
        {
            (int left, int top) = Console.GetCursorPosition();
            Console.SetCursorPosition(0, top);
            if (left > 0)
            {
                Log.Write(new string(' ', left), LogDest.Console);
                Console.SetCursorPosition(0, top);
            }
            Log.Write(message, LogDest.Console);
            Log.WriteLine(message, LogDest.File);
        }

        public IKwpCommon KwpCommon { get; }

        private bool _isConnected;

        private byte? _blockCounter;

        public KW1281Dialog(IKwpCommon kwpCommon)
        {
            KwpCommon = kwpCommon;
            _isConnected = false;
            _blockCounter = null;
        }
    }

    /// <summary>
    /// Used for commands such as ActuatorTest which need to be kept alive with ACKs while waiting
    /// for user input.
    /// </summary>
    internal class KW1281KeepAlive : IDisposable
    {
        private readonly IKW1281Dialog _kw1281Dialog;
        private volatile bool _cancel = false;
        private Task? _keepAliveTask = null;

        public KW1281KeepAlive(IKW1281Dialog kw1281Dialog)
        {
            _kw1281Dialog = kw1281Dialog;
        }

        public ActuatorTestResponseBlock? ActuatorTest(byte value)
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
                Log.Write(".", LogDest.Console);
            }
        }
    }
}
