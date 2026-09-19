using System;

namespace BitFab.KW1281Test.EDC15
{
    /// <summary>
    /// EDC15P/EDC15V/EDC15VM+ external-flash checksum verification and correction -- a C# port of
    /// the "Bosch VAG TDI v4.1 / v4.1-2002" checksum algorithm from the open-source VAGEDCSuite
    /// project (https://github.com/Blackfrosch/VAGEDCSuite, <c>EDC15P_checksum.cs</c> and
    /// <c>Tools.cs</c>, itself Copyright 2007-2012 MTX Electronics / www.mtx-electronics.com) --
    /// the same project the user's own <c>Reference/Checksums/VAGEDI15_16_Checksum.cpp</c> credits
    /// as its origin.
    ///
    /// <para><b>Scope:</b> only the 512KB (0x80000-byte) EDC15P/V/VM+ layout is supported -- the
    /// only size <see cref="Edc15FlashVM"/> ever reads or writes (see its <c>WriteFlash</c>'s own
    /// 0x80000-byte length check). VAGEDCSuite's separate 1MB EDC15VM table (a different ECU class
    /// this codebase doesn't talk to) and its EDC16/EDC17/MSA/etc. checksum algorithms are
    /// intentionally not ported here.</para>
    ///
    /// <para><b>The three algorithms, in the order they're tried (matching
    /// <c>Tools.CalculateEDC15PChecksum</c> exactly):</b> "V4.1 rev.1" (11-region table, signature
    /// at 0x50008) is tried first if that signature is present; otherwise "V4.1 rev.2" (7-region
    /// table, signature at 0x58008) is tried if THAT signature is present; otherwise rev.1 is
    /// still tried first (the reference's own fallback default). If whichever V4.1 attempt was
    /// tried comes back "wrong table" (many regions needed "fixing" at once -- see
    /// <see cref="SearchV41"/>'s threshold check), the older, signature-less "2002-era" layout is
    /// tried as a last resort (see <see cref="SearchV412002"/>) -- this last one has NOT been
    /// validated against real hardware by this app (the user's own file matched V4.1 rev.2), so
    /// treat a result reporting <see cref="Algorithm.V412002"/> with extra caution.</para>
    ///
    /// <para><b>Safety:</b> <see cref="Verify"/> never modifies its input. <see cref="VerifyAndCorrect"/>
    /// only writes corrected checksum bytes into the caller's own in-memory buffer -- it never
    /// touches a file itself, and the caller decides whether/when to persist that buffer (see
    /// <see cref="Tester.WriteFlashEdc15"/>, which only does so after explicit user confirmation).
    /// If the file's size or checksum layout isn't recognized at all -- wrong size, or neither the
    /// V4.1 nor the 2002-era algorithm's expected structure matches -- both methods report
    /// <see cref="Result.Supported"/> = false and change nothing: silence, not a guess, when this
    /// code doesn't actually know how to check the file.</para>
    /// </summary>
    public static class Edc15Checksum
    {
        private const int ExpectedLength = 0x80000;

        /// <summary>Which checksum algorithm/region layout a file matched.</summary>
        public enum Algorithm
        {
            /// <summary>"V4.1 rev.1" -- signature at 0x50008, 11-region table.</summary>
            V41Rev1,

            /// <summary>"V4.1 rev.2" -- signature at 0x58008, 7-region table. The only variant
            /// validated against real hardware in this app so far -- see the type doc comment.</summary>
            V41Rev2,

            /// <summary>Older "2002-era" layout -- no fixed signature; found by scanning every
            /// 0x10000 bytes for a repeating "V4.1" marker. NOT validated against real hardware by
            /// this app.</summary>
            V412002,
        }

        /// <summary>Result of a <see cref="Verify"/> or <see cref="VerifyAndCorrect"/> call.</summary>
        public sealed class Result
        {
            /// <summary>False if this file's size or checksum layout wasn't recognized at all --
            /// <see cref="Algorithm"/>, <see cref="RegionsChecked"/> and
            /// <see cref="RegionsMismatched"/> are meaningless when this is false, and the buffer
            /// passed in was never touched.</summary>
            public bool Supported { get; init; }

            public Algorithm Algorithm { get; init; }

            public int RegionsChecked { get; init; }

            /// <summary>How many regions' stored checksum did not match a freshly-computed one
            /// (before any correction). 0 means every region already matched.</summary>
            public int RegionsMismatched { get; init; }

            /// <summary>True only when the layout was recognized AND every region's stored
            /// checksum matched the freshly-computed one.</summary>
            public bool Valid => Supported && RegionsMismatched == 0;
        }

        // "58000 was not there" in the upstream comment refers to the rev.1 table's own gap --
        // transcribed verbatim from EDC15P_checksum.tdi41_checksum_search.
        private static readonly uint[] Rev1Regions =
        {
            0x10000, 0x14000, 0x4C000, 0x50000, 0x50B80, 0x5C000,
            0x60000, 0x60B80, 0x6C000, 0x70000, 0x70B80, 0x7C000,
        };

        // From EDC15P_checksum.tdi41v2_checksum_search.
        private static readonly uint[] Rev2Regions =
        {
            0x10000, 0x14000, 0x58000, 0x58B80, 0x64000, 0x70000, 0x70B80, 0x7C000,
        };

        /// <summary>Checks <paramref name="image"/>'s stored checksums against freshly-computed
        /// ones. Never modifies <paramref name="image"/>.</summary>
        public static Result Verify(byte[] image) => Run(image, correctInPlace: false);

        /// <summary>Same check as <see cref="Verify"/>, but if the layout is recognized and any
        /// region's stored checksum is wrong, overwrites those 4 bytes in
        /// <paramref name="image"/> with the freshly-computed correct value. Does nothing to a
        /// file whose layout isn't recognized (<see cref="Result.Supported"/> = false) -- never a
        /// partial or guessed correction.</summary>
        public static Result VerifyAndCorrect(byte[] image) => Run(image, correctInPlace: true);

        private static Result Run(byte[] image, bool correctInPlace)
        {
            if (image == null || image.Length != ExpectedLength)
            {
                return new Result { Supported = false };
            }

            // Signature check order matches VAGEDCSuite's Tools.CalculateEDC15PChecksum exactly:
            // 0x50008 (rev.1) is checked first; only if that signature is ABSENT is 0x58008
            // (rev.2) checked; if NEITHER is present, the reference still defaults to trying the
            // rev.1 table first, same as here.
            var startAlgorithm = !HasV41Signature(image, 0x50008) && HasV41Signature(image, 0x58008)
                ? Algorithm.V41Rev2
                : Algorithm.V41Rev1;

            var regions = startAlgorithm == Algorithm.V41Rev2 ? Rev2Regions : Rev1Regions;
            var skipEmptyRegions = startAlgorithm == Algorithm.V41Rev2;
            var typeErrorThreshold = startAlgorithm == Algorithm.V41Rev2 ? 4 : 6;

            // SearchV41 "corrects" mismatches into whatever buffer it's given as it goes, even
            // while just detecting whether this is even the right table (matching the reference's
            // own behavior -- see its doc comment) -- so detection always happens on a scratch
            // copy first, and the caller's real buffer is only touched once, at the very end, with
            // the buffer from whichever algorithm actually recognized the file.
            var attempt = CloneOf(image);
            var (outcome, checkedCount, mismatchCount) =
                SearchV41(attempt, regions, skipEmptyRegions, typeErrorThreshold);

            if (outcome != SearchOutcome.TypeError)
            {
                if (correctInPlace)
                {
                    Array.Copy(attempt, image, image.Length);
                }

                return new Result
                {
                    Supported = true,
                    Algorithm = startAlgorithm,
                    RegionsChecked = checkedCount,
                    RegionsMismatched = mismatchCount,
                };
            }

            // Wrong table -- discard that speculative attempt and try the older 2002-era layout
            // fresh from the original bytes (mirrors Tools.cs re-reading the file from disk before
            // this fallback: the V4.1 attempt's buffer is not a trustworthy starting point once
            // it's known to be the wrong algorithm).
            var attempt2002 = CloneOf(image);
            var (outcome2002, checkedCount2002, mismatchCount2002) = SearchV412002(attempt2002);

            if (outcome2002 != SearchOutcome.TypeError)
            {
                if (correctInPlace)
                {
                    Array.Copy(attempt2002, image, image.Length);
                }

                return new Result
                {
                    Supported = true,
                    Algorithm = Algorithm.V412002,
                    RegionsChecked = checkedCount2002,
                    RegionsMismatched = mismatchCount2002,
                };
            }

            // Neither algorithm recognized this file's checksum layout -- don't claim to know
            // whether it's valid, and never touch the caller's buffer.
            return new Result { Supported = false };
        }

        private static byte[] CloneOf(byte[] source)
        {
            var copy = new byte[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static bool HasV41Signature(byte[] image, int offset) =>
            image[offset] == (byte)'V' && image[offset + 1] == (byte)'4' &&
            image[offset + 2] == (byte)'.' && image[offset + 3] == (byte)'1';

        private enum SearchOutcome { Ok, Fail, TypeError }

        /// <summary>
        /// Port of EDC15P_checksum.tdi41_checksum_search / tdi41v2_checksum_search
        /// (VAGEDCSuite) -- the two differ only in region table, whether empty (all-0xC3) regions
        /// are skipped, and the mismatch-count threshold used to decide "this is the wrong table"
        /// -- unified here into one method parameterized on those three things. Always mutates
        /// <paramref name="buffer"/> in place for any mismatch found (matching the reference, which
        /// "corrects" speculatively even while still determining whether the table is even right --
        /// see <see cref="Run"/>'s doc comment on why callers always pass a scratch copy in).
        /// </summary>
        private static (SearchOutcome outcome, int regionsChecked, int regionsMismatched) SearchV41(
            byte[] buffer, uint[] regions, bool skipEmptyRegions, int typeErrorThreshold)
        {
            ushort seedA = 0, seedB = 0;
            var firstPass = true;
            int checkedCount = 0, matchCount = 0, fixedCount = 0;

            for (var i = 0; i < regions.Length - 1; i++)
            {
                var startAddr = regions[i];
                var endAddr = regions[i + 1];
                checkedCount++;

                if (!firstPass)
                {
                    seedA |= 0x8631;
                    seedB |= 0xEFCD;
                }

                if (skipEmptyRegions && IsAllFiller(buffer, startAddr, endAddr))
                {
                    firstPass = false;
                    continue;
                }

                uint storedValue =
                    ((uint)buffer[endAddr - 1] << 24) | ((uint)buffer[endAddr - 2] << 16) |
                    ((uint)buffer[endAddr - 3] << 8) | buffer[endAddr - 4];

                var computed = Tdi41Calculate(buffer, startAddr, endAddr - 4, seedA, seedB);

                if (storedValue != computed && storedValue != 0xC3C3C3C3)
                {
                    buffer[endAddr - 4] = (byte)(computed & 0xFF);
                    buffer[endAddr - 3] = (byte)((computed >> 8) & 0xFF);
                    buffer[endAddr - 2] = (byte)((computed >> 16) & 0xFF);
                    buffer[endAddr - 1] = (byte)((computed >> 24) & 0xFF);
                    fixedCount++;
                }
                else if (storedValue == computed)
                {
                    matchCount++;
                }

                firstPass = false;
            }

            SearchOutcome outcome;
            if (fixedCount == 0) outcome = SearchOutcome.Ok;
            else if (matchCount > 3) outcome = SearchOutcome.Fail;
            else if (fixedCount >= typeErrorThreshold) outcome = SearchOutcome.TypeError;
            else outcome = SearchOutcome.Fail;

            return (outcome, checkedCount, fixedCount);
        }

        private static bool IsAllFiller(byte[] buffer, uint start, uint end)
        {
            for (var i = start; i < end - 4; i++)
            {
                if (buffer[i] != 0xC3) return false;
            }
            return true;
        }

        /// <summary>Port of EDC15P_checksum.tdi41_checksum_calculate (VAGEDCSuite) -- the
        /// core rolling-seed checksum used by both the rev.1 and rev.2 region tables. Independently
        /// validated (Python port, outside this repo) against the user's real Stock file -- all 7
        /// rev.2 regions matched exactly -- before being transcribed here.</summary>
        private static uint Tdi41Calculate(byte[] buffer, uint startAddr, uint endAddr, ushort seedA, ushort seedB)
        {
            do
            {
                byte carry = 0;
                seedA ^= (ushort)(((ushort)buffer[startAddr + 1] << 8) + buffer[startAddr]);
                startAddr += 2;

                if ((seedB & 0xF) > 0)
                {
                    var shifted = (ushort)(seedA >> (16 - (seedB & 0xF)));
                    seedA <<= (seedB & 0xF);
                    seedA |= shifted;
                    carry = (byte)(seedA & 1);
                }

                seedB -= (ushort)(((ushort)buffer[startAddr + 1] << 8) + buffer[startAddr]);
                seedB -= carry;
                startAddr += 2;
                seedB ^= seedA;

                if (startAddr == endAddr) break;

                seedA -= (ushort)(((ushort)buffer[startAddr + 1] << 8) + buffer[startAddr]);
                startAddr += 2;
                seedA += 0xDAAC;
                seedB ^= (ushort)(((ushort)buffer[startAddr + 1] << 8) + buffer[startAddr]);
                startAddr += 2;

                if ((seedA & 0xF) > 0)
                {
                    var shifted = (ushort)((seedB << (16 - (seedA & 0xF))) & 0xFFFF);
                    seedB >>= (seedA & 0xF);
                    seedB |= shifted;
                }
            }
            while (startAddr != endAddr);

            seedA -= 0x8631;
            seedA += 0xDAAC;
            seedB ^= 0xDF9B;

            return ((uint)seedB << 16) + seedA;
        }

        /// <summary>
        /// Port of EDC15P_checksum.tdi41_2002_checksum_search (VAGEDCSuite) -- the older,
        /// signature-less layout. Two fixed checksums (at 0xFFFC and 0x13FFC) plus a scan every
        /// 0x10000 bytes for a repeating "V4.1" marker (the literal bytes 0x56 0x34 0x2E 0x31),
        /// each occurrence gating 3 more region checksums. NOT validated against real hardware by
        /// this app -- see the type doc comment.
        /// </summary>
        private static (SearchOutcome outcome, int regionsChecked, int regionsMismatched) SearchV412002(byte[] buffer)
        {
            var fileSize = (uint)buffer.Length;
            int checkedCount = 2, matchCount = 0, fixedCount = 0;

            var seed1 = Tdi41_2002Calculate(buffer, 0x14000, 0x4BFFE, 0x8631, 0xEFCD, 0, 0, firstPass: true);
            var seed1Msb = (ushort)(seed1 >> 16);
            var seed1Lsb = (ushort)seed1;

            var seed2 = Tdi41_2002Calculate(buffer, 0, 0x7FFE, 0, 0, 0, 0, firstPass: true);
            var seed2Msb = (ushort)(seed2 >> 16);
            var seed2Lsb = (ushort)seed2;

            CheckAndFix(
                buffer, 0xFFFC,
                Tdi41_2002Calculate(buffer, 0x8000, 0xFFFB, seed2Lsb, seed2Msb, 0x4531, 0x3550, false),
                ref matchCount, ref fixedCount);

            CheckAndFix(
                buffer, 0x13FFC,
                Tdi41_2002Calculate(buffer, 0x10000, 0x13FFB, 0, 0, 0x8631, 0xEFCD, false),
                ref matchCount, ref fixedCount);

            uint storeAddr = 0x4FFFB;
            while (storeAddr + 5 < fileSize)
            {
                if (buffer[storeAddr + 13] == 0x56 && buffer[storeAddr + 14] == 0x34 &&
                    buffer[storeAddr + 15] == 0x2E && buffer[storeAddr + 16] == 0x31)
                {
                    var chkStart = storeAddr - 0x3FFB;
                    var chkEnd = storeAddr;
                    CheckAndFix(
                        buffer, storeAddr + 1,
                        Tdi41_2002Calculate(buffer, chkStart, chkEnd, seed1Lsb, seed1Msb, seed1Lsb, seed1Msb, false),
                        ref matchCount, ref fixedCount);

                    chkStart = storeAddr + 5;
                    chkEnd = storeAddr + 0xB80;
                    CheckAndFix(
                        buffer, storeAddr + 2945,
                        Tdi41_2002Calculate(buffer, chkStart, chkEnd, seed1Lsb, seed1Msb, seed1Lsb, seed1Msb, false),
                        ref matchCount, ref fixedCount);

                    chkStart = storeAddr + 0xB85;
                    chkEnd = storeAddr + 0xC000;
                    CheckAndFix(
                        buffer, storeAddr + 49153,
                        Tdi41_2002Calculate(buffer, chkStart, chkEnd, seed1Lsb, seed1Msb, seed1Lsb, seed1Msb, false),
                        ref matchCount, ref fixedCount);

                    checkedCount += 3;
                }

                storeAddr += 0x10000;
            }

            SearchOutcome outcome;
            if (fixedCount == 0) outcome = SearchOutcome.Ok;
            else if (matchCount > 3) outcome = SearchOutcome.Fail;
            else if (fixedCount >= checkedCount - 1) outcome = SearchOutcome.TypeError;
            else outcome = SearchOutcome.Fail;

            return (outcome, checkedCount, fixedCount);
        }

        /// <summary>Reads the 4-byte little-endian checksum stored at <paramref name="storeAddr"/>,
        /// compares it against <paramref name="computed"/>, and (matching every call site in
        /// tdi41_2002_checksum_search) overwrites it in place if different.</summary>
        private static void CheckAndFix(
            byte[] buffer, uint storeAddr, uint computed, ref int matchCount, ref int fixedCount)
        {
            uint stored =
                ((uint)buffer[storeAddr + 3] << 24) | ((uint)buffer[storeAddr + 2] << 16) |
                ((uint)buffer[storeAddr + 1] << 8) | buffer[storeAddr];

            if (stored != computed)
            {
                buffer[storeAddr] = (byte)(computed & 0xFF);
                buffer[storeAddr + 1] = (byte)((computed >> 8) & 0xFF);
                buffer[storeAddr + 2] = (byte)((computed >> 16) & 0xFF);
                buffer[storeAddr + 3] = (byte)((computed >> 24) & 0xFF);
                fixedCount++;
            }
            else
            {
                matchCount++;
            }
        }

        /// <summary>Port of EDC15P_checksum.tdi41_2002_checksum_calculate (VAGEDCSuite).</summary>
        private static uint Tdi41_2002Calculate(
            byte[] buffer, uint startAddr, uint endAddr,
            ushort seedA, ushort seedB, ushort seedC, ushort seedD, bool firstPass)
        {
            var count = startAddr / 2;
            var endCount = endAddr / 2;
            var bufferAddr = startAddr;
            uint checksum;
            ushort var1 = 0, var2 = 0;

            if (count != endCount)
            {
                var1 = seedA;
                var2 = seedB;

                if (startAddr == 0x8000)
                {
                    var1 ^= 0xD565;
                    var2 += 0x308A;
                }

                do
                {
                    var1 ^= (ushort)(((ushort)buffer[bufferAddr + 1] << 8) + buffer[bufferAddr]);
                    var shiftCount = (ushort)(var2 & 0xF);
                    count++;
                    bufferAddr += 2;
                    ushort carry = 0;

                    if ((var2 & 0xF) > 0)
                    {
                        while (shiftCount > 0)
                        {
                            carry = (ushort)(var1 >> 15);
                            var1 = (ushort)((var1 * 2) + carry);
                            shiftCount--;
                        }
                    }

                    var2 -= (ushort)(carry + ((ushort)buffer[bufferAddr + 1] << 8) + buffer[bufferAddr]);
                    var2 = (ushort)(var1 ^ var2);

                    bufferAddr += 2;
                    count++;

                    if (count > endCount) break;

                    var word = (ushort)(((ushort)buffer[bufferAddr + 1] << 8) + buffer[bufferAddr]);
                    bufferAddr += 4;
                    var1 += (ushort)(0xFFFF - word + 0xDAAD);
                    var hi = (uint)((ushort)buffer[bufferAddr - 1] << 8);
                    var2 ^= (ushort)(hi + buffer[bufferAddr - 2]);
                    var rotateCount = (ushort)(var1 & 0xF);
                    count += 2;

                    if ((var1 & 0xF) > 0)
                    {
                        while (rotateCount > 0)
                        {
                            hi = (hi | 0xFFFF) & var2;
                            hi <<= 15;
                            var2 = (ushort)((var2 >> 1) + hi);
                            rotateCount--;
                        }
                    }
                }
                while (count <= endCount);
            }

            if (startAddr == 0)
            {
                var1 -= 0x79CF;
                var2 -= 0x1033;
            }

            if (!firstPass)
            {
                var var5 = seedD;
                var1 -= seedC;
                var var6 = (ushort)((seedC | 0xFFFF) & 0xDAAD);
                var1 += (ushort)(var6 - 1);
                uint var7 = 0;

                for (var loopCount = (uint)(seedC & 0xF);
                     loopCount > 0;
                     var5 = (ushort)(((uint)var5 >> 15) + var7))
                {
                    loopCount--;
                    var7 = (var7 | 0xFFFF) & var5;
                    var7 *= 2;
                }

                checksum = (uint)var1 + (((uint)var5 ^ var2) << 16);
            }
            else
            {
                checksum = (uint)var1 + ((uint)var2 << 16);
            }

            return checksum;
        }
    }
}
