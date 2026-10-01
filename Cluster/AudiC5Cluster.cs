using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace BitFab.KW1281Test.Cluster;

internal class AudiC5Cluster : ICluster
{
    public void UnlockForEepromReadWrite()
    {
        string[] passwords =
        [
            "loginas9",
            "n7KB2Qat",
        ];

        var succeeded = false;
        foreach (var password in passwords)
        {
            Log.WriteLine("Sending custom login block");
            var blockBytes = new List<byte>([0x1B, 0x80]); // Custom 0x80
            blockBytes.AddRange(Encoding.ASCII.GetBytes(password));
            _kw1281Dialog.SendBlock(blockBytes);

            var block = _kw1281Dialog.ReceiveBlock();
            if (block is NakBlock)
            {
                continue;
            }
            else if (block is not AckBlock)
            {
                throw new InvalidOperationException(
                    $"Expected ACK block but received: {block}");
            }

            succeeded = true;
        }

        if (!succeeded)
        {
            throw new InvalidOperationException("Unable to login to cluster");
        }

        var @interface = _kw1281Dialog.KwpCommon.Interface;
        @interface.SetBaudRate(19200);
        @interface.SetParity(Parity.Even);
        @interface.ClearReceiveBuffer();

        Thread.Sleep(TimeSpan.FromSeconds(2));
    }

    public string DumpEeprom(uint? address, uint? length, string? dumpFileName)
    {
        address ??= 0;
        length ??= 0x800;
        dumpFileName ??= $"AudiC5_0x{address:X4}_eeprom.bin";

        WriteBlock([Constants.Hello]);

        var blockBytes = ReadBlock();
        Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");
        if (BlockTitle(blockBytes) != Constants.Hello)
        {
            Log.WriteLine($"Warning: Expected block of type 0x{Constants.Hello:X2}");
        }

        string[] passwords =
        [
            "19xDR8xS",
            "vdokombi",
            "w10kombi",
            "w10serie",
        ];

        var succeeded = false;
        foreach (var password in passwords)
        {
            Log.WriteLine("Sending login request");

            blockBytes = [Constants.Login, 0x9D];
            blockBytes.AddRange(Encoding.ASCII.GetBytes(password));
            WriteBlock(blockBytes);

            blockBytes = ReadBlock();
            Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");

            if (BlockTitle(blockBytes) == Constants.Ack)
            {
                succeeded = true;
                break;
            }
            else
            {
                Log.WriteLine($"Warning: Expected block of type 0x{Constants.Ack:X2}");
            }
        }

        if (!succeeded)
        {
            throw new InvalidOperationException("Unable to login to cluster");
        }
        else
        {
            Log.WriteLine("Succeeded");
        }

        Log.WriteLine($"Dumping EEPROM to {dumpFileName}");
        Utils.WriteDump(
            (addr, len) => ReadEepromByAddress(addr, len),
            (uint)address, (uint)length, maxReadLength: 0x10, dumpFileName);

        _kw1281Dialog.SetDisconnected();

        return dumpFileName;
    }

    private List<byte> ReadEepromByAddress(uint addr, byte readLength)
    {
        List<byte> blockBytes =
        [
            Constants.ReadEeprom,
            readLength,
            (byte)(addr >> 8),
            (byte)(addr & 0xFF)
        ];
        WriteBlock(blockBytes);

        blockBytes = ReadBlock();
        Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");

        if (BlockTitle(blockBytes) != Constants.ReadEeprom)
        {
            throw new InvalidOperationException($"Expected block of type 0x{Constants.ReadEeprom:X2}");
        }

        var expectedLength = readLength + 4;
        var actualLength = blockBytes.Count;
        if (blockBytes.Count != expectedLength)
        {
            Log.WriteLine(
                $"Warning: Expected block length 0x{expectedLength:X2} but length is 0x{actualLength:X2}");
        }

        return blockBytes.Skip(3).Take(actualLength - 4).ToList();
    }

    private static byte BlockTitle(IReadOnlyList<byte> blockBytes)
    {
        return blockBytes[2];
    }

    private void WriteBlock(IReadOnlyCollection<byte> bodyBytes)
    {
        byte checksum = 0x00;

        WriteBlockByte(Constants.StartOfBlock);
        WriteBlockByte((byte)(bodyBytes.Count + 3)); // Block length
        foreach (var bodyByte in bodyBytes)
        {
            WriteBlockByte(bodyByte);
        }

        _kw1281Dialog.KwpCommon.WriteByte(checksum);
        return;

        void WriteBlockByte(byte b)
        {
            _kw1281Dialog.KwpCommon.WriteByte(b);
            checksum ^= b;
        }
    }

    private List<byte> ReadBlock()
    {
        var blockBytes = new List<byte>();
        byte checksum = 0x00;

        try
        {
            var header = ReadByte();
            var blockSize = ReadByte();
            for (var i = 0; i < blockSize - 2; i++)
            {
                ReadByte();
            }

            if (header != Constants.StartOfBlock)
            {
                throw new InvalidOperationException($"Expected 0xD1 header byte but got 0x{header:X2}");
            }

            if (checksum != 0x00)
            {
                throw new InvalidOperationException($"Expected 0x00 block checksum but got 0x{checksum:X2}");
            }
        }
        catch (Exception e)
        {
            Log.WriteLine($"Error reading block: {e}");
            Log.WriteLine($"Partial block: {Utils.Dump(blockBytes)}");
            throw;
        }

        return blockBytes;

        byte ReadByte()
        {
            var b = _kw1281Dialog.KwpCommon.ReadByte();
            checksum ^= b;
            blockBytes.Add(b);
            return b;
        }
    }

    private static class Constants
    {
        public const byte StartOfBlock = 0xD1;
        public const byte Ack = 0x06;
        public const byte Nak = 0x15;
        public const byte Hello = 0x49;
        public const byte Login = 0x53;
        public const byte ReadEeprom = 0x72;
    }

    private readonly IKW1281Dialog _kw1281Dialog;

    public AudiC5Cluster(IKW1281Dialog kw1281Dialog)
    {
        _kw1281Dialog = kw1281Dialog;
    }
}