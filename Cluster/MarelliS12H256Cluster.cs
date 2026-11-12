using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace BitFab.KW1281Test.Cluster;

/// <summary>
/// MC9S12H256 Marelli cluster (2K78X-0805 family).
///
/// These clusters do not have the DG128A 0x6C RAM-code loader. The KWP1281 0x03 read and
/// 0x0C write services are the memory interface, bounded to $0000-$37EB and limited to a
/// short count per request. The 0x03 deny-list protects the calibration/shadow EEPROM block,
/// so a full dump uses a RAM routine uploaded through 0x0C.
///
/// The routine is a copy service placed at $16C3 inside the known-free RAM run and
/// installed by changing only the high byte of the CAN0 TX RAM stub at $2F60 (the low
/// byte already matches $55C3), so the patch is one atomic write. On each CAN0 transmit
/// interrupt it copies one chunk of flash or EEPROM into a free RAM buffer, increments a
/// sequence byte, calls the original CAN0 TX handler and returns. The tester writes the
/// request and reads the result through the 0x03/0x0C services, so no SCI transmitter is
/// used and no CAN frame is needed. The stub is restored when the dump finishes and the
/// cluster keeps running.
///
/// This requires a bench/owned cluster. It writes only RAM and volatile registers; flash
/// and EEPROM are never written.
/// </summary>
internal sealed class MarelliS12H256Cluster
{
    private const ushort EepromBase = 0x3000;
    private const int EepromSize = 0x1000;          // 4 KB
    private const int EepromLowerSize = 0x800;      // $3000-$37FF
    private const int EepromUpperOffset = 0xC00;    // $3C00-$3FFF in the 4 KB image
    private const int EepromUpperSize = 0x400;
    private const int EepromOverlayOffset = 0x800;  // $3800-$3BFF is the register block
    private const int EepromOverlaySize = 0x400;
    private const ushort EepromServiceEnd = 0x37EB; // 0x03/0x0C mapping bound (inclusive)

    private const int PageSize = 0x4000;            // 16 KB
    private const int FlashSize = 16 * PageSize;    // 256 KB
    private const ushort Can0TxStub = 0x2F60;

    // $15E2-$16F6 is the only >=128-byte zero run in BeetleRam.bin between the firmware
    // globals (which end at $151F) and the stack at $2EFE. The boot path at 34/8865 calls
    // 36/976A, which loads the 59-byte login/coding record into $15E2-$15FC, so everything
    // the tool plants must stay inside this run. $15C3 (just before $15D2) looked free but
    // is KWP scratch and crashed the cluster, as did $20C3, which is live firmware RAM.
    //
    // The service itself sits at $16C3, where the low byte matches the original $55C3
    // CAN0 TX stub: patching the stub therefore changes only its high byte, which is a
    // single atomic write, and no separate trampoline exists to be overwritten.
    private const ushort ServiceAddress = 0x16C3;  // 49 bytes, ends $16F3
    private const ushort ServiceRequest = 0x162F;  // srcHi, srcLo, page, len, cmd, seq
    private const ushort ServiceSeq = 0x1634;
    private const ushort SavedStubHigh = 0x1635;    // original CAN0 TX stub page

    private const ushort ServiceBuffer = 0x1638;   // 128 bytes, ends $16B7
    private const int ServiceChunkSize = 128;
    private const byte ReadChunkSize = 0x2C;        // 0x03 handler limit
    private const int WriteChunkSize = 0x20;        // conservative 0x0C limit

    /// <summary>
    /// Reads the EEPROM through the 0x03 service only. The deny-list block
    /// ($31D9-$343B), the top 20 bytes ($37EC-$37FF) and $3C00-$3FFF are left as
    /// 0xFF. This is safe and needs no code execution or CAN trigger.
    /// </summary>
    public string DumpEeprom(string? dumpFileName)
    {
        dumpFileName ??= "s12h256_eeprom.bin";

        Log.WriteLine($"Cluster: {_ecuInfo}");
        var image = ReadEepromViaService();
        File.WriteAllBytes(dumpFileName, image);
        Log.WriteLine($"Saved EEPROM to {dumpFileName} ({image.Length} bytes)");
        Log.WriteLine("$3800-$3BFF is the register-block overlay; $3C00-$3FFF is above the 0x03 bound.");
        Log.WriteLine("Both are filled with 0xFF. Use DumpS12H256 for the full EEPROM.");
        return dumpFileName;
    }

    /// <summary>
    /// Dumps RAM through the 0x01 service. The 0x01 and 0x03 handlers share the same
    /// $0000-$37EB window, so all 12 KB of RAM ($0000-$2FFF) is reachable. The protected
    /// RAM ranges are retried byte by byte and left as 0xFF if denied. This bypasses the
    /// generic Tester.ReadRam path, which runs the VDO unlock before reading.
    /// </summary>
    public string DumpRam(uint start, uint length, string? dumpFileName)
    {
        if (start >= 0x3000 || length == 0 || length > 0x3000 - start)
        {
            Log.WriteLine("RAM dump must lie inside $0000-$2FFF on this cluster.");
            return string.Empty;
        }

        dumpFileName ??= $"s12h256_ram_0x{start:X4}.bin";
        var image = Enumerable.Repeat((byte)0xFF, (int)length).ToArray();
        int denied = 0;

        for (int offset = 0; offset < image.Length;)
        {
            var address = (ushort)(start + (uint)offset);
            int chunk = Math.Min((int)ReadChunkSize, image.Length - offset);
            var bytes = _kwp1281.ReadRam(address, (byte)chunk);
            if (bytes is { Count: > 0 })
            {
                var copy = bytes.ToArray();
                Array.Copy(copy, 0, image, offset, Math.Min(copy.Length, chunk));
                offset += chunk;
                continue;
            }

            if (chunk > 1)
            {
                for (int i = 0; i < chunk; i++)
                {
                    var one = _kwp1281.ReadRam((ushort)(address + i), 1);
                    if (one is { Count: > 0 })
                    {
                        image[offset + i] = one[0];
                    }
                    else
                    {
                        denied++;
                    }
                }

                offset += chunk;
            }
            else
            {
                denied++;
                offset++;
            }
        }

        if (denied > 0)
        {
            Log.WriteLine($"0x01 denied {denied} RAM byte(s) (protected ranges); left as 0xFF.");
        }

        File.WriteAllBytes(dumpFileName, image);
        Log.WriteLine($"Saved RAM to {dumpFileName} ({image.Length} bytes)");
        return dumpFileName;
    }

    /// <summary>
    /// Dumps flash and/or EEPROM through the RAM copy service instead of the SCI.
    /// The service runs on the CAN0 TX interrupt, copies one chunk into $1620,
    /// increments a sequence byte and returns to the firmware. The tester then reads
    /// the chunk with the working KWP 0x03 service. No SCI transmitter is used.
    /// With only one part selected, <paramref name="fileName"/> is that part's output
    /// file; with both, the EEPROM is written next to the flash as
    /// &lt;name&gt;_eeprom.bin.
    /// </summary>
    public string DumpFlashAndEeprom(string? fileName,
        bool dumpFlash = true, bool dumpEeprom = true)
    {
        if (!dumpFlash && !dumpEeprom)
        {
            Log.WriteLine("Nothing selected: pass --flash, --eeprom, or neither for both.");
            return string.Empty;
        }

        string flashFile = string.Empty;
        string eepromFile = string.Empty;
        if (dumpFlash && dumpEeprom)
        {
            flashFile = fileName ?? "s12h256_flash.bin";
            eepromFile = Path.Combine(
                Path.GetDirectoryName(flashFile) ?? string.Empty,
                Path.GetFileNameWithoutExtension(flashFile) + "_eeprom.bin");
        }
        else if (dumpFlash)
        {
            flashFile = fileName ?? "s12h256_flash.bin";
        }
        else
        {
            eepromFile = fileName ?? "s12h256_eeprom.bin";
        }

        Log.WriteLine($"Cluster: {_ecuInfo}");
        Log.WriteLine(dumpFlash && dumpEeprom
            ? "Dumping flash and EEPROM."
            : dumpFlash ? "Dumping flash only." : "Dumping EEPROM only.");

        // Verify the runtime map before any 0x0C write.
        Log.WriteLine("Verifying the EEPROM/register map at $3000...");
        var signature = _kwp1281.ReadRomEeprom(EepromBase, 8);
        if (signature is not { Count: >= 8 } ||
            signature[0] != (byte)'V' ||
            !signature.Take(8).All(b => b is >= 0x20 and <= 0x7E))
        {
            Log.WriteLine("EEPROM at $3000 does not look like the S12H256 version string; aborting.");
            return string.Empty;
        }

        var service = BuildCopyService();
        Log.WriteLine($"Copy service: {service.Length} bytes at ${ServiceAddress:X4}; buffer ${ServiceBuffer:X4}.");
        if (!WriteMemory(ServiceAddress, service))
        {
            Log.WriteLine("Service upload failed; no files were written.");
            return string.Empty;
        }

        if (!WriteMemory(ServiceRequest, [0, 0, 0, 0, 0]) ||
            !WriteMemory(ServiceSeq, [0]))
        {
            Log.WriteLine("Service state init failed; no files were written.");
            return string.Empty;
        }

        if (!PatchCanTxStub(ServiceAddress, out byte originalHigh))
        {
            Log.WriteLine("CAN0 TX stub patch failed; no files were written.");
            return string.Empty;
        }

        int totalBytes =
            (dumpFlash ? FlashSize : 0) +
            (dumpEeprom ? EepromLowerSize + EepromUpperSize : 0);
        int doneBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;

        void Report(string stage, bool hasPage, byte page, ushort address)
        {
            TimeSpan now = stopwatch.Elapsed;
            if (now - lastReport < TimeSpan.FromSeconds(5.0) && doneBytes < totalBytes)
            {
                return;
            }

            lastReport = now;
            double rate = doneBytes / Math.Max(now.TotalSeconds, 0.001);
            int remaining = (int)Math.Max(0, (totalBytes - doneBytes) / Math.Max(rate, 1.0));
            string where = hasPage
                ? $"page ${page:X2} offset ${address & 0x3FFF:X4}"
                : $"${address:X4}";
            Log.WriteLine(
                $"Progress {100.0 * doneBytes / totalBytes,5:F1}%  " +
                $"{doneBytes,7:N0}/{totalBytes:N0} bytes  {stage} {where}  " +
                $"{rate,6:F0} B/s  ETA {remaining / 60}:{remaining % 60:00}");
        }

        void ReadRange(byte[] destination, int destinationOffset, ushort source,
            int length, byte page, bool hasPage, string stage)
        {
            for (int offset = 0; offset < length; offset += ServiceChunkSize)
            {
                int chunk = Math.Min(ServiceChunkSize, length - offset);
                var data = ReadServiceChunk((ushort)(source + offset), page, chunk);
                Array.Copy(data, 0, destination, destinationOffset + offset, chunk);
                doneBytes += chunk;
                Report(stage, hasPage, page, (ushort)(source + offset));
            }
        }

        Log.WriteLine(
            $"Dumping {totalBytes:N0} bytes in {ServiceChunkSize}-byte chunks; " +
            "progress is logged every 5 s.");
        Log.WriteLine(
            "Each request writes sourceHi sourceLo page length 01 to $162F; " +
            "its Values field in the log shows the current source.");

        try
        {
            if (dumpFlash)
            {
                var flash = new byte[FlashSize];
                for (int index = 0; index < 16; index++)
                {
                    byte page = (byte)(0x30 + index);
                    ushort baseAddress = page <= 0x3D
                        ? (ushort)0x8000
                        : page == 0x3E ? (ushort)0x4000 : (ushort)0xC000;
                    ReadRange(flash, index * PageSize, baseAddress, PageSize, page, true, "flash");
                    Log.WriteLine(
                        $"Page ${page:X2} done  {100.0 * doneBytes / totalBytes:F1}%  " +
                        $"elapsed {stopwatch.Elapsed:mm\\:ss}.");
                }

                File.WriteAllBytes(flashFile, flash);
                Log.WriteLine($"Saved flash to {flashFile} ({flash.Length} bytes)");
            }

            if (dumpEeprom)
            {
                var eeprom = Enumerable.Repeat((byte)0xFF, EepromSize).ToArray();
                ReadRange(eeprom, 0, 0x3000, EepromLowerSize, 0, false, "EEPROM");
                ReadRange(eeprom, EepromUpperOffset, 0x3C00, EepromUpperSize, 0, false, "EEPROM");
                Array.Fill(eeprom, (byte)0xFF, EepromOverlayOffset, EepromOverlaySize);

                File.WriteAllBytes(eepromFile, eeprom);
                Log.WriteLine($"Saved EEPROM to {eepromFile} ({eeprom.Length} bytes; $3800-$3BFF filled with 0xFF)");
                LogEepromSecrets(eeprom);
            }

            Log.WriteLine($"Done in {stopwatch.Elapsed:mm\\:ss}.");
        }
        finally
        {
            try
            {
                _ = _kwp1281.WriteRomEeprom((ushort)(Can0TxStub + 1), [originalHigh]);
                Log.WriteLine("Restored the CAN0 TX stub.");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"WARNING: could not restore the CAN0 TX stub: {ex.Message}");
            }
        }

        return dumpFlash ? flashFile : eepromFile;
    }

    private byte[] ReadServiceChunk(ushort source, byte page, int length)
    {
        var before = _kwp1281.ReadRomEeprom(ServiceSeq, 1);
        if (before is not { Count: 1 })
        {
            throw new InvalidOperationException("Could not read the service sequence byte.");
        }

        byte sequence = before[0];
        var request = new List<byte>
        {
            (byte)(source >> 8), (byte)source, page, (byte)length, 0x01,
        };
        if (!_kwp1281.WriteRomEeprom(ServiceRequest, request))
        {
            throw new InvalidOperationException("Service request write was not acknowledged.");
        }

        bool done = false;
        for (int attempt = 0; attempt < 100 && !done; attempt++)
        {
            var current = _kwp1281.ReadRomEeprom(ServiceSeq, 1);
            if (current is { Count: 1 } && current[0] != sequence)
            {
                done = true;
                break;
            }

            Thread.Sleep(10);
        }

        if (!done)
        {
            throw new TimeoutException(
                "The CAN0 TX service did not run; ensure CAN0 traffic or send a CAN frame.");
        }

        var data = new List<byte>(length);
        ushort address = ServiceBuffer;
        int remaining = length;
        while (remaining > 0)
        {
            int count = Math.Min((int)ReadChunkSize, remaining);
            var part = _kwp1281.ReadRomEeprom(address, (byte)count);
            if (part is not { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Could not read the service buffer at ${address:X4}.");
            }

            data.AddRange(part);
            address = (ushort)(address + count);
            remaining -= count;
        }

        if (data.Count != length)
        {
            throw new InvalidOperationException("Service buffer was short.");
        }

        return [.. data];
    }

    private bool PatchCanTxStub(ushort target, out byte originalHigh)
    {
        originalHigh = 0x55;
        var stub = _kwp1281.ReadRomEeprom(Can0TxStub, 3);
        if (stub is not { Count: 3 } || stub[0] != 0x06)
        {
            Log.WriteLine($"CAN0 TX stub at ${Can0TxStub:X4} is not a JMP.");
            return false;
        }

        ushort original = (ushort)((stub[1] << 8) | stub[2]);
        if (original < 0x3000)
        {
            // A previous run may have died before restoring the stub. Accept it only if
            // it already points directly at the service and the original stub page was
            // saved before the patch.
            var saved = _kwp1281.ReadRomEeprom(SavedStubHigh, 1);
            if (original == target && saved is { Count: 1 } && saved[0] >= 0x30)
            {
                originalHigh = saved[0];
                Log.WriteLine(
                    $"CAN0 TX stub is already patched to the service; " +
                    $"original page ${originalHigh:X2} was saved at ${SavedStubHigh:X4}.");
                return true;
            }

            Log.WriteLine(
                $"CAN0 TX stub already points into RAM (${original:X4}) and is not this " +
                "service; power-cycle the cluster.");
            return false;
        }

        if (stub[2] != Lo(target))
        {
            Log.WriteLine(
                $"CAN0 TX stub low byte is ${stub[2]:X2}, not ${Lo(target):X2}; cannot " +
                "patch only the high byte.");
            return false;
        }

        originalHigh = stub[1];
        if (!WriteMemory(SavedStubHigh, [originalHigh]))
        {
            Log.WriteLine($"Could not save the original stub page at ${SavedStubHigh:X4}.");
            return false;
        }

        if (!WriteMemory((ushort)(Can0TxStub + 1), [Hi(target)]))
        {
            Log.WriteLine("CAN0 TX stub write failed.");
            return false;
        }

        Log.WriteLine($"CAN0 TX stub: JMP ${original:X4} -> JMP ${target:X4}.");
        return true;
    }

    // Identity records in the 4 KB EEPROM image (offset 0 = $3000). The VIN and
    // immobilizer ID are stored in three XOR-obfuscated copies each; the VIN also has a
    // second, single-record slot (used when a cluster is moved to another vehicle). The
    // descriptor table at 3F/D860 carries the keys.
    private static readonly byte[] VinXorKey = [0xB8, 0x67, 0x82, 0x4E];         // 3F/D878
    private static readonly byte[] VinAltXorKey = [0x61, 0x38, 0x05, 0xC1];      // 3F/D8D8
    private static readonly byte[] ImmobilizerIdXorKey = [0xFF, 0x56, 0x73, 0xDF]; // 3F/DAE8
    private static readonly byte[] LoginCodeXorKey = [0x31, 0xDC, 0x12, 0x3E];   // 3F/DA48

    private static void LogEepromSecrets(byte[] eeprom)
    {
        Log.WriteLine("EEPROM secrets:");

        string? vin = DecodeFirstValidIdentity(
            eeprom, [0x0618, 0x062C, 0x0640], 17, VinXorKey);
        Log.WriteLine(vin is null ? "  VIN: unavailable" : $"  VIN: {vin}");

        string? vinAlt = DecodeFirstValidIdentity(
            eeprom, [0x0660], 17, VinAltXorKey);
        if (vinAlt is null)
        {
            Log.WriteLine("  VIN record 2: unavailable");
        }
        else if (vin is not null && vinAlt == vin)
        {
            Log.WriteLine($"  VIN record 2: {vinAlt} (same as VIN)");
        }
        else
        {
            Log.WriteLine($"  VIN record 2: {vinAlt}");
        }

        string? immobilizer = DecodeFirstValidIdentity(
            eeprom, [0x0738, 0x0748, 0x0758], 14, ImmobilizerIdXorKey);
        Log.WriteLine(immobilizer is null
            ? "  Immobilizer ID: unavailable"
            : $"  Immobilizer ID: {immobilizer}");

        var codes = new List<ushort>();
        foreach (int offset in new[] { 0x06F4, 0x06F8, 0x06FC })
        {
            ushort code = (ushort)(
                ((eeprom[offset] ^ LoginCodeXorKey[0]) << 8) |
                (eeprom[offset + 1] ^ LoginCodeXorKey[1]));
            codes.Add(code);
        }

        if (codes.Distinct().Count() == 1 && codes[0] is > 0 and < 0xFFFF)
        {
            Log.WriteLine(
                $"  Security Access code: {codes[0]:D5}");
        }
        else
        {
            Log.WriteLine(
                "  Security Access code: unavailable (copies: " +
                string.Join(", ", codes.Select(c => $"0x{c:X4}")) + ")");
        }
    }

    private static string? DecodeFirstValidIdentity(
        byte[] image, int[] offsets, int length, byte[] key)
    {
        foreach (int offset in offsets)
        {
            var chars = new char[length];
            bool valid = true;
            for (int i = 0; i < length; i++)
            {
                byte value = (byte)(image[offset + i] ^ key[i % key.Length]);
                if (value is not (>= (byte)'0' and <= (byte)'9') and
                    not (>= (byte)'A' and <= (byte)'Z'))
                {
                    valid = false;
                    break;
                }

                chars[i] = (char)value;
            }

            if (valid)
            {
                return new string(chars);
            }
        }

        return null;
    }

    /// <summary>
    /// Copy service at $16C3. Request: $162F srcHi, $1630 srcLo, $1631 page,
    /// $1632 length, $1633 command, $1634 sequence. Copies to $1638 and returns
    /// through the original CAN0 TX worker.
    /// </summary>
    private static byte[] BuildCopyService()
    {
        return
        [
            0xF6, 0x16, 0x33,               // ldab $1633 (command)
            0xC1, 0x01,                     // cmpb #$01
            0x26, 0x24,                     // bne Done
            0xF6, 0x38, 0x30,               // ldab $3830 (PPAGE)
            0x37,                           // pshb
            0xB6, 0x16, 0x31,               // ldaa $1631 (page)
            0x7A, 0x38, 0x30,               // staa $3830
            0xFE, 0x16, 0x2F,               // ldx $162F (source)
            0xCD, 0x16, 0x38,               // ldy #$1638 (buffer)
            0xF6, 0x16, 0x32,               // ldab $1632 (length)
            // Copy:
            0xA6, 0x30,                     // ldaa 1,X+
            0x6A, 0x70,                     // staa 1,Y+
            0x53,                           // decb
            0x26, 0xF9,                     // bne Copy
            0x33,                           // pulb
            0x7B, 0x38, 0x30,               // stab $3830 (restore PPAGE)
            0x79, 0x16, 0x33,               // clr $1633 (command)
            0x72, 0x16, 0x34,               // inc $1634 (sequence)
            // Done:
            0xC7,                           // clrb
            0x4A, 0xF3, 0x93, 0x00,         // call $F393,0 (CAN0 TX worker)
            0x0B,                           // rti
        ];
    }


    /// <summary>
    /// Reads $3000-$37EB through title 0x03. Chunks that the deny-list rejects are retried
    /// byte by byte so the accessible bytes around the protected ranges still land in the
    /// image. Everything not readable stays 0xFF.
    /// </summary>
    private byte[] ReadEepromViaService()
    {
        var image = Enumerable.Repeat((byte)0xFF, EepromSize).ToArray();
        int denied = 0;

        for (int address = EepromBase; address <= EepromServiceEnd;)
        {
            int chunk = Math.Min((int)ReadChunkSize, EepromServiceEnd + 1 - address);
            var bytes = _kwp1281.ReadRomEeprom((ushort)address, (byte)chunk);
            if (bytes is { Count: > 0 })
            {
                var copy = bytes.ToArray();
                Array.Copy(copy, 0, image, address - EepromBase, Math.Min(copy.Length, chunk));
                address += chunk;
                continue;
            }

            if (chunk > 1)
            {
                for (int i = 0; i < chunk; i++)
                {
                    var one = _kwp1281.ReadRomEeprom((ushort)(address + i), 1);
                    if (one is { Count: > 0 })
                    {
                        image[address - EepromBase + i] = one[0];
                    }
                    else
                    {
                        denied++;
                    }
                }

                address += chunk;
            }
            else
            {
                denied++;
                address++;
            }
        }

        if (denied > 0)
        {
            Log.WriteLine($"0x03 denied {denied} EEPROM byte(s) (protected ranges); left as 0xFF.");
        }

        return image;
    }

    private bool WriteMemory(ushort address, IReadOnlyList<byte> data)
    {
        for (int offset = 0; offset < data.Count; offset += WriteChunkSize)
        {
            var chunk = data.Skip(offset).Take(WriteChunkSize).ToList();
            if (!WriteMemoryChunk((ushort)(address + offset), chunk))
            {
                return false;
            }
        }

        return true;
    }

    private bool WriteMemoryChunk(ushort address, List<byte> chunk)
    {
        if (_kwp1281.WriteRomEeprom(address, chunk))
        {
            return true;
        }

        if (chunk.Count == 1)
        {
            return false;
        }

        // The per-request count limit is not documented. Split and retry so a smaller
        // limit still works, down to one byte per request.
        int half = chunk.Count / 2;
        return WriteMemoryChunk(address, [.. chunk.Take(half)]) &&
               WriteMemoryChunk((ushort)(address + half), chunk.Skip(half).ToList());
    }

    private static byte Hi(ushort value) => (byte)(value >> 8);

    private static byte Lo(ushort value) => (byte)value;

    private readonly IKW1281Dialog _kwp1281;
    private readonly string _ecuInfo;

    public MarelliS12H256Cluster(IKW1281Dialog kwp1281, string ecuInfo)
    {
        _kwp1281 = kwp1281;
        _ecuInfo = ecuInfo;
    }
}
