using System;
using System.Linq;

namespace BitFab.KW1281Test.Airbag;

public sealed class Vw51AirbagModule : IAirbagModule
{
    // Base physical address from which logical address 0 is mapped in EEPROM.
    // Confirmed by capturing traffic from the OEM tool: VW51 reads from 0x0800,
    // VW61 reads from 0x0D00 (0x0D00 + EepromSizeVW61(0x300) = 0x1000).
    private const int LogicalBaseAddressVW51 = 0x0800;
    private const int LogicalBaseAddressVW61 = 0x0D00;
    private const byte MaxReadLength = 8;

    // EEPROM sizes by module version.
    // VW51 and VW61 are confirmed by a real dump from the module; 1C0909601 is an estimate
    // based on the maximum address in ClearCrashData (0x30F), not confirmed by a live dump.
    private const int EepromSizeVW51 = 512;  // 0x200
    private const int EepromSizeVW61 = 768;  // 0x300
    private const int EepromSizeVW_1C0909601 = 784;  // 0x310 (unconfirmed)

    private readonly IKW1281Dialog _kwp1281;
    private readonly string _ecuText;

    internal Vw51AirbagModule(IKW1281Dialog kwp1281, string ecuText)
    {
        _kwp1281 = kwp1281;
        _ecuText = ecuText ?? string.Empty;
    }

    /// <summary>Module version: VW51, VW61, or the specific part number 1C0909601.</summary>
    public enum ModuleVersion { VW51, VW61, VW_1C0909601 }

    private ModuleVersion _version = ModuleVersion.VW51;

    public int EepromSize => _version switch
    {
        ModuleVersion.VW61 => EepromSizeVW61,
        ModuleVersion.VW_1C0909601 => EepromSizeVW_1C0909601,
        _ => EepromSizeVW51
    };

    public bool IsSupportedIdent(string ecuIdent, out string reason)
    {
        var text = string.IsNullOrWhiteSpace(ecuIdent) ? _ecuText : ecuIdent;

        // First check the specific part number — it takes priority.
        if (text.Contains("1C0909601", StringComparison.OrdinalIgnoreCase))
        {
            _version = ModuleVersion.VW_1C0909601;
            reason = string.Empty;
            return true;
        }

        bool isAirbag = text.Contains("AIRBAG", StringComparison.OrdinalIgnoreCase);

        if (isAirbag && text.Contains("VW51", StringComparison.OrdinalIgnoreCase))
        {
            _version = ModuleVersion.VW51;
            reason = string.Empty;
            return true;
        }

        if (isAirbag && text.Contains("VW61", StringComparison.OrdinalIgnoreCase))
        {
            _version = ModuleVersion.VW61;
            reason = string.Empty;
            return true;
        }

        reason = "Not a supported VW Airbag module (expected VW51, VW61, or part# 1C0909601)";
        return false;
    }

    public void PrepareSession()
    {
        Log.WriteLine("VW51 airbag: Login 0x4653");
        _kwp1281.Login(0x4653, 0);

        Log.WriteLine("VW51 airbag: send unlock block 1A 01 50 4D 00 (no response expected)");
        _kwp1281.SendBlock(
        [
            (byte)BlockTitle.WriteEeprom,
            0x01,
            0x50,
            0x4D,
            0x00
        ]);
    }

    public byte[] DumpEeprom(int startAddress, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length == 0)
        {
            return [];
        }

        try
        {
            PrepareSession();
            EnterRawReadMode();

            int absoluteAddress = ResolveAbsoluteAddress(startAddress);

            var result = new byte[length];
            int written = 0;
            int currentAddress = absoluteAddress;

            while (written < length)
            {
                byte readLength = (byte)Math.Min(MaxReadLength, length - written);
                var chunk = ReadRawChunk(currentAddress, readLength);

                Buffer.BlockCopy(chunk, 0, result, written, chunk.Length);

                written += chunk.Length;
                currentAddress += chunk.Length;
            }

            // End the raw session — without this the ECU does not return to normal mode and requires a reboot.
            Log.WriteLine("VW51 airbag: sending end-of-session frame 02 77 75");
            CommitWrite();

            return result;
        }
        catch
        {
            _kwp1281.SetDisconnected();
            throw;
        }
        finally
        {
            _kwp1281.SetDisconnected();
        }
    }

    public void ClearCrashData(byte fillValue = 0xFF)
    {
        // Ranges are file offsets (before ResolveAbsoluteAddress), inclusive.
        var ranges = _version switch
        {
            ModuleVersion.VW51 =>
                // VW51: addresses 0x000-0x04F (80 bytes)
                [(0x000, 0x04F)],
            ModuleVersion.VW61 =>
                // VW61: two ranges
                [(0x000, 0x030), (0x151, 0x1EF)],
            // 1C0909601: 0x000-0x30F — fault area, 0x151-0x1EB and 0x1EE-0x1EF — crash data
            _ => new[] { (0x000, 0x30F), (0x151, 0x1EB), (0x1EE, 0x1EF) }
        };

        Log.WriteLine(
            $"VW51 airbag: ClearCrashData ({_version}) — " +
            string.Join(", ", ranges.Select(r => $"0x{r.Item1:X3}-0x{r.Item2:X3}")) +
            $", value 0x{fillValue:X2}");

        // All ranges are written within a single session (one Login/raw-mode/commit) —
        // a repeated Login after SetDisconnected() fails because the block counter resets
        // and is not restored without a full new connection.
        try
        {
            PrepareSession();
            EnterRawReadMode();

            foreach (var (startOffset, endOffset) in ranges)
            {
                int length = endOffset - startOffset + 1;
                var data = new byte[length];
                for (int i = 0; i < length; i++)
                    data[i] = fillValue;

                int absoluteAddress = ResolveAbsoluteAddress(startOffset);
                Log.WriteLine(
                    $"  FillRange 0x{startOffset:X3}-0x{endOffset:X3} ({length} bytes) = 0x{fillValue:X2}");
                WriteBytesAtAbsoluteAddress(absoluteAddress, data);
            }

            Log.WriteLine("VW51 airbag: sending commit frame 02 77 75");
            CommitWrite();

            Log.WriteLine("VW51 airbag: ClearCrashData complete.");
        }
        catch
        {
            _kwp1281.SetDisconnected();
            throw;
        }
        finally
        {
            _kwp1281.SetDisconnected();
        }
    }

    public void LoadEeprom(int startAddress, byte[] data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startAddress);

        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            throw new ArgumentException("EEPROM write buffer is empty.", nameof(data));
        }

        int absoluteAddress = ResolveAbsoluteAddress(startAddress);

        Log.WriteLine(
            $"VW51 airbag: LoadEeprom start=0x{startAddress:X4}, " +
            $"absolute=0x{absoluteAddress:X4}, length={data.Length}");

        try
        {
            PrepareSession();
            EnterRawReadMode(); // read and write mode are entered the same way through 0x70

            WriteBytesAtAbsoluteAddress(absoluteAddress, data);

            Log.WriteLine("VW51 airbag: sending commit frame 02 77 75");
            CommitWrite();

            Log.WriteLine("VW51 airbag: LoadEeprom complete.");
        }
        catch
        {
            _kwp1281.SetDisconnected();
            throw;
        }
        finally
        {
            _kwp1281.SetDisconnected();
        }
    }

    /// <summary>
    /// Writes bytes at an absolute address. Must be called while an active raw session is
    /// already open (after PrepareSession/EnterRawReadMode), without commit/disconnect —
    /// this allows writing several ranges in one session (see ClearCrashData).
    /// </summary>
    private void WriteBytesAtAbsoluteAddress(int absoluteAddress, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int byteAbsoluteAddress = absoluteAddress + i;
            byte page = (byte)(byteAbsoluteAddress >> 8);
            byte index = (byte)(byteAbsoluteAddress & 0xFF);
            byte value = data[i];

            Log.WriteLine(
                $"VW51 airbag: write byte [{i + 1}/{data.Length}] " +
                $"page=0x{page:X2} index=0x{index:X2} (abs=0x{byteAbsoluteAddress:X4}) value=0x{value:X2}");

            WriteRawByte(page, index, value);
        }
    }

    private void CommitWrite()
    {
        // Finalizing frame: 02 77 75
        // Without it, the ECU does not persist data from the buffer to EEPROM.
        var request = new byte[] { 0x02, 0x77, 0x00 };
        request[2] = ComputeXor(request, 2);
        SendRawBytes(request);
    }

    private void WriteRawByte(byte page, byte index, byte value)
    {
        // Frame format: 0D 73 <page> <index> 01 <value> FF FF FF FF FF FF FF <xor>
        // index = absoluteAddress & 0xFF
        // 13 bytes before XOR, XOR is XOR of all 13 preceding bytes.
        var request = new byte[]
        {
            0x0D, 0x73, page,
            index,
            0x01,
            value,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00  // placeholder for XOR
        };
        request[^1] = ComputeXor(request, request.Length - 1);

        SendRawBytes(request);

        // Read and validate the acknowledgement frame
        var response = ReadRawFrame();
        ValidateRawWriteResponse(response, page, index, value);
    }

    private static void ValidateRawWriteResponse(byte[] response, byte page, byte index, byte value)
    {
        // Minimum sanity check: response must not be empty and XOR must match.
        if (response.Length < 2)
        {
            throw new InvalidOperationException(
                $"VW51 airbag write ack too short ({response.Length} bytes) " +
                $"for page=0x{page:X2} index=0x{index:X2} value=0x{value:X2}.");
        }

        byte expectedChecksum = ComputeXor(response, response.Length - 1);
        byte actualChecksum = response[^1];
        if (expectedChecksum != actualChecksum)
        {
            throw new InvalidOperationException(
                $"VW51 airbag write ack checksum mismatch for page=0x{page:X2} index=0x{index:X2} value=0x{value:X2}. " +
                $"Expected 0x{expectedChecksum:X2}, got 0x{actualChecksum:X2}.");
        }
    }

    private void EnterRawReadMode()
    {
        var kwpCommon = _kwp1281.KwpCommon;

        kwpCommon.Interface.ClearReceiveBuffer();

        Log.WriteLine("VW51 airbag: enter raw read mode (0x70)");
        kwpCommon.WriteByte(0x70);

        // Unlock response is a plain length-prefixed frame (like ReadRawFrame),
        // not a stream terminated by a fixed byte. The last payload byte must be
        // echoed back verbatim (not complemented) to acknowledge it; that byte
        // varies per module (e.g. VW61 ends in 0x6B, not the 0x2D some VW51 units use).
        var response = ReadRawFrame();
        Log.WriteLine(
            $"VW51 airbag: raw unlock response ({response.Length} bytes): " +
            BitConverter.ToString(response).Replace("-", " "));

        byte ackByte = response[^1];
        Log.WriteLine($"VW51 airbag: acknowledge raw unlock with 0x{ackByte:X2}");
        kwpCommon.Interface.WriteByteRaw(ackByte);

        // WriteByteRaw doesn't read/discard the echo, but the K-line is a single
        // wire so our own byte loops back before the ECU's real confirmation arrives.
        byte loopback = kwpCommon.Interface.ReadByte();
        Log.WriteLine($"VW51 airbag: ack loopback = 0x{loopback:X2}");

        byte ready = kwpCommon.Interface.ReadByte();
        if (ready != 0x71)
        {
            throw new InvalidOperationException(
                $"VW51 airbag did not enter raw read mode. Expected 0x71, got 0x{ready:X2}.");
        }

        Log.WriteLine("VW51 airbag: raw read mode confirmed (0x71)");
    }

    private byte[] ReadRawChunk(int absoluteAddress, byte count)
    {
        byte addressHi = (byte)(absoluteAddress >> 8);
        byte addressLo = (byte)(absoluteAddress & 0xFF);

        var request = new byte[] { 0x05, 0x72, addressHi, addressLo, count, 0x00 };
        request[5] = ComputeXor(request, request.Length - 1);

        SendRawBytes(request);

        var response = ReadRawFrame();
        ValidateRawReadResponse(response, addressHi, addressLo, count);

        var data = new byte[count];
        Buffer.BlockCopy(response, 5, data, 0, count);
        return data;
    }

    private byte[] ReadRawFrame()
    {
        var kwpCommon = _kwp1281.KwpCommon;

        int payloadLength = kwpCommon.ReadByte();
        var frame = new byte[payloadLength + 1];
        frame[0] = (byte)payloadLength;

        for (int i = 0; i < payloadLength; i++)
        {
            frame[i + 1] = kwpCommon.ReadByte();
        }

        return frame;
    }

    private void SendRawBytes(byte[] bytes)
    {
        var kwpCommon = _kwp1281.KwpCommon;

        foreach (byte b in bytes)
        {
            kwpCommon.WriteByte(b);
        }
    }

    private static void ValidateRawReadResponse(
        byte[] response,
        byte addressHi,
        byte addressLo,
        byte count)
    {
        if (response.Length != count + 6)
        {
            throw new InvalidOperationException(
                $"VW51 airbag raw response has unexpected size. " +
                $"Expected {count + 6}, got {response.Length}.");
        }

        if (response[1] != 0x8D)
        {
            throw new InvalidOperationException(
                $"VW51 airbag raw response title mismatch. Expected 0x8D, got 0x{response[1]:X2}.");
        }

        if (response[2] != addressHi || response[3] != addressLo)
        {
            throw new InvalidOperationException(
                $"VW51 airbag raw response address mismatch. " +
                $"Expected 0x{addressHi:X2}{addressLo:X2}, got 0x{response[2]:X2}{response[3]:X2}.");
        }

        if (response[4] != count)
        {
            throw new InvalidOperationException(
                $"VW51 airbag raw response length mismatch. " +
                $"Expected 0x{count:X2}, got 0x{response[4]:X2}.");
        }

        byte expectedChecksum = ComputeXor(response, response.Length - 1);
        byte actualChecksum = response[^1];
        if (expectedChecksum != actualChecksum)
        {
            throw new InvalidOperationException(
                $"VW51 airbag raw response checksum mismatch. " +
                $"Expected 0x{expectedChecksum:X2}, got 0x{actualChecksum:X2}.");
        }
    }

    private static byte ComputeXor(byte[] bytes, int count)
    {
        byte value = 0;
        for (int i = 0; i < count; i++)
        {
            value ^= bytes[i];
        }

        return value;
    }

    private int ResolveAbsoluteAddress(int address)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(address);

        int logicalBaseAddress = _version == ModuleVersion.VW61
            ? LogicalBaseAddressVW61
            : LogicalBaseAddressVW51;

        // So that the ReadEeprom 0 command reads from the start of the module, as in the OEM tool.
        return address < logicalBaseAddress
            ? logicalBaseAddress + address
            : address;
    }
}
