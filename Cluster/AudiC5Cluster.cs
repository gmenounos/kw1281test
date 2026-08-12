using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using BitFab.KW1281Test.Blocks;

namespace BitFab.KW1281Test.Cluster;

internal class AudiC5Cluster : ICluster
{
    public void UnlockForEepromReadWrite()
    {
        Log.WriteLine("Sending custom \"Unlock Additional Commands\" block");
        _kw1281Dialog.SendBlock([0x1B, 0x80, 0x01, 0x02, 0x03, 0x04]);
        var unlockBlock = _kw1281Dialog.ReceiveBlock();
        if (unlockBlock is not AckBlock)
        {
            // Real VVDI2 traces show the cluster NAKing this same challenge yet the tool
            // proceeds straight to the password login anyway, so don't treat this as fatal.
            Log.WriteLine($"Warning: Expected ACK block but received: {unlockBlock}");
        }

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
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(length);
        ArgumentNullException.ThrowIfNull(dumpFileName);

        Login();

        Log.WriteLine($"Dumping EEPROM to {dumpFileName}");
        DumpEeprom(address.Value, length.Value, maxReadLength: 0x08, dumpFileName);

        _kw1281Dialog.SetDisconnected();

        return dumpFileName;
    }

    /// <summary>
    /// Writes the contents of <paramref name="filename"/> to EEPROM starting at
    /// <paramref name="address"/>. Confirmed against real unlocking-tool write traffic (both a
    /// full 2048-byte image write and a small 2-byte targeted write) — same $77 opcode/framing
    /// as $72 ReadEeprom, just with the data to write appended to the request instead of the
    /// response.
    /// </summary>
    public void WriteEeprom(uint? address, string? filename)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(filename);

        var data = File.ReadAllBytes(filename);

        Login();

        Log.WriteLine($"Writing EEPROM from {filename}");
        WriteEeprom((ushort)address.Value, data, maxWriteLength: 0x08);

        _kw1281Dialog.SetDisconnected();

        Log.WriteLine();
        Log.WriteLine("Power-cycle the cluster (ignition off/on) for the new EEPROM data " +
            "(mileage/IMMO ID/VIN) to take effect.");
    }

    private const int MaxAccessLevel = 7;

    private void Login()
    {
        SendHello();
        LoginWithPassword();

        var accessLevel = GetAccessLevel();
        if (accessLevel is not null && accessLevel != MaxAccessLevel)
        {
            UnlockViaSeedKey();
        }
    }

    private void SendHello()
    {
        WriteBlock([Constants.Hello]);

        var blockBytes = ReadBlock();
        Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");
        if (BlockTitle(blockBytes) != Constants.Hello)
        {
            Log.WriteLine($"Warning: Expected block of type ${Constants.Hello:X2}");
        }
    }

    private void LoginWithPassword()
    {
        string[] passwords =
        [
            "19xDR8xS",
            "vdokombi",
            "w10kombi",
            "w10serie",
        ];

        foreach (var password in passwords)
        {
            Log.WriteLine("Sending login request");

            var blockBytes = new List<byte> { Constants.Login, 0x9D };
            blockBytes.AddRange(Encoding.ASCII.GetBytes(password));
            WriteBlock(blockBytes);

            blockBytes = ReadBlock();
            Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");

            if (BlockTitle(blockBytes) == Constants.Ack)
            {
                Log.WriteLine("Succeeded");
                return;
            }

            Log.WriteLine($"Warning: Expected block of type ${Constants.Ack:X2}");
        }

        throw new InvalidOperationException("Unable to login to cluster");
    }

    private int? GetAccessLevel()
    {
        Log.WriteLine("Sending \"Get Access Level\" request");
        WriteBlock([Constants.Login, 0x96, 0x04]);
        var blockBytes = ReadBlock();
        Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");

        if (BlockTitle(blockBytes) == Constants.Login && blockBytes.Count > 3)
        {
            int accessLevel = blockBytes[3];
            Log.WriteLine($"Access level is {accessLevel}.");
            return accessLevel;
        }

        Log.WriteLine("Warning: Access level is unknown.");
        return null;
    }

    private void UnlockViaSeedKey()
    {
        Log.WriteLine("Sending \"Seed Request\" request");
        WriteBlock([Constants.Login, 0x96, 0x01]);
        var blockBytes = ReadBlock();
        Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");

        if (BlockTitle(blockBytes) != Constants.Login || blockBytes.Count != 14)
        {
            Log.WriteLine("Warning: Unexpected response to seed request. EEPROM access will likely fail.");
            return;
        }

        var seed = blockBytes.Skip(3).Take(10).ToArray();
        var key = VdoKeyFinder.FindKey(seed, MaxAccessLevel);

        Log.WriteLine("Sending \"Key Response\" request");
        var keyBlockBytes = new List<byte> { Constants.Login, 0x96, 0x02 };
        keyBlockBytes.AddRange(key);
        WriteBlock(keyBlockBytes);

        blockBytes = ReadBlock();
        Log.WriteLine($"Received block:{Utils.Dump(blockBytes)}");
        if (BlockTitle(blockBytes) != Constants.Ack)
        {
            Log.WriteLine("Warning: Key was not accepted. EEPROM access will likely fail.");
        }

        // An ACK above only confirms the block was well-formed, not that the key was actually
        // correct, so re-query to see whether access level genuinely changed.
        //
        // Note: this key-computation algorithm has been verified correct against multiple real
        // (seed, key) pairs captured from a genuine unlocking tool talking to a cluster stuck
        // below max access level, but a kw1281test-submitted key has not yet been observed to
        // actually raise access level on real hardware - only the genuine tool's own
        // submissions have. If reads still fail after this despite a correct key, the cluster
        // is likely gated by some additional precondition outside this exchange.
        GetAccessLevel();
    }

    private void DumpEeprom(
        uint startAddr, uint length, byte maxReadLength, string fileName)
    {
        using var fs = File.Create(fileName, bufferSize: maxReadLength, FileOptions.WriteThrough);

        var succeeded = true;
        for (var addr = startAddr; addr < startAddr + length; addr += maxReadLength)
        {
            var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
            var blockBytes = ReadEepromByAddress(addr, readLength);

            if (blockBytes.Count != readLength)
            {
                succeeded = false;
                blockBytes.AddRange(
                    Enumerable.Repeat((byte)0, readLength - blockBytes.Count));
            }

            fs.Write(blockBytes.ToArray(), offset: 0, blockBytes.Count);
            fs.Flush();
        }

        if (!succeeded)
        {
            Log.WriteLine();
            Log.WriteLine("**********************************************************************");
            Log.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
            Log.WriteLine("**********************************************************************");
            Log.WriteLine();
        }
    }

    private void WriteEeprom(ushort startAddr, byte[] data, byte maxWriteLength)
    {
        var succeeded = true;
        for (var offset = 0; offset < data.Length; offset += maxWriteLength)
        {
            var chunkLen = (byte)Math.Min(data.Length - offset, maxWriteLength);
            var addr = (ushort)(startAddr + offset);

            List<byte> blockBytes =
            [
                Constants.WriteEeprom,
                chunkLen,
                (byte)(addr >> 8),
                (byte)(addr & 0xFF),
            ];
            blockBytes.AddRange(data.Skip(offset).Take(chunkLen));
            WriteBlock(blockBytes);

            var response = ReadBlock();
            Log.WriteLine($"Received block:{Utils.Dump(response)}");
            if (BlockTitle(response) != Constants.Ack)
            {
                succeeded = false;
                Log.WriteLine(
                    $"Warning: Expected block of type ${Constants.Ack:X2} but got ${BlockTitle(response):X2} (address ${addr:X4})");
            }
        }

        if (!succeeded)
        {
            Log.WriteLine();
            Log.WriteLine("**********************************************************************");
            Log.WriteLine("*** Warning: Some bytes may not have been written (see warnings above) ***");
            Log.WriteLine("**********************************************************************");
            Log.WriteLine();
        }
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
            Log.WriteLine(
                $"Warning: Expected block of type ${Constants.ReadEeprom:X2} but got ${BlockTitle(blockBytes):X2} (address ${addr:X4})");
            return [];
        }

        var expectedLength = readLength + 4;
        var actualLength = blockBytes.Count;
        if (blockBytes.Count != expectedLength)
        {
            Log.WriteLine(
        $"Warning: Expected block length ${expectedLength:X2} but length is ${actualLength:X2}");
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
        var sentBytes = new List<byte>(bodyBytes.Count + 3);

        WriteBlockByte(Constants.StartOfBlock);
        WriteBlockByte((byte)(bodyBytes.Count + 3)); // Block length
        foreach (var bodyByte in bodyBytes)
        {
            WriteBlockByte(bodyByte);
        }

        _kw1281Dialog.KwpCommon.WriteByte(checksum);
        sentBytes.Add(checksum);
        Log.WriteLine($"Sending block:{Utils.Dump(sentBytes)}");
        return;

        void WriteBlockByte(byte b)
        {
            _kw1281Dialog.KwpCommon.WriteByte(b);
            checksum ^= b;
            sentBytes.Add(b);
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
                throw new InvalidOperationException($"Expected $D1 header byte but got ${header:X2}");
            }

            if (checksum != 0x00)
            {
                throw new InvalidOperationException($"Expected $00 block checksum but got ${checksum:X2}");
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
        public const byte WriteEeprom = 0x77;
    }

    private readonly IKW1281Dialog _kw1281Dialog;

    public AudiC5Cluster(IKW1281Dialog kw1281Dialog)
    {
        _kw1281Dialog = kw1281Dialog;
    }
}
