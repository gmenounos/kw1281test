using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;

namespace BitFab.KW1281Test.EDC16
{
    /// <summary>
    /// Bosch EDC16 flash checksum verification and correction.
    ///
    /// <para>Reverse-engineered from K-line bus sniffing and disassembly of the
    /// EDC16 internal and external flash.</para>
    ///
    /// <para>A 2 MB EDC16 flash image (EDC16U31/U34) carries TWO 32-bit big-endian checksums in its
    /// calibration area, each chosen so that the BE-DWORD sum of its region (INCLUDING the checksum
    /// slot itself) equals the fixed constant <see cref="TargetSum"/> (0xD01FE500):</para>
    /// <list type="bullet">
    /// <item><b>block</b> checksum at 0x1BFFFC over [0x180000, 0x1BFFFC).</item>
    /// <item><b>span</b>  checksum at 0x1FDFFC over [0x18002C, 0x1FDFFC).</item>
    /// </list>
    /// <para>The region layout is anchored by a 0xCAFECADE marker (stored little-endian) in the
    /// calibration area (0x18003D on every VW EDC16U31/U34 image seen). The block region is the
    /// 0x40000-aligned sector containing the marker; the span region runs from just before the marker
    /// (marker - 0x11, DWORD-aligned) to the last populated DWORD. The block checksum is written
    /// FIRST because its slot lies inside the span region, so the span sum must include the finalised
    /// block value. A 1 MB EDC16U1 image uses the same TargetSum with 0x80000 sectors.</para>
    ///
    /// <para>Any tune that changes calibration bytes invalidates both checksums; an ECU rejects (or a
    /// modified image runs wrong / limps) until they are corrected. <see cref="VerifyAndCorrect"/>
    /// rewrites both in place; <see cref="Verify"/> reports their state without modifying the image.
    /// A file whose size or 0xCAFECADE layout isn't recognized reports
    /// <see cref="Result.Supported"/> = false and is never touched -- silence, not a guess, exactly
    /// like <see cref="EDC15.Edc15Checksum"/>.</para>
    ///
    /// <para>EDC16U31/U34 images ALSO carry a 33-byte content <b>digest</b> in each calibration block's
    /// trailer (at additiveSlot-0x21), checked by the ECU's 0x31/0x33 C5 verify: an edited block is
    /// rejected (7F 33 22 forever) until the digest matches. The digest is the low 33 bytes of the
    /// integer cube root of a 128-byte value built from MD5 of the block body.
    /// <see cref="VerifyAndCorrect"/> regenerates it BEFORE the additive slots (each digest lies inside
    /// an additive region); <see cref="Verify"/> reports it as the dig-b / dig-s slots. This is what lets
    /// this program flash edited EDC16 calibrations.</para>
    /// </summary>
    public static class Edc16Checksum
    {
        public const int SizeU31 = 0x200000;      // 2 MB (EDC16U31 / U34)
        public const int SizeU1 = 0x100000;       // 1 MB (EDC16U1)
        public const uint TargetSum = 0xD01FE500;  // required BE-DWORD sum of each region
        private const int MarkerDelta = 0x11;       // span_start = (marker - 0x11) & ~3
        private const int BlockU31 = 0x40000;       // 2 MB: 256 KB block sector
        private const int BlockU1 = 0x80000;        // 1 MB: 512 KB block sector

        // 0xCAFECADE stored little-endian.
        private static readonly byte[] Marker = { 0xDE, 0xCA, 0xFE, 0xCA };

        // 2 MB images: the calibration marker lives in the upper half; searching from here skips a
        // stray 0xCAFECADE byte pattern in the program code of a full (boot-mode) read.
        private const int CalSearchStart = 0x100000;

        public enum Algorithm
        {
            /// <summary>Layout not recognized (size or marker) -- nothing was checked.</summary>
            None,
            /// <summary>2 MB EDC16U31 / U34 (block + span, 0x40000 sectors).</summary>
            Edc16_U31U34,
            /// <summary>1 MB EDC16U1 (block + span, 0x80000 sectors).</summary>
            Edc16_U1,
        }

        /// <summary>Common view of one checksum slot -- an additive DWORD or a content digest --
        /// for the <see cref="Result"/> report.</summary>
        public interface ISlot
        {
            string Name { get; }
            int Offset { get; }
            bool Ok { get; }
            string Describe();
        }

        /// <summary>One additive checksum slot (block or span): a 32-bit big-endian DWORD.</summary>
        public sealed class Slot : ISlot
        {
            public string Name { get; init; } = "";
            public int Offset { get; init; }
            public int RegionStart { get; init; }
            public uint Stored { get; init; }
            public uint Expected { get; init; }
            public bool Ok => Stored == Expected;

            public string Describe() =>
                $"{Name,-5} @0x{Offset:X6}  stored=0x{Stored:X8} expected=0x{Expected:X8}  " +
                $"[{(Ok ? "OK" : "MISMATCH")}]";
        }

        /// <summary>One 33-byte content-digest slot (EDC16U31/U34). Compared and shown byte-exact --
        /// no fingerprint, so the report prints the actual stored vs expected digest.</summary>
        public sealed class DigestSlot : ISlot
        {
            public string Name { get; init; } = "";
            public int Offset { get; init; }
            public int RegionStart { get; init; }
            public byte[] Stored { get; init; } = Array.Empty<byte>();
            public byte[] Expected { get; init; } = Array.Empty<byte>();
            public bool Ok => Stored.AsSpan().SequenceEqual(Expected);

            public string Describe() =>
                $"{Name,-5} @0x{Offset:X6}  stored={Hex(Stored)} expected={Hex(Expected)}  " +
                $"[{(Ok ? "OK" : "MISMATCH")}]";

            // The slot holds the full canonical trailer (gap + prefix + digest); the digest is its
            // last DigestLength bytes -- show that, the informative part.
            private static string Hex(byte[] b) =>
                b.Length == 0 ? "(none)"
                : Convert.ToHexString(b, b.Length > DigestLength ? b.Length - DigestLength : 0,
                                      b.Length < DigestLength ? b.Length : DigestLength).ToLowerInvariant();
        }

        /// <summary>Result of a <see cref="Verify"/> or <see cref="VerifyAndCorrect"/> call. Mirrors
        /// <see cref="EDC15.Edc15Checksum.Result"/> so the write-path checksum gate is identical for
        /// both ECU families.</summary>
        public sealed class Result
        {
            /// <summary>False when the image size/layout isn't a recognized EDC16 flash: the other
            /// fields are then meaningless and the buffer was not modified.</summary>
            public bool Supported { get; init; }
            public Algorithm Algorithm { get; init; }
            public int RegionsChecked { get; init; }
            public int RegionsMismatched { get; init; }
            public int MarkerOffset { get; init; }
            public IReadOnlyList<ISlot> Slots { get; init; } = Array.Empty<ISlot>();

            public bool Valid => Supported && RegionsMismatched == 0;

            public string Describe()
            {
                if (!Supported)
                {
                    return "EDC16 checksums: layout not recognized (not a 1 MB / 2 MB Bosch EDC16 image).";
                }
                var head = $"EDC16 checksums (marker @0x{MarkerOffset:X6}): " +
                           (Valid ? "all valid" : "correction needed");
                var lines = new List<string> { head };
                foreach (var s in Slots)
                {
                    lines.Add("  " + s.Describe());
                }
                return string.Join("\n", lines);
            }
        }

        /// <summary>Report the state of both checksums without modifying the image.</summary>
        public static Result Verify(byte[] image) => Run(image, correctInPlace: false);

        /// <summary>Recompute and re-write both checksums in place (block first, then span). Returns a
        /// Result whose <see cref="Result.RegionsMismatched"/> is how many slots were wrong before
        /// correction. An unrecognized layout (Supported = false) is never a throw and never modifies
        /// the buffer.</summary>
        public static Result VerifyAndCorrect(byte[] image) => Run(image, correctInPlace: true);

        /// <summary>True if this looks like a checksummable EDC16 image (1 MB or 2 MB with a marker).</summary>
        public static bool IsSupported(byte[] image) => Verify(image).Supported;

        private static Result Run(byte[] image, bool correctInPlace)
        {
            if (image == null)
            {
                return new Result { Supported = false, Algorithm = Algorithm.None };
            }

            if (!TrySlots(image, out var algorithm, out var markerOffset, out var specs))
            {
                return new Result { Supported = false, Algorithm = Algorithm.None };
            }

            var slots = new List<ISlot>(specs.Count + 2);
            var mismatched = 0;

            // EDC16U31/U34 also carry a 33-byte content "digest" in each calibration block's trailer:
            // the low 33 bytes of the integer cube root of a 128-byte block built from MD5 of the block
            // body. The ECU's 0x31/0x33 C5 verify rejects an edited block until this digest matches, so
            // it MUST be regenerated before the additive slots (each digest lies inside an additive
            // region and would otherwise unbalance the additive sum).
            if (algorithm == Algorithm.Edc16_U31U34)
            {
                foreach (var (name, _, chk) in specs)
                {
                    var blockStart = chk & ~(BlockU31 - 1);
                    var trailerStart = chk - DigestBodyBack;   // start of the 128-byte canonical trailer

                    // The canonical trailer is the 84-byte gap zeroed (the ECU requires it: an edited
                    // block whose gap is left dirty fails the ECU's 0x31/0x33 C5 verify even with a
                    // correct digest) followed by the 44-byte prefix+digest.
                    var prefixDigest = ComputeDigestTrailer(image, blockStart, trailerStart); // 44 bytes
                    var expected = new byte[DigestBodyBack];   // 128, gap pre-zeroed by allocation
                    Array.Copy(prefixDigest, 0, expected, DigestBodyBack - PrefixBack, PrefixBack);

                    var stored = ReadRegion(image, trailerStart, DigestBodyBack);
                    if (!stored.AsSpan().SequenceEqual(expected))
                    {
                        mismatched++;
                        if (correctInPlace)
                        {
                            Array.Copy(expected, 0, image, trailerStart, DigestBodyBack);
                            stored = expected;   // reflect the correction in the report
                        }
                    }
                    slots.Add(new DigestSlot
                    {
                        Name = name == "block" ? "dig-b" : "dig-s",
                        Offset = chk - DigestBack,   // digest location, for the report
                        RegionStart = blockStart,
                        Stored = stored,
                        Expected = expected,
                    });
                }
            }

            // Additive checksums: block first, then span (the span region covers the block slot).
            foreach (var (name, start, chk) in specs)
            {
                var sumExcl = BeSum(image, start, chk);                 // excludes the slot itself
                var expected = unchecked(TargetSum - sumExcl);
                var stored = Be32(image, chk);
                if (stored != expected)
                {
                    mismatched++;
                    if (correctInPlace)
                    {
                        WriteBe32(image, chk, expected);
                    }
                }
                slots.Add(new Slot
                {
                    Name = name,
                    Offset = chk,
                    RegionStart = start,
                    Stored = correctInPlace ? expected : stored,
                    Expected = expected,
                });
            }

            return new Result
            {
                Supported = true,
                Algorithm = algorithm,
                RegionsChecked = slots.Count,
                RegionsMismatched = mismatched,
                MarkerOffset = markerOffset,
                Slots = slots,
            };
        }

        /// <summary>
        /// Derive (algorithm, markerOffset, [(name, regionStart, checksumOffset), ...]) for this
        /// image, or false if the size/marker layout isn't recognized. Two layouts, both using
        /// <see cref="TargetSum"/> and a span checksum at the last populated DWORD:
        /// 2 MB (U31/U34) block = the 0x40000 sector holding the upper-half marker; 1 MB (U1) block =
        /// the first 0x80000 sector, span anchored on the second-sector marker.
        /// </summary>
        private static bool TrySlots(
            byte[] data, out Algorithm algorithm, out int markerOffset,
            out List<(string Name, int Start, int Chk)> specs)
        {
            algorithm = Algorithm.None;
            markerOffset = 0;
            specs = new List<(string, int, int)>();

            int blockStart, blockChk, spanStart, spanChk;

            if (data.Length == SizeU31)
            {
                var marker = FindMarker(data, CalSearchStart);
                if (marker < 0)
                {
                    return false;
                }
                blockStart = marker & ~(BlockU31 - 1);
                blockChk = blockStart + BlockU31 - 4;
                spanStart = (marker - MarkerDelta) & ~3;
                spanChk = SpanEnd(data);
                markerOffset = marker;
                algorithm = Algorithm.Edc16_U31U34;
            }
            else if (data.Length == SizeU1)
            {
                var m0 = IndexOf(data, Marker, 0);          // first sector marker (~0x3D)
                if (m0 < 0)
                {
                    return false;
                }
                blockStart = m0 & ~(BlockU1 - 1);           // 0
                blockChk = blockStart + BlockU1 - 4;        // 0x7FFFC
                var m1 = IndexOf(data, Marker, BlockU1);    // second-sector marker (~0x8003D)
                if (m1 < 0)
                {
                    return false;
                }
                spanStart = (m1 - MarkerDelta) & ~3;        // 0x8002C
                spanChk = SpanEnd(data);                    // 0xFDFFC
                markerOffset = m0;
                algorithm = Algorithm.Edc16_U1;
            }
            else
            {
                return false;
            }

            if (spanChk == 0 || spanChk <= blockChk || spanStart >= spanChk || blockChk + 4 > data.Length)
            {
                algorithm = Algorithm.None;
                markerOffset = 0;
                return false;
            }

            specs.Add(("block", blockStart, blockChk));
            specs.Add(("span", spanStart, spanChk));
            return true;
        }

        /// <summary>Offset of the anchoring 0xCAFECADE marker: search from <paramref name="from"/>
        /// first (skips a stray marker in program code on a full read), then a global scan (blank-code
        /// tuning / calibration-only reads whose only marker is at the very start). -1 if none.</summary>
        private static int FindMarker(byte[] data, int from)
        {
            var idx = IndexOf(data, Marker, from);
            if (idx < 0)
            {
                idx = IndexOf(data, Marker, 0);
            }
            return idx;
        }

        /// <summary>Last DWORD-aligned offset whose big-endian value is not 0xFFFFFFFF (the last
        /// populated DWORD), or 0 if none. Mirrors checksum.py's _span_end.</summary>
        private static int SpanEnd(byte[] data)
        {
            for (var off = (data.Length - 4) & ~3; off > 3; off -= 4)
            {
                if (Be32(data, off) != 0xFFFFFFFF)
                {
                    return off;
                }
            }
            return 0;
        }

        private static uint Be32(byte[] data, int off) =>
            (uint)((data[off] << 24) | (data[off + 1] << 16) | (data[off + 2] << 8) | data[off + 3]);

        private static void WriteBe32(byte[] data, int off, uint value)
        {
            data[off] = (byte)(value >> 24);
            data[off + 1] = (byte)(value >> 16);
            data[off + 2] = (byte)(value >> 8);
            data[off + 3] = (byte)value;
        }

        /// <summary>Big-endian DWORD sum over [start, end), mod 2^32. end is DWORD-aligned by the
        /// callers (checksum offsets are always multiples of 4).</summary>
        private static uint BeSum(byte[] data, int start, int end)
        {
            uint total = 0;
            for (var off = start; off < end; off += 4)
            {
                total = unchecked(total + Be32(data, off));
            }
            return total;
        }

        // ---- 33-byte trailer digest (EDC16U31/U34) ----
        private const int DigestLength = 33;      // content digest bytes
        private const int DigestBack = 0x21;       // digest starts (additiveSlot - 0x21)
        private const int PrefixBack = 0x2C;       // 11-byte prefix + 33-byte digest starts (slot - 0x2C)
        private const int DigestBodyBack = 0x80;   // MD5 body = [blockStart, additiveSlot - 0x80)

        /// <summary>Build the 44-byte trailer (11-byte prefix followed by the 33-byte digest) for one
        /// calibration block. digest = low 33 bytes of floor(cbrt(P)), where P is a 128-byte big-endian
        /// integer: 00 01 FF*8 00 MD5(body)[16] 00*15 C0 FF*85, and body = [bodyStart, bodyEnd). The
        /// 11-byte prefix (00 01 42 8A 2F 98 D7 28 AE 22 08) is simply the fixed high bytes of the same
        /// cube root.</summary>
        private static byte[] ComputeDigestTrailer(byte[] image, int bodyStart, int bodyEnd)
        {
            byte[] md5;
            using (var h = MD5.Create())
            {
                md5 = h.ComputeHash(image, bodyStart, bodyEnd - bodyStart);
            }

            var p = new byte[128];
            p[1] = 0x01;
            for (var i = 2; i < 10; i++) p[i] = 0xFF;
            Array.Copy(md5, 0, p, 11, 16);
            p[42] = 0xC0;
            for (var i = 43; i < 128; i++) p[i] = 0xFF;

            var bigP = new BigInteger(p, isUnsigned: true, isBigEndian: true);
            var root = IntegerCubeRoot(bigP);

            // 44-byte big-endian: [11-byte prefix][33-byte digest].
            var trailer = new byte[PrefixBack];
            var raw = root.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length > trailer.Length)
            {
                // Cannot happen for a well-formed 128-byte P (root is ~43 bytes); guard rather than
                // silently emit a wrong-but-plausible digest.
                throw new InvalidOperationException(
                    $"EDC16 digest cube root is {raw.Length} bytes, exceeding the {trailer.Length}-byte " +
                    "trailer -- malformed padded block.");
            }
            Array.Copy(raw, 0, trailer, trailer.Length - raw.Length, raw.Length);
            return trailer;
        }

        /// <summary>floor(n**(1/3)) for n &gt;= 0, by binary search on x^3 &lt;= n.</summary>
        private static BigInteger IntegerCubeRoot(BigInteger n)
        {
            if (n <= 0) return BigInteger.Zero;
            BigInteger hi = 1;
            while (hi * hi * hi <= n) hi <<= 1;
            var lo = hi >> 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) >> 1;
                if (mid * mid * mid <= n) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private static byte[] ReadRegion(byte[] data, int off, int len)
        {
            var b = new byte[len];
            Array.Copy(data, off, b, 0, len);
            return b;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            var last = haystack.Length - needle.Length;
            for (var i = Math.Max(0, start); i <= last; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
