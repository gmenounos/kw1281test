using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BitFab.KW1281Test.Ccm;

/// <summary>A contiguous run of EEPROM addresses to read.</summary>
public readonly record struct CcmReadRange(int Address, int Length);

/// <summary>
/// Read plan for a comfort control module EEPROM dump.
///
/// A sequential dump of a comfort module reads 21 KiB one 8-byte block at a time, and each
/// block is a full protocol exchange - around fifteen minutes on a real car. Almost all of
/// that range is empty: the data worth having sits in five 512-byte windows. Reading only
/// those windows takes about a tenth of the time.
///
/// The map is deliberately narrow. It applies to 1C0/1J0 959 799 comfort modules only, and
/// only for a dump that starts at 0000 with the usual size; anything else falls back to a
/// sequential read, because a wrong map would quietly produce a dump full of FF.
/// </summary>
public sealed class CcmDumpPlan
{
    /// <summary>Mapped dumps end at 51FF, matching the layout CCMreader produces.</summary>
    public const int MappedFileLength = 0x5200;

    /// <summary>The 21 KiB size commonly used for sequential comfort module dumps.</summary>
    public const int LegacyFileLength = 21 * 1024;

    /// <summary>
    /// Window 1000 is the comfort module's own EEPROM. Windows 2000/3000/4000/5000 are the
    /// door modules - the same memories reachable at diagnostic addresses 42/52/62/72, one
    /// per door. A car may have four doors, two, or none at all when the module is on a
    /// bench, so a window that refuses to answer is expected, not an error.
    /// </summary>
    public static readonly IReadOnlyList<(int Window, string Controller)> DoorWindows =
    [
        (0x2000, "42"), // driver
        (0x3000, "52"), // front passenger
        (0x4000, "62"), // rear left
        (0x5000, "72"), // rear right
    ];

    /// <summary>Diagnostic address of the door whose window covers this address, else null.</summary>
    public static string? DoorControllerForAddress(int address)
    {
        foreach (var (window, controller) in DoorWindows)
        {
            if (address >= window && address < window + 0x1000)
            {
                return controller;
            }
        }
        return null;
    }

    public int StartAddress { get; }

    public int FileLength { get; }

    /// <summary>True when the five-window map is used instead of a sequential read.</summary>
    public bool IsMapped { get; }

    public IReadOnlyList<CcmReadRange> Ranges { get; }

    public int BytesToRead => Ranges.Sum(r => r.Length);

    /// <summary>
    /// Why the map was not used, or null when it was. The preconditions are easy to miss,
    /// and without this the user just sees a dump that still takes fifteen minutes.
    /// </summary>
    public string? FastReadSkippedReason { get; private init; }

    private CcmDumpPlan(int start, int length, bool mapped, params CcmReadRange[] ranges)
    {
        StartAddress = start;
        FileLength = length;
        IsMapped = mapped;
        Ranges = Array.AsReadOnly(ranges);
    }

    public static void ValidateRange(int start, int length)
    {
        if (start < 0 || start > ushort.MaxValue || length <= 0 || length > 0x10000 - start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), "EEPROM range must fit in 0000-FFFF.");
        }
    }

    /// <summary>
    /// Builds a plan for the given controller and range. Falls back to a sequential read
    /// whenever the map does not apply, and records why.
    /// </summary>
    public static CcmDumpPlan Create(string ecuInfo, int start, int length, bool preferFast)
    {
        ValidateRange(start, length);

        // 1J0 modules were added after testing a live 1J0 959 799 J. They use the same
        // windows as 1C0: the coding lives at 10B8 (window 1000, offset B8), and the door
        // modules answer at 2041 and 3041.
        bool supported = Regex.IsMatch(ecuInfo,
            @"^\s*(?:ECU:\s*)?1[CJ]0\s*959\s*799(?:[A-Z]{1,2})?(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        string? skipped =
            !preferFast ? "the fast read option is off"
            : !supported ? $"the mapped read only covers 1C0/1J0 959 799, this is \"{ecuInfo.Trim()}\""
            : start != 0 ? $"the mapped read starts at 0000, the requested start is {start:X4}"
            : length != MappedFileLength && length != LegacyFileLength
                ? $"the mapped read needs a dump size of {MappedFileLength} or {LegacyFileLength} bytes, "
                  + $"the requested size is {length}"
            : null;

        if (skipped != null)
        {
            return new CcmDumpPlan(start, length, false, new CcmReadRange(start, length))
            {
                FastReadSkippedReason = skipped
            };
        }

        return new CcmDumpPlan(0, MappedFileLength, true,
            new CcmReadRange(0x1000, 0x200),
            new CcmReadRange(0x2000, 0x200),
            new CcmReadRange(0x3000, 0x200),
            new CcmReadRange(0x4000, 0x200),
            new CcmReadRange(0x5000, 0x200));
    }
}
