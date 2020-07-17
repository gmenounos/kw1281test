using BitFab.Kwp1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;

namespace BitFab.Kwp1281Test
{
    internal interface IKwp1281
    {
        void WakeUp(byte controllerAddress);

        void CustomReadRom(byte count, uint address);

        void CustomReadSoftwareVersion();

        void CustomReset();

        void CustomUnlockAdditionalCommands();

        void EndCommunication();

        void ReadEeprom(byte count, ushort address);

        void ReadIdent();

        void ReceiveBlocks();

        void SendBlock(List<byte> blockBytes);

        void SendCustom(List<byte> blockCustomBytes);
    }

    internal class Kwp1281 : IKwp1281
    {
        public Kwp1281(IInterface @interface)
        {
            _interface = @interface;
        }

        public void WakeUp(byte controllerAddress)
        {
            _interface.BitBang5Baud(controllerAddress);

            Console.WriteLine("Reading sync byte");
            var syncByte = _interface.ReadByte();
            if (syncByte != 0x55)
            {
                throw new InvalidOperationException(
                    $"Unexpected sync byte: Expected 0x55, Actual 0x{syncByte:X2}");
            }

            var keywordLsb = _interface.ReadByte();
            Console.WriteLine($"Keyword Lsb 0x{keywordLsb:X2}");

            var keywordMsb = ReadAndAckByte();
            Console.WriteLine($"Keyword Msb 0x{keywordMsb:X2}");

            if (keywordLsb == 0x01 && keywordMsb == 0x8A)
            {
                Console.WriteLine("Protocol is KW 1281 (8N1)");
            }
        }

        public void ReadIdent()
        {
            Console.WriteLine("Sending ReadIdent block");
            SendBlock(new List<byte> { (byte)BlockTitle.ReadIdent });
            ReceiveBlocks();
        }

        public void ReadEeprom(byte count, ushort address)
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

        public void CustomReadRom(byte count, uint address)
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

        public void CustomUnlockAdditionalCommands()
        {
            Console.WriteLine("Sending Custom \"Unlock Additional Commands\" block");
            SendCustom(new List<byte> { 0x80, 0x01, 0x02, 0x03, 0x04 });
        }

        public void CustomReadSoftwareVersion()
        {
            Console.WriteLine("Sending Custom \"Read Software Version\" block");
            SendCustom(new List<byte> { 0x84 });
        }

        public void CustomReset()
        {
            Console.WriteLine("Sending Custom Reset block");
            SendCustom(new List<byte> { 0x82 });
        }

        public void SendCustom(List<byte> blockCustomBytes)
        {
            blockCustomBytes.Insert(0, (byte)BlockTitle.Custom);
            SendBlock(blockCustomBytes);
            ReceiveBlocks();
        }

        public void EndCommunication()
        {
            Console.WriteLine("Sending EndCommunication block");
            SendBlock(new List<byte> { (byte)BlockTitle.End });
        }

        public void SendBlock(List<byte> blockBytes)
        {
            var blockLength = (byte)(blockBytes.Count + 2);

            blockBytes.Insert(0, _blockCounter++);
            blockBytes.Insert(0, blockLength);

            foreach (var b in blockBytes)
            {
                WriteByteAndReadAck(b);
            }

            _interface.WriteByte(0x03); // Block end, does not get ACK'd
        }

        public void ReceiveBlocks()
        {
            while (true)
            {
                var block = ReceiveBlock();
                if (block is AckBlock || block is NakBlock)
                {
                    break;
                }
                SendAckBlock();
            }
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
                    $"Received complement 0x{actualComplement:X2} but expected 0x{expectedComplement:X2}");
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
                    $"Received block end 0x{blockEnd:X2} but expected 0x03");
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
            if (blockCounter != _blockCounter)
            {
                throw new InvalidOperationException(
                    $"Received block counter 0x{blockCounter:X2} but expected 0x{_blockCounter:X2}");
            }
            _blockCounter++;
            return blockCounter;
        }

        private readonly IInterface _interface;
        private byte _blockCounter = 0x01;
    }
}
