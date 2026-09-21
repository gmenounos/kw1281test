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

    // The fault log is a table of four-byte slots. Byte 0 of a slot is an internal fault
    // number rather than a DTC - (index << 2) | status - and bytes 1-3 are a condition
    // stamp whose last byte is the value of the write marker that lives in the row after
    // the table. The real DTC numbers live in firmware, not in EEPROM.
    //
    // Filling past the end of the table erases that marker, and the module then rebuilds
    // the log with a zero stamp and invents records whose indices its own firmware cannot
    // map. Those all decode to 0xFFFF and surface as a single DTC 65535. Verified on a live
    // 1C0909605A with a dump after every step: 0x000-0x03F is clean and the module rebuilds
    // the log itself; 0x000-0x04F produces 65535; restoring 0x000-0x04F from a backup
    // clears it and returns the dump to baseline byte-for-byte. Erasing the marker alone,
    // with the log intact, does nothing - it is the combination that breaks.
    private const int FaultLogEndVW51 = 0x03F;
    private const int FaultLogEndVW61 = 0x02F;

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

    public int EepromSize => GetEepromSize(_version);

    internal static int GetEepromSize(ModuleVersion version) => version switch
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

    /// <summary>
    /// The areas each module version fills. Offsets are file offsets (before
    /// ResolveAbsoluteAddress) and both bounds are inclusive. Kept out of ClearCrashData so
    /// that the "never touch a protected track, never run past the end of EEPROM" invariant
    /// is covered by a unit test rather than only by a module on the bench.
    /// </summary>
    internal static (int Start, int End)[] GetClearRanges(ModuleVersion version) => version switch
    {
        // VW51: fault log plus crash record, 0x000-0x03F. Row 0x040-0x04F must NOT be
        // touched - see GetProtectedTracks.
        //
        // The VW51 crash record lives at 0x033-0x038, inside this same range, so the command
        // still does its job. The bench module used for testing has FF there, which is why
        // the hardware test never exercised it; two other 1C0909605A dumps carry
        // 0F B0 CF 05 4D 9F and 01 AE 08. This is also why the widely repeated advice to
        // "change four rows" is right for a real reason: four rows are the log plus the
        // crash record, and the fifth row is the write marker.
        ModuleVersion.VW51 => [(0x000, FaultLogEndVW51)],

        // VW61: fault log plus crash records.
        //
        // The crash record is 22 bytes at 0x1EA-0x1FF, duplicated at 0x200-0x215. Confirmed
        // by three dumps from modules with a real crash (two 1C0909605C and one 1C0909605F):
        // in all three the two halves are byte-identical. A second crash is appended after
        // it - in one dump the records reach 0x228, with a 0x01 byte at 0x24F - so the upper
        // bound is 0x24F, just below the identification block that starts at 0x250 on every
        // module.
        //
        // 0x160-0x169 is deliberately NOT in this list even though it looks like a crash
        // record (a value stored twice in a row plus two more bytes). It is a required
        // record present on every healthy module regardless of any crash, and it is a
        // per-part-number constant: all three 1C0909605C dumps here, with three different
        // serial numbers, hold an identical D9 F3 00 CC D9 F3 00 CC 05 30. A bench
        // 1C0909605C arrived with that record missing and reported a permanent 65535;
        // restoring those ten bytes cleared it for good and the module went from reporting
        // three DTCs to seven.
        //
        // 0x1AD-0x1C0 only appears on 1C0909605F; on both 605C dumps it is empty. Kept in
        // case it is that part number's layout.
        ModuleVersion.VW61 => [(0x000, FaultLogEndVW61), (0x1AD, 0x1C0), (0x1E9, 0x24F)],

        // 1C0909601: 0x151-0x1EB and 0x1EE-0x1EF are crash data. The first range used to be
        // 0x000-0x30F, which is the ENTIRE EEPROM of this version (0x310 bytes) - such a
        // fill wipes the software coding, the workshop code and the equipment block along
        // with everything else.
        //
        // The crash bounds are left as found, unlike VW61 above: for THIS part number the
        // author of the original write-up reports success with exactly these, the layout is
        // different (0x310 EEPROM, 93C66), and there are no dumps of it here. The
        // 0x1EC-0x1ED gap is the "two version bytes" people in that same discussion warn
        // about, and dumps confirm the warning literally: two VW51 dumps hold the ASCII
        // digits "01" (30 31) and "04" (30 34) there, so wiping them corrupts the version
        // string.
        _ => [(0x000, FaultLogEndVW61), (0x151, 0x1EB), (0x1EE, 0x1EF)]
    };

    /// <summary>
    /// Tracks that the write marker walks along. A track rather than a single byte because
    /// the marker IS a position: tomorrow it sits in the neighbouring cell. Observed on live
    /// modules - on VW51 the fault-log marker sat at 0x049 on one unit, 0x04B on another and
    /// 0x04E on a third; on VW61 it sat at 0x037 and moved to 0x038 across a power cycle,
    /// while a second marker walked 0x150 -> 0x151 -> 0x152 -> 0x153.
    ///
    /// The VW61 track runs to 0x169 rather than covering the marker cells alone, because the
    /// same span also holds the fault log's stamp counters at 0x156-0x15F (bytes 1-2 of every
    /// log stamp are read from 0x157-0x158) and the required record at 0x160-0x169. Filling
    /// from 0x156 on a live module zeroed every log stamp and brought 65535 straight back.
    ///
    /// No tracks are declared for 1C0909601: that part number's layout is not confirmed by
    /// any dump here, and inventing bounds would be worse than admitting the check cannot
    /// help for it.
    /// </summary>
    internal static (int Start, int End)[] GetProtectedTracks(ModuleVersion version) =>
        version switch
        {
            ModuleVersion.VW51 => [(0x040, 0x04F)],
            ModuleVersion.VW61 => [(0x030, 0x03F), (0x140, 0x169)],
            _ => []
        };

    internal static void ValidateClearRanges(
        (int Start, int End)[] ranges, ModuleVersion version)
    {
        var eepromSize = GetEepromSize(version);
        var tracks = GetProtectedTracks(version);

        foreach (var (start, end) in ranges)
        {
            foreach (var (trackStart, trackEnd) in tracks)
            {
                if (start <= trackEnd && end >= trackStart)
                {
                    throw new InvalidOperationException(
                        $"Range 0x{start:X3}-0x{end:X3} overlaps the protected track " +
                        $"0x{trackStart:X3}-0x{trackEnd:X3}; filling it makes the module " +
                        "report a permanent 65535 that only a dump restore clears.");
                }
            }

            if (end >= eepromSize)
            {
                throw new InvalidOperationException(
                    $"Range 0x{start:X3}-0x{end:X3} runs past the end of the " +
                    $"{eepromSize}-byte EEPROM of a {version} module.");
            }
        }
    }

    public void ClearCrashData(byte fillValue = 0xFF)
    {
        var ranges = GetClearRanges(_version);

        // Validate before opening the session: better to refuse up front than to stop
        // halfway through a fill and leave the module in a state its firmware never expects.
        ValidateClearRanges(ranges, _version);

        Log.WriteLine(
            $"VW51 airbag: ClearCrashData ({_version}) - " +
            string.Join(", ", ranges.Select(r => $"0x{r.Start:X3}-0x{r.End:X3}")) +
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
