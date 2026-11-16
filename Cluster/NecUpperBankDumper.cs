using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BitFab.KW1281Test.Cluster;

/// <summary>
/// Reads the upper 64 KB (0x10000-0x1FFFF) of a FIS display controller's 128 KB mask ROM.
///
/// The NEC's own memory commands ($A6 read / $A7 write) take a 16-bit address. This dumper
/// therefore:
///   1. writes a small stub into the NEC's executable PRAM (0xEE00-0xFCFF);
///   2. overwrites the display frame task's saved return address (0xEEB0 on VWK501-lineage
///      firmware) so the stub runs when the task unwinds;
///   3. the stub copies a chunk of 0x1xxxx into low RAM, which is read back with $A6.
///
/// The whole operation only uses the documented display memory service, so no special
/// hardware or firmware patch is needed. It is inherently invasive (it hijacks one return
/// address per chunk) and must only be used on a bench cluster.
///
/// The layout and the signature come from the full 128 KB disassembly:
///   0x4178 "call !!sub_3AF8" and 0x3AFC "call !!sub_50B8" push the saved return 0x3B00
///   below the caller's 0x417C, which is the signature matched here; none of the decoded
///   code in either bank references 0xF700-0xF7FF, so the stub and buffer live there
///   (0xFA00-0xFBFF holds firmware variables and 0xF880 is used by the VWK503 build).
/// Completion is confirmed by the counter at 0xF70B rather than by a fixed delay; the stub
/// increments it only after the copy, and each chunk is read back twice and must agree, because
/// the first acquisition produced one wrong byte (upper offset 0x1C44, 0x18 instead of 0x1A).
/// The saved return address is restored on the way out (only while it still reads as the
/// stub with its caller context), so a completed or aborted run does not leave the frame
/// task redirected.
/// </summary>
internal sealed class NecUpperBankDumper
{
    private const ushort StackStart = 0xEE00;
    private const int StackLength = 0xB8;    // SP starts at 0xEEB8 and grows down

    // The working layout, all inside the verified-zero 0xF700-0xF7FF page. PRAM is
    // 0xEE00-0xFCFF (executable) and IRAM above it (0xFD00-0xFEFF) is data-only.
    private const ushort ParamAddress = 0xF700;   // 16-bit upper-bank offset
    private const ushort CounterAddress = 0xF70B; // stub run counter
    private const ushort StubAddress = 0xF710;    // 45-byte stub
    private const ushort BufferAddress = 0xF740;  // copied chunk
    private const int ChunkLength = 0xC0;
    private const int SettleMs = 150;
    private const int StubPollMs = 40;            // counter poll interval
    private const int StubTimeoutMs = 1500;       // one frame-task period plus slack
    private const byte MaxReadCount = 0x10;       // main CPU accepts len < 0x11
    private const int MaxWriteCount = 2;          // NEC only protects len <= 2 with DI

    /// <summary>
    /// The display-task call chain leaves the task's saved return address (0x3B00 on
    /// VWK501-lineage FIS firmware) directly below the caller's return (0x417C).
    /// A CALL pushes PC7-0, PC15-8, then a byte whose low nibble is PC19-16.
    /// </summary>
    private static readonly byte[] FrameSignature = [0x00, 0x3B, 0x00, 0x7C, 0x41, 0x00];

    private ushort _slot;
    private ushort _resume;

    public void Dump(string filename, int startOffset)
    {
        if (startOffset is < 0 or > 0xFFFF)
        {
            Log.WriteLine("Start offset must be 0..0xFFFF (upper-bank offset).");
            return;
        }

        var (unlocked, softwareVersion) = _cluster.Unlock();
        if (!unlocked)
        {
            Log.WriteLine(
                $"Write unlock failed for '{softwareVersion}'; the NEC RAM write will be NAK'd.");
            return;
        }

        if (!FindFrameSlot())
        {
            Log.WriteLine(
                "Could not find the display-task return address in the NEC stack. " +
                "This command currently supports the VWK501-lineage FIS firmware " +
                "(saved return 0x3B00 / caller 0x417C); no writes were made.");
            return;
        }

        Log.WriteLine($"Frame-task return slot ${_slot:X4}, resume address ${_resume:X4}.");

        if (!PrepareStubAndCounter())
        {
            return;
        }

        try
        {
            DumpChunks(filename, startOffset);
        }
        finally
        {
            // The frame task re-pushes 0x3B00 every frame, so the hijack usually clears by
            // itself; restore explicitly anyway so a pending write, an abort or a stopped
            // task can never leave the cluster with a redirection into the stub.
            RestoreSlot();
        }
    }

    private void DumpChunks(string filename, int startOffset)
    {
        // The file is the authority on where an interrupted dump stopped, so a start offset
        // that disagrees with the file length can never misalign the image.
        var resume = startOffset > 0;
        if (resume && !File.Exists(filename))
        {
            Log.WriteLine($"{filename} does not exist; starting at 0x10000.");
            resume = false;
            startOffset = 0;
        }

        using var stream = new FileStream(
            filename, resume ? FileMode.Open : FileMode.Create, FileAccess.Write);
        if (resume)
        {
            var requested = startOffset;
            startOffset = (int)stream.Length;
            if (startOffset >= 0x10000)
            {
                Log.WriteLine($"{filename} already holds the full 65536 bytes.");
                return;
            }

            if (startOffset != requested)
            {
                Log.WriteLine(
                    $"{filename} holds {startOffset} bytes, so resuming at " +
                    $"0x1{startOffset:X4} rather than the requested 0x1{requested:X4}.");
            }

            stream.Seek(startOffset, SeekOrigin.Begin);
            Log.WriteLine($"Resuming {filename} at 0x1{startOffset:X4}.");
        }

        var total = 0x10000 - startOffset;
        Log.WriteLine(
            $"Reading upper NEC ROM 0x1{startOffset:X4}-0x1{startOffset + total - 1:X4} " +
            $"into {filename}");

        var chunk = Math.Min(ChunkLength, total);
        List<byte>? stub = null;
        var builtLength = -1;
        var counter = 0;
        var written = 0;

        for (var offset = startOffset; offset < startOffset + total; offset += chunk)
        {
            var count = Math.Min(chunk, startOffset + total - offset);

            if (count != builtLength)
            {
                stub = BuildStub(count);
                if (!WriteVerified(StubAddress, stub))
                {
                    Log.WriteLine("Aborting: stub did not verify.");
                    return;
                }

                builtLength = count;
            }

            if (!WriteParameter((ushort)offset))
            {
                Log.WriteLine($"Aborting at 0x1{offset:X4}: parameter did not verify.");
                return;
            }

            var expected = (byte)((counter + 1) & 0xFF);
            if (!RunStub(expected))
            {
                Log.WriteLine(
                    $"Aborting at 0x1{offset:X4}: stub did not run. " +
                    $"Partial image ({written} bytes) is in {filename}; " +
                    $"resume with start offset 0x{offset:X4}.");
                return;
            }

            counter = expected;

            var data = ReadBufferVerified(count);
            if (data.Count != count)
            {
                Log.WriteLine(
                    $"Aborting at 0x1{offset:X4}: read returned {data.Count} of {count} bytes. " +
                    $"Partial image ({written} bytes) is in {filename}; " +
                    $"resume with start offset 0x{offset:X4}.");
                return;
            }

            stream.Write([.. data]);
            stream.Flush();
            written += data.Count;

            if ((offset & 0x7FF) == 0)
            {
                Log.WriteLine($"  0x1{offset:X4} done ({written} bytes)");
            }
        }

        Log.WriteLine(
            $"Upper NEC ROM complete: {written} bytes read this run, file is " +
            $"{stream.Position} bytes in {filename}.");
    }

    // ------------------------------------------------------------ discovery

    private bool FindFrameSlot()
    {
        var stack = ReadMemory(StackStart, StackLength);
        for (var i = 0; i + FrameSignature.Length <= stack.Count; i++)
        {
            var match = true;
            for (var j = 0; j < FrameSignature.Length; j++)
            {
                if (stack[i + j] != FrameSignature[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            _slot = (ushort)(StackStart + i);
            _resume = (ushort)(stack[i] | (stack[i + 1] << 8) | ((stack[i + 2] & 0x0F) << 16));
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------ stub

    private bool PrepareStubAndCounter()
    {
        // A store to internal ROM is ignored, but leaves RP2 = 0x0002 so the following
        // writes use the interrupt-protected block-copy path rather than the byte loop.
        Prime();
        if (!WriteVerified(CounterAddress, [0x00]))
        {
            Log.WriteLine("Aborting: could not initialise the stub run counter.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from 0x1xxxx into the buffer, then increments a
    /// RAM counter and returns to the interrupted task. The source offset is read from
    /// <see cref="ParamAddress"/>; the counter is written only after the copy completes, so a
    /// changed counter means the buffer is ready to read.
    /// </summary>
    private List<byte> BuildStub(int length)
    {
        var b = new List<byte>
        {
            0x09, 0x8F,                                                // PUSH WHL
            0x09, 0x8D,                                                // PUSH TDE
            0x3E, 0xE8, 0x00, 0xF7,                                    // MOVW HL,!0xF700
            0x65, (byte)(BufferAddress & 0xFF), (byte)(BufferAddress >> 8), // MOVW DE,#buffer
            0x07, 0x67, 0x01,                                          // MOV W,#1
            0x07, 0x65, 0x00,                                          // MOV T,#0
            0x3A, 0xF2, (byte)length,                                  // MOV 0xFDF2,#length
            0x4A,                                                      // DI
            0x5D,                                                      // loop: MOV A,[WHL]
            0x54,                                                      //       MOV [TDE],A
            0x47,                                                      //       INCW HL
            0x46,                                                      //       INCW DE
            0x3B, 0xF2, 0xF9,                                          //       DBNZ 0xFDF2,loop
            0x4B,                                                      // EI
            0x09, 0x9D,                                                // POP TDE
            0x09, 0x9F,                                                // POP WHL
            0x3E, 0x10, 0x0B, 0xF7,                                    // MOV A,!0xF70B
            0xC1,                                                      // INC A
            0x3E, 0x11, 0x0B, 0xF7,                                    // MOV !0xF70B,A
            0x2C, (byte)(_resume & 0xFF), (byte)(_resume >> 8),        // BR !resume
        };
        return b;
    }

    // ------------------------------------------------------------ chunk run

    private bool RunStub(byte expectedCounter)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Prime();
            WriteSlot();

            if (WaitForCounter(expectedCounter, out var last))
            {
                return true;
            }

            Log.WriteLine(
                $"Stub run not confirmed (counter ${last:X2}, expected ${expectedCounter:X2}); " +
                $"re-finding the slot (attempt {attempt}/2)...");

            if (!FindFrameSlot())
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The $A7 acknowledgement is sent before the frame task unwinds, so the counter the stub
    /// increments is the real completion signal. Poll it instead of sleeping a fixed time: a
    /// fast frame resumes immediately, and a slow one still gets the full timeout.
    /// </summary>
    private bool WaitForCounter(byte expectedCounter, out byte last)
    {
        last = 0xFF;
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < StubTimeoutMs)
        {
            Thread.Sleep(StubPollMs);

            var counter = ReadMemory(CounterAddress, 1);
            if (counter.Count != 1)
            {
                continue;
            }

            last = counter[0];
            if (last == expectedCounter)
            {
                return true;
            }
        }

        return false;
    }

    private void WriteSlot()
    {
        // Bits 2-0 rp codes are permuted: 0x65 sets DE. The stub address is < 0x10000, so
        // the PC19-16 byte stays 0; the task's own CALL pushes 0x00 there every time.
        _cluster.CustomWriteNecRam(
            _slot, [(byte)(StubAddress & 0xFF), (byte)(StubAddress >> 8)]);
        Thread.Sleep(SettleMs);
    }

    /// <summary>
    /// Undoes a pending hijack. The slot belongs to the frame task only while the task is
    /// inside the 0x3AF8/0x50B8 chain, so the full six-byte context must still read
    /// [stub][0x00][caller 0x417C]; if the task has already re-pushed 0x3B00 (or another
    /// frame owns the slot), the bytes are left alone.
    /// </summary>
    private void RestoreSlot()
    {
        byte[] hijacked =
        [
            (byte)(StubAddress & 0xFF), (byte)(StubAddress >> 8),
            FrameSignature[2], FrameSignature[3], FrameSignature[4], FrameSignature[5],
        ];
        byte[] resumeBytes = [(byte)(_resume & 0xFF), (byte)(_resume >> 8)];

        try
        {
            var current = ReadMemory(_slot, hijacked.Length);
            if (!current.SequenceEqual(hijacked))
            {
                return;
            }

            Prime();
            _cluster.CustomWriteNecRam(_slot, resumeBytes);
            Thread.Sleep(SettleMs);

            var readback = ReadMemory(_slot, resumeBytes.Length);
            if (readback.SequenceEqual(resumeBytes))
            {
                Log.WriteLine($"Return address at ${_slot:X4} restored to ${_resume:X4}.");
            }
            else
            {
                Log.WriteLine(
                    $"Warning: return address at ${_slot:X4} reads " +
                    $"{Utils.DumpBytes(readback)} after the restore attempt.");
            }
        }
        catch (Exception ex)
        {
            Log.WriteLine(
                $"Warning: could not restore the return address at ${_slot:X4} " +
                $"({ex.Message}). Power-cycle the cluster if the display does not recover.");
        }
    }

    // ------------------------------------------------------------ writes

    private void Prime()
    {
        // Internal ROM ignores the store; the service still sets RP2 to the address.
        _cluster.CustomWriteNecRam(0x0002, [0x55]);
        Thread.Sleep(SettleMs);
    }

    private bool WriteParameter(ushort offset)
    {
        byte[] bytes = [(byte)(offset & 0xFF), (byte)(offset >> 8)];
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _cluster.CustomWriteNecRam(ParamAddress, bytes);
            Thread.Sleep(SettleMs);

            var readback = ReadMemory(ParamAddress, 2);
            if (readback.SequenceEqual(bytes))
            {
                return true;
            }

            Log.WriteLine($"Parameter 0x{offset:X4} did not verify (attempt {attempt}/3).");
        }

        return false;
    }

    private bool WriteVerified(ushort address, IReadOnlyList<byte> data)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            for (var off = 0; off < data.Count; off += MaxWriteCount)
            {
                var count = Math.Min(MaxWriteCount, data.Count - off);
                _cluster.CustomWriteNecRam(
                    (ushort)(address + off), [.. data.Skip(off).Take(count)]);
                Thread.Sleep(SettleMs);
            }

            var readback = ReadMemory(address, data.Count);
            if (readback.SequenceEqual(data))
            {
                return true;
            }

            Log.WriteLine($"Verify failed at ${address:X4} (attempt {attempt}/2).");
        }

        return false;
    }

    // ------------------------------------------------------------ reads

    /// <summary>
    /// Reads the copied chunk twice and returns it only if both passes agree. One bad byte in
    /// the first acquisition (upper offset 0x1C44, read as 0x18 instead of 0x1A) showed that a
    /// single pass is not enough to trust.
    /// </summary>
    private List<byte> ReadBufferVerified(int length)
    {
        var empty = new List<byte>();
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

        return empty;
    }

    private List<byte> ReadMemory(ushort start, int length)
    {
        var result = new List<byte>(length);
        for (var off = 0; off < length; off += MaxReadCount)
        {
            var count = Math.Min(MaxReadCount, length - off);
            var address = (ushort)(start + off);
            var chunk = ReadChunkWithRetry(address, (byte)count);
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
                return _cluster.CustomReadNecRom(address, count);
            }
            catch (Exception ex) when (
                (ex is InvalidOperationException or TimeoutException) && attempt < attempts)
            {
                Log.WriteLine(
                    $"Read at ${address:X4} failed ({ex.Message}); " +
                    $"retrying ({attempt}/{attempts - 1})...");
                Thread.Sleep(400);
            }
            catch (Exception)
            {
                Log.WriteLine(
                    $"All read attempts at ${address:X4} failed. If this persists, " +
                    "power-cycle the cluster: a redirected NEC write may have left the " +
                    "display firmware hung.");
                throw;
            }
        }
    }

    private readonly VdoCluster _cluster;

    public NecUpperBankDumper(VdoCluster cluster)
    {
        _cluster = cluster;
    }
}
