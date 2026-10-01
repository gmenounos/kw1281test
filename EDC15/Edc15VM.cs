using BitFab.KW1281Test.Kwp2000;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace BitFab.KW1281Test.EDC15;

public class Edc15VM
{
    public byte[] ReadWriteEeprom(
        string filename,
        List<KeyValuePair<ushort, byte>>? addressValuePairs = null)
    {
        addressValuePairs ??= [];

        var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

        _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, [0x89]);

        _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, [0x85]);

        const byte accMod = 0x41;
        var resp = kwp2000.SendReceive(DiagnosticService.securityAccess, [accMod]);

        // ECU normally doesn't require seed/key authentication the first time it wakes up in
        // KWP2000 mode so sending an empty key is sufficient.
        var buf = new List<byte> { accMod + 1 };

        if (!resp.Body.SequenceEqual(new byte[] { accMod, 0x00, 0x00 }))
        {
            // Normally we'll only get here if we wake up the ECU and it's already in KWP2000 mode,
            // which can happen if a previous download attempt did not complete. In that case we
            // need to calculate and send back a real key.
            var seedBuf = resp.Body.Skip(1).Take(4).ToArray();
            var keyBuf = LVL41Auth(0x508DA647, 0x3800000, seedBuf);

            buf.AddRange(keyBuf);
        }
        _ = kwp2000.SendReceive(DiagnosticService.securityAccess, buf.ToArray());

        var loader = Edc15VM.GetLoader();
        var len = loader.Length;

        // Ask the ECU to accept our loader and store it in RAM
        _ = kwp2000.SendReceive(DiagnosticService.requestDownload, [
            0x40, 0xE0, 0x00, // Load address 0x40E000
            0x00, // Not compressed, not encrypted
            (byte)(len >> 16), (byte)(len >> 8), (byte)(len & 0xFF) // Length
            ],
        excludeAddresses: true);

        // Break the loader into blocks and send each one
        var maxBlockLen = resp.Body[0];
        var s = new MemoryStream(loader);
        while (true)
        {
            Thread.Sleep(5);

            var blockBytes = new byte[maxBlockLen];
            var readCount = s.Read(blockBytes, 0, maxBlockLen - 1);
            if (readCount == 0)
            {
                break;
            }

            _ = kwp2000.SendReceive(
                DiagnosticService.transferData, blockBytes.Take(readCount).ToArray(),
                excludeAddresses: true);
        }

        // Ask the ECU to execute our loader
        kwp2000.SendMessage(
            DiagnosticService.startRoutineByLocalIdentifier, [0x02],
            excludeAddresses: true);
        _ = kwp2000.ReceiveMessage();

        // Custom loader command to send all 512 bytes of the EEPROM
        kwp2000.SendMessage(
            (DiagnosticService)0xA6, [],
            excludeAddresses: true);
        resp = kwp2000.ReceiveMessage();
        if (!resp.IsPositiveResponse(DiagnosticService.transferData))
        {
            throw new InvalidOperationException($"Dump EEPROM failed.");
        }

        var eeprom = new byte[512];
        for (var i = 0; i < 512; i++)
        {
            eeprom[i] = _kwpCommon.Interface.ReadByte();
        }

        File.WriteAllBytes(filename, eeprom);
        Log.WriteLine($"Saved EEPROM to {filename}");

        _ = kwp2000.ReceiveMessage();

        // Now write any supplied values
        foreach (var addressValuePair in addressValuePairs)
        {
            var service = (DiagnosticService)(
                addressValuePair.Key > 0xFF
                    ? 0xA8  // Write 1 byte to EEPROM (Page 1)
                    : 0xA7); // Write 1 byte to EEPROM (Page 0)

            kwp2000.SendMessage(
                service, [],
                excludeAddresses: true);
            resp = kwp2000.ReceiveMessage();
            if (!resp.IsPositiveResponse(DiagnosticService.transferData))
            {
                throw new InvalidOperationException($"Write EEPROM failed.");
            }

            var address = (byte)(addressValuePair.Key & 0xFF);
            var value = addressValuePair.Value;

            _kwpCommon.WriteByte(address);
            _kwpCommon.WriteByte(value);
            Log.WriteLine($"Sent: {address:X2} {value:X2}");

            resp = kwp2000.ReceiveMessage();
            if (!resp.IsPositiveResponse(DiagnosticService.transferData))
            {
                throw new InvalidOperationException($"Write EEPROM failed.");
            }
        }

        // Custom loader command to reboot the ECU to return it to normal operation.
        kwp2000.SendMessage(
                (DiagnosticService)0xA2, [],
            excludeAddresses: true);
        _ = kwp2000.ReceiveMessage();

        var b = _kwpCommon.Interface.ReadByte();
        if (b == 0x55)
        {
            Log.WriteLine($"Reboot successful!");
        }

        return eeprom;
    }

    public static void DisplayEepromInfo(ReadOnlySpan<byte> eeprom)
    {
        var skc = Utils.GetShort(eeprom, 0x12E);
        Log.WriteLine($"SKC: {skc:D5}");

        double odometerKm =
            eeprom[0x1BF] +
            (eeprom[0x1C0] << 8) +
            (eeprom[0x1C1] << 16) +
            ((eeprom[0x1C2] & 0x3F) << 24);
        odometerKm /= 100.0;
        Log.WriteLine($"Odometer: {odometerKm} km");

        var vin = Utils.DumpAscii(eeprom.Slice(0x140, 17).ToArray());
        Log.WriteLine($"VIN: {vin}");

        var immoNumber = Utils.DumpAscii(eeprom.Slice(0x131, 14).ToArray());
        Log.WriteLine($"Immo Number: {immoNumber}");

        var immoId = Utils.DumpBytes(eeprom.Slice(0x126, 7).ToArray());
        Log.WriteLine($"Immo Id: {immoId}");

        const ushort immo1Addr = 0x1B0;
        var immo1 = eeprom[immo1Addr];
        const ushort immo2Addr = 0x1DE;
        var immo2 = eeprom[immo2Addr];
        var immoStatus = immo1 == 0x60 && immo2 == 0x60 ? "Off" : "On";
        Log.WriteLine($"Immo is {immoStatus} (${immo1Addr:X3}=${immo1:X2}, ${immo2Addr:X3}=${immo2:X2})");
    }

    /// <summary>
    /// Computes the EDC15 security access key (level 0x41) from the 4-byte seed.
    ///
    /// The seed is split into two 16-bit words which are mixed over five rounds.
    /// Each round shifts the low word left, then either carries the top bit of
    /// the high word into bit 0 of the low word (plain round), or performs a
    /// deeper mix that also folds in the two halves of the fixed auth key and
    /// rebuilds the control word (mixing round).
    ///
    /// Borrowed from https://github.com/fjvva/ecu-tool and simplified.
    /// Thanks to Javier Vazquez Vidal (https://github.com/fjvva).
    /// </summary>
    /// <param name="authKey">Fixed auth key; used as two 16-bit halves.</param>
    /// <param name="controlWord">Mutable working register of the mixer (0x03800000).</param>
    /// <param name="seed">The 4-byte seed received from the ECU.</param>
    /// <returns>The 4-byte key, packed as two big-endian 16-bit words.</returns>
    internal static byte[] LVL41Auth(uint authKey, uint controlWord, byte[] seed)
    {
        // Split the seed into two 16-bit words (big-endian).
        uint lowWord = (uint)(seed[0] << 8) + seed[1];
        uint highWord = (uint)(seed[2] << 8) + seed[3];

        // Split the fixed auth key into its two 16-bit halves.
        uint keyHi = authKey >> 16;
        uint keyLo = authKey & 0xFFFF;

        for (var round = 0; round < 5; round++)
        {
            // Capture the low word's top bit before shifting it left; it
            // selects the round variant below.
            var topBit = lowWord & 0x8000;
            lowWord <<= 1;

            if (topBit == 0)
            {
                // Plain round: carry the top bit of the high word into bit 0
                // of the low word, and shift the high word left as well.
                lowWord &= 0xFFFE;
                lowWord |= (highWord & 0x8000) >> 15;
                highWord <<= 1;
            }
            else
            {
                // Mixing round: double the high word and rebuild the control
                // word around its low byte.
                var doubleHigh = highWord + highWord;
                lowWord &= 0xFFFE;

                // Low byte becomes (low byte of the doubled high word) | 1,
                // with the upper bytes of the old control word preserved.
                controlWord = ((doubleHigh & 0xFF) | 1) + (controlWord & 0xFFFFFF00);
                controlWord = (controlWord & 0xFFFF00FF) | doubleHigh;

                // Fold the overflow bits of the doubling back in, use the top
                // bit of the result for bit 0, then overlay the low word.
                var foldedHigh = (highWord & 0xFFFF) + (doubleHigh & 0xFFFF0000);
                var newLowWord = (foldedHigh & 0xFFFF0000) + ((foldedHigh & 0xFFFF) >> 15);
                newLowWord |= lowWord;

                // Mix in the two halves of the fixed auth key.
                controlWord ^= keyHi;
                newLowWord ^= keyLo;

                highWord = controlWord;
                lowWord = newLowWord;
            }
        }

        // Truncate to 16 bits and pack the key, big-endian per word.
        lowWord &= 0xFFFF;
        highWord &= 0xFFFF;

        return
        [
            (byte)(lowWord >> 8),   // low word, high byte
            (byte)lowWord,          // low word, low byte
            (byte)(highWord >> 8),  // high word, high byte
            (byte)highWord          // high word, low byte
        ];
    }

    /// <summary>
    /// Loader that can read/write the serial EEPROM.
    /// </summary>
    private static byte[] GetLoader()
    {
        var assembly = Assembly.GetEntryAssembly()!;
        var resourceStream = assembly.GetManifestResourceStream(
            "BitFab.KW1281Test.EDC15.Loader.bin");
        if (resourceStream == null)
        {
            throw new InvalidOperationException(
                $"Unable to load BitFab.KW1281Test.EDC15.Loader.bin embedded resource.");
        }

        var loaderLength = resourceStream.Length + 4; // Add 4 bytes for checksum correction
        loaderLength = (loaderLength + 7) / 8 * 8; // Round up to a multiple of 8 bytes
        var buf = new byte[loaderLength];

        resourceStream.ReadExactly(buf, 0, (int)resourceStream.Length);

        // In order for this loader to be executed by the ECU, the checksum of all the bytes
        // must be EFCD8631.

        // Patch the loader with the location of the end (actually 1 byte past the end)
        ushort loaderEnd = (ushort)(0xE000 + loaderLength);
        buf[0x0E] = (byte)(loaderEnd & 0xFF);
        buf[0x0F] = (byte)(loaderEnd >> 8);

        // Take the checksum of the loader up to but not including the checksum correction
        ushort r6 = 0xEFCD;
        ushort r1 = 0x8631;
        Checksum(ref r6, ref r1, buf.Take(buf.Length - 4).ToArray());

        // Calculate the checksum correction bytes and insert them at the end of the loader
        var padding = CalcPadding(r6, r1);
        Array.Copy(padding, 0, buf, buf.Length - 4, 4);

        return buf;
    }

    /// <summary>
    /// Calculate the checksum correction padding needed to result in a checksum of EFCD8631
    /// </summary>
    /// <param name="r6"></param>
    /// <param name="r1"></param>
    /// <returns></returns>
    private static byte[] CalcPadding(ushort r6, ushort r1)
    {
        var paddingH = (ushort)(0xDF9B ^ r6);
        var paddingL = (ushort)(r1 - 0xAB85);

        return
        [
            (byte)(paddingL & 0xFF),
            (byte)(paddingL >> 8),
            (byte)(paddingH & 0xFF),
            (byte)(paddingH >> 8)
        ];
    }

    /// <summary>
    /// EDC15 checksum algorithm (sub_1584).
    /// Calculates a 32-bit checksum of an array of bytes based on an initial 32-bit seed.
    /// Based on https://www.ecuconnections.com/forum/viewtopic.php?f=211&t=49704&sid=5cf324c44d2c74d372984f428ffea5ed
    /// </summary>
    /// <param name="r6">Input: High word of seed, Output: High word of checksum</param>
    /// <param name="r1">Input: Low word of seed, Output: Low word of checksum</param>
    /// <param name="buf">Buffer to calculate checksum for</param>
    static void Checksum(ref ushort r6, ref ushort r1, byte[] buf)
    {
        int r3 = 0; // Buffer index
        int r0 = buf.Length;
        while (true)
        {
            r1 ^= GetBuf(buf, r3); r3 += 2;
            r1 = Rol(r1, r6, out ushort c);
            r6 = (ushort)(r6 - GetBuf(buf, r3) - c); r3 += 2;
            r6 ^= r1;
            if (r3 >= r0)
            {
                break;
            }

            r1 = (ushort)(r1 - GetBuf(buf, r3) - 1); r3 += 2;
            r1 += 0xDAAD;
            r6 ^= GetBuf(buf, r3); r3 += 2;
            r6 = Ror(r6, r1);
            if (r3 >= r0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Rotates a 16-bit value right by count bits.
    /// </summary>
    private static ushort Ror(ushort value, ushort count)
    {
        count &= 0xF;
        value = (ushort)((value >> count) | (value << (16 - count)));
        return value;
    }

    /// <summary>
    /// Rotates a 16-bit value left by count bits. Carry will be equal to the last bit rotated
    /// or 0 if the low 4 bits of count are 0;
    /// </summary>
    private static ushort Rol(ushort value, ushort count, out ushort carry)
    {
        count &= 0xF;
        value = (ushort)((value << count) | (value >> (16 - count)));
        carry = ((value & 1) == 0 || (count == 0)) ? (ushort)0 : (ushort)1;
        return value;
    }

    private static ushort GetBuf(byte[] buf, int ix)
    {
        return (ushort)(buf[ix] + (buf[ix + 1] << 8));
    }

    private readonly IKwpCommon _kwpCommon;
    private readonly int _controllerAddress;

    public Edc15VM(IKwpCommon kwpCommon, int controllerAddress)
    {
        _kwpCommon = kwpCommon;
        _controllerAddress = controllerAddress;
    }
}
