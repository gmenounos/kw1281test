using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace BitFab.KW1281Test.Cluster;

/// <summary>
/// Reads a CDC16xx ABR bank (the $8000-$FFFF window selected by I/O register $1F0F) from a
/// V599LLA-lineage cluster.
///
/// The cluster's own custom memory service ($86) takes only a 16-bit address and runs with
/// ABR=1, so addresses 0x18000 and 0x28000 both read bank 1. The custom write service ($87)
/// has no address range check, so this dumper:
///   1. writes a small 6502 stub into RAM at 0x0800 (the reset clears 0x0300-0x0AFF and nothing
///      else references 0x0700/0x0800; the 0x0B00-0x17FF pages read back as the address high
///      byte on this part and are not usable);
///   2. overwrites the custom-command dispatcher's saved continuation, the $DAD8 word pushed
///      at 01/DACD, with $07FF. The continuation is consumed by an RTS, which adds one, so
///      $07FF lands on the stub at $0800 (the dispatcher's own $DAD8 lands on the RTS at
///      $DAD9). The dispatcher pushes it immediately before the handler table jump, so it is
///      the top of stack while a custom handler runs;
///   3. the stub sets ABR, copies one 256-byte page of the selected bank from $8000-$FFFF to
///      0x0700, restores ABR, and jumps to the RTS at 01/DAD9. That RTS pops the real caller
///      return, so the stack stays balanced and the cluster resumes normally;
///   4. the buffer is read back with $86, then the next page is run.
///
/// The whole operation only uses the documented custom commands. It is invasive (one return
/// address hijack per page) and must only be used on a bench cluster. The dispatcher re-pushes
/// $DAD8 for every request, so the hijack is self-clearing; the slot is also restored explicitly
/// if a run is aborted. A wedged cluster should only need a power cycle; no EEPROM or I/O is
/// written (except ABR).
/// </summary>
internal sealed class VdoUpperBankDumper
{
    private const ushort StubAddress = 0x0800;

    /// <summary>
    /// The dispatcher's continuation is consumed by an RTS, which adds one to the popped
    /// value: it pushes $DAD8 so that the RTS lands on $DAD9. To land on the stub at
    /// <see cref="StubAddress"/>, the slot must contain the address minus one.
    /// </summary>
    private const ushort HijackValue = StubAddress - 1;

    private const ushort BufferAddress = 0x0700;
    private const ushort CounterAddress = 0x08F0;
    private const int StubPageOffset = 0x0E;     // operand high byte in "lda $page00,x"
    private const byte FirstPage = 0x80;         // $8000
    private const int PageSize = 0x100;
    private const int PageCount = 0x80;          // 32 KB window

    private const ushort StackStart = 0x0150;
    private const int StackLength = 0x00B0;      // $0150-$01FF; keys live at $0100-$014F
    private const byte MaxReadCount = 15;        // $86 sends min(DATA0, 15) body bytes

    private const int SettleMs = 40;
    private const int StubPollMs = 30;
    private const int StubTimeoutMs = 2000;

    /// <summary>
    /// The custom dispatcher pushes $DA then $D8 at 01/DACF-01/DAD2 and jumps through the
    /// command table. On the stack the pair is low=$D8, high=$DA.
    /// </summary>
    private static readonly byte[] SlotSignature = [0xD8, 0xDA];

    private readonly VdoCluster _cluster;
    private readonly Stopwatch _progress = new();

    private ushort _slot;
    private int _counter;
    private int _runStartPage;
    private int _runTotalPages;
    private TimeSpan _lastProgress;

    public VdoUpperBankDumper(VdoCluster cluster)
    {
        _cluster = cluster;
    }

    public void Dump(string filename, int bank, int startOffset)
    {
        if (bank is < 1 or > 7)
        {
            Log.WriteLine("Bank must be 1..7 (the V599 firmware uses ABR 1, 2 and 3).");
            return;
        }

        if (startOffset is < 0 or >= PageCount * PageSize)
        {
            Log.WriteLine("Start offset must be 0..0x7FFF.");
            return;
        }

        if (startOffset % PageSize != 0)
        {
            Log.WriteLine("Start offset must be page-aligned; using the containing 256-byte page.");
            startOffset &= ~(PageSize - 1);
        }

        // The $9D unlock clears $3C bit 0, the write-protect bit checked by $87. The live
        // cluster may already be unlocked; if the unlock does not ACK, continue anyway and let
        // the first write report a NAK.
        var (unlocked, softwareVersion) = _cluster.Unlock();
        if (!unlocked)
        {
            Log.WriteLine(
                $"Unlock did not ACK for '{softwareVersion}'; writes may be NAK'd. " +
                "No writes have been made yet.");
        }

        List<ushort> slots;
        try
        {
            slots = FindFrameSlots();
        }
        catch (Exception ex)
        {
            Log.WriteLine($"Could not read the stack: {ex.Message}. No writes were made.");
            return;
        }

        if (slots.Count == 0)
        {
            Log.WriteLine(
                "Could not find the custom-dispatcher continuation ($DAD8) in the stack. " +
                "No writes were made. This command currently supports V599LLA-lineage " +
                "firmware that uses the 01/DAAE custom dispatcher.");
            return;
        }

        Log.WriteLine(
            $"Candidate dispatcher slots: {string.Join(", ", slots.Select(s => $"${s:X4}"))}");

        var resume = startOffset > 0;
        if (resume && !File.Exists(filename))
        {
            Log.WriteLine($"{filename} does not exist; starting at 0x0000.");
            resume = false;
            startOffset = 0;
        }

        using var stream = new FileStream(
            filename, resume ? FileMode.Open : FileMode.Create, FileAccess.Write);

        if (resume)
        {
            var requested = startOffset;
            startOffset = (int)stream.Length;
            if (startOffset >= PageCount * PageSize)
            {
                Log.WriteLine($"{filename} already holds the full 32 KB bank.");
                return;
            }

            startOffset &= ~(PageSize - 1);
            if (startOffset != requested)
            {
                Log.WriteLine(
                    $"{filename} holds {startOffset} bytes, so resuming at 0x{startOffset:X4} " +
                    $"rather than the requested 0x{requested:X4}.");
            }

            Log.WriteLine($"Resuming {filename} at 0x{startOffset:X4}.");
        }

        stream.Seek(startOffset, SeekOrigin.Begin);

        try
        {
            if (!PrepareStubAndCounter((byte)bank))
            {
                return;
            }

            var startPage = startOffset / PageSize;
            _runStartPage = startPage;
            _runTotalPages = PageCount - startPage;
            _lastProgress = TimeSpan.Zero;
            _progress.Restart();

            Log.WriteLine(
                $"Reading ABR bank {bank} ($8000-$FFFF) into {filename} from " +
                $"0x{startOffset:X4} ({_runTotalPages} pages)...");

            if (!RunFirstPage(stream, slots, startPage))
            {
                return;
            }

            LogProgress(startPage);
            DumpRemainingPages(stream, (byte)bank, startPage + 1);
        }
        catch (Exception ex)
        {
            Log.WriteLine($"Aborting: {ex.GetType().Name}: {ex.Message}");
            Log.WriteLine(
                $"Partial image is in {filename}; resume with start offset " +
                $"0x{(int)stream.Length:X4}.");
        }
        finally
        {
            RestoreSlot();
        }
    }

    // ------------------------------------------------------------ discovery

    private List<ushort> FindFrameSlots()
    {
        var stack = ReadMemory(StackStart, StackLength);
        var slots = new List<ushort>();
        for (var i = 0; i + SlotSignature.Length <= stack.Count; i++)
        {
            if (stack[i] == SlotSignature[0] && stack[i + 1] == SlotSignature[1])
            {
                slots.Add((ushort)(StackStart + i));
            }
        }

        return slots;
    }

    // ------------------------------------------------------------ stub

    /// <summary>
    /// Saves P/A/X/Y, sets ABR, copies 256 bytes from $page00 to <see cref="BufferAddress"/>,
    /// restores ABR, increments the run counter, restores the registers and jumps to the RTS
    /// at 01/DAD9 (the dispatcher's original continuation). The original RTS then pops the
    /// real caller return, so the stack frame is consumed exactly as it would have been.
    /// </summary>
    private static List<byte> BuildStub(byte abr, byte page) =>
    [
        0x08,                               // php
        0x48,                               // pha
        0xDA,                               // phx
        0x5A,                               // phy
        0x78,                               // sei
        0xA9, abr,                          // lda #abr
        0x8D, 0x0F, 0x1F,                   // sta $1F0F
        0xA2, 0x00,                         // ldx #$00
        0xBD, 0x00, page,                   // lda $page00,x
        0x9D, (byte)(BufferAddress & 0xFF), (byte)(BufferAddress >> 8), // sta buffer,x
        0xE8,                               // inx
        0xD0, 0xF7,                         // bne lda
        0xEE, (byte)(CounterAddress & 0xFF), (byte)(CounterAddress >> 8), // inc counter
        0xA9, 0x01,                         // lda #$01
        0x8D, 0x0F, 0x1F,                   // sta $1F0F
        0x7A,                               // ply
        0xFA,                               // plx
        0x68,                               // pla
        0x28,                               // plp
        0x4C, 0xD9, 0xDA,                   // jmp $DAD9
    ];

    private bool PrepareStubAndCounter(byte abr)
    {
        var flags = ReadMemory(0x003C, 1);
        if (flags.Count == 1)
        {
            Log.WriteLine($"Write-protect $003C = ${flags[0]:X2} (bit 0 must be 0).");
        }

        var stub = BuildStub(abr, FirstPage);
        if (!WriteVerified(StubAddress, stub))
        {
            Log.WriteLine(
                "Aborting: the stub did not verify. The cluster may be write-protected " +
                "($3C bit 0); the $9D unlock is sent by VdoCluster.Unlock.");
            return false;
        }

        _cluster.CustomWriteMemory(CounterAddress, [0]);
        Thread.Sleep(SettleMs);

        var counter = ReadCounter();
        if (counter != 0)
        {
            Log.WriteLine($"Aborting: the run counter at ${CounterAddress:X4} reads {counter}.");
            return false;
        }

        return true;
    }

    private bool WriteVerified(ushort address, IReadOnlyList<byte> data)
    {
        const int maxWriteCount = 16;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            for (var off = 0; off < data.Count; off += maxWriteCount)
            {
                var count = Math.Min(maxWriteCount, data.Count - off);
                _cluster.CustomWriteMemory(
                    (ushort)(address + off), data.Skip(off).Take(count).ToList());
                Thread.Sleep(SettleMs);
            }

            var readback = ReadMemory(address, data.Count);
            if (readback.SequenceEqual(data))
            {
                return true;
            }

            Log.WriteLine(
                $"Verify failed at ${address:X4} (attempt {attempt}/2): " +
                $"expected {Utils.DumpBytes(data.Take(16))} " +
                $"got {Utils.DumpBytes(readback.Take(16))}.");
        }

        return false;
    }

    private bool PatchPage(byte page)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _cluster.CustomWriteMemory(
                (ushort)(StubAddress + StubPageOffset), [page]);
            Thread.Sleep(SettleMs);

            var readback = ReadMemory((ushort)(StubAddress + StubPageOffset), 1);
            if (readback.Count == 1 && readback[0] == page)
            {
                return true;
            }

            Log.WriteLine(
                $"Source page ${page:X2} did not verify (attempt {attempt}/3).");
        }

        return false;
    }

    // ------------------------------------------------------------ run

    private bool RunFirstPage(FileStream stream, List<ushort> slots, int pageIndex)
    {
        var page = (byte)(FirstPage + pageIndex);
        foreach (var candidate in slots)
        {
            _slot = candidate;
            Log.WriteLine($"Trying dispatcher slot ${candidate:X4}...");

            if (!PatchPage(page))
            {
                Log.WriteLine("Aborting: could not set the source page in the stub.");
                return false;
            }

            if (RunStub(1))
            {
                _counter = 1;
                Log.WriteLine($"Hijack slot ${candidate:X4} works.");
                WritePage(stream, pageIndex, page);
                return true;
            }

            Log.WriteLine($"Slot ${candidate:X4} did not run the stub; trying the next.");
            RestoreSlot();
            _slot = 0;
        }

        Log.WriteLine(
            "No candidate slot ran the stub. Nothing was dumped. Power-cycle the cluster " +
            "and re-run; if it persists, the stack layout differs from the V599LLA firmware.");
        return false;
    }

    private void DumpRemainingPages(FileStream stream, byte abr, int startPage)
    {
        for (var pageIndex = startPage; pageIndex < PageCount; pageIndex++)
        {
            var page = (byte)(FirstPage + pageIndex);
            if (!PatchPage(page))
            {
                Log.WriteLine($"Aborting at 0x{pageIndex * PageSize:X4}: page did not verify.");
                return;
            }

            var expected = (byte)(_counter + 1);
            if (!RunStub(expected))
            {
                Log.WriteLine(
                    $"Aborting at 0x{pageIndex * PageSize:X4}: stub did not run. " +
                    $"Partial image is in {stream.Name}; resume with start offset " +
                    $"0x{pageIndex * PageSize:X4}.");
                return;
            }

            _counter = expected;
            WritePage(stream, pageIndex, page);

            if (((pageIndex + 1) % 8) == 0 ||
                pageIndex == PageCount - 1 ||
                _progress.Elapsed - _lastProgress >= TimeSpan.FromSeconds(10))
            {
                LogProgress(pageIndex);
            }
        }

        Log.WriteLine(
            $"Bank {abr} complete: {PageCount * PageSize} bytes in {stream.Name}.");
    }

    private bool RunStub(int expectedCounter)
    {
        _cluster.CustomWriteMemory(
            _slot, [(byte)(HijackValue & 0xFF), (byte)(HijackValue >> 8)]);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < StubTimeoutMs)
        {
            Thread.Sleep(StubPollMs);
            if (ReadCounter() == expectedCounter)
            {
                return true;
            }
        }

        return false;
    }

    private void RestoreSlot()
    {
        if (_slot == 0)
        {
            return;
        }

        try
        {
            var current = ReadMemory(_slot, 2);
            if (current.Count == 2 &&
                current[0] == (byte)(HijackValue & 0xFF) &&
                current[1] == (byte)(HijackValue >> 8))
            {
                _cluster.CustomWriteMemory(_slot, [0xD8, 0xDA]);
                Log.WriteLine($"Restored the dispatcher continuation at ${_slot:X4}.");
            }
        }
        catch (Exception ex)
        {
            Log.WriteLine(
                $"Warning: could not restore the stack slot at ${_slot:X4} " +
                $"({ex.Message}). Power-cycle the cluster if it does not recover.");
        }
    }

    // ------------------------------------------------------------ file

    private void WritePage(FileStream stream, int pageIndex, byte page)
    {
        var data = ReadBufferVerified(PageSize);
        if (data.Count != PageSize)
        {
            throw new InvalidOperationException(
                $"Buffer read at page ${page:X2} returned {data.Count} of {PageSize} bytes.");
        }

        stream.Seek(pageIndex * PageSize, SeekOrigin.Begin);
        stream.Write([.. data]);
        stream.Flush();
    }

    /// <summary>
    /// Logs the bank offset completed, the whole-bank percentage, the pages completed in this run,
    /// elapsed time and an ETA based on the current run's per-page rate.
    /// </summary>
    private void LogProgress(int pageIndex)
    {
        var done = pageIndex - _runStartPage + 1;
        if (done <= 0 || _runTotalPages <= 0)
        {
            return;
        }

        var elapsed = _progress.Elapsed;
        _lastProgress = elapsed;
        var perPage = TimeSpan.FromTicks(elapsed.Ticks / done);
        var remaining = TimeSpan.FromTicks(perPage.Ticks * (_runTotalPages - done));
        var percent = 100.0 * (pageIndex + 1) / PageCount;

        Log.WriteLine(
            $"  0x{(pageIndex + 1) * PageSize:X4}  {percent,5:0.0}%  " +
            $"({done}/{_runTotalPages} pages)  elapsed {FormatDuration(elapsed)}  " +
            $"remaining {FormatDuration(remaining)}");
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes}:{value.Seconds:D2}";
    }

    // ------------------------------------------------------------ reads

    private byte? ReadCounter()
    {
        var bytes = _cluster.CustomReadMemory(CounterAddress, 1);
        return bytes.Count == 1 ? bytes[0] : null;
    }

    private List<byte> ReadBufferVerified(int length)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = ReadMemory(BufferAddress, length);
            var second = ReadMemory(BufferAddress, length);
            if (first.SequenceEqual(second))
            {
                return first;
            }

            Log.WriteLine(
                $"Buffer read at ${BufferAddress:X4} differed between two passes " +
                $"(attempt {attempt}/3); re-reading.");
        }

        return [];
    }

    private List<byte> ReadMemory(ushort start, int length)
    {
        var result = new List<byte>(length);
        for (var off = 0; off < length; off += MaxReadCount)
        {
            var count = Math.Min(MaxReadCount, length - off);
            var chunk = ReadChunkWithRetry((ushort)(start + off), (byte)count);
            result.AddRange(chunk.Take(count));
        }

        return result;
    }

    private List<byte> ReadChunkWithRetry(ushort address, byte count)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var chunk = _cluster.CustomReadMemory(address, count);
                if (chunk.Count == count)
                {
                    return chunk;
                }

                if (attempt >= attempts)
                {
                    throw new InvalidOperationException(
                        $"Read at ${address:X4} returned {chunk.Count} of {count} bytes.");
                }

                Log.WriteLine(
                    $"Read at ${address:X4} returned {chunk.Count} of {count} bytes; " +
                    $"retrying ({attempt}/{attempts - 1})...");
            }
            catch (Exception ex) when (
                (ex is InvalidOperationException or TimeoutException) && attempt < attempts)
            {
                Log.WriteLine(
                    $"Read at ${address:X4} failed ({ex.Message}); " +
                    $"retrying ({attempt}/{attempts - 1})...");
            }
            catch (Exception)
            {
                Log.WriteLine(
                    $"All read attempts at ${address:X4} failed. If this persists, " +
                    "power-cycle the cluster: the stub may have left the dispatcher hung.");
                throw;
            }

            Thread.Sleep(400);
        }
    }
}
