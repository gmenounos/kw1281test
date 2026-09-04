using BitFab.KW1281Test.Blocks;
using BitFab.KW1281Test.Kwp2000;
using BitFab.KW1281Test.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test
{
    /// <summary>
    /// Which protocol a controller answered the 5-baud wakeup with. KW1281 and KWP2000 are the
    /// two VAG K-line diagnostic protocols; a given ECU speaks exactly one, decided by the keyword
    /// bytes it returns during wakeup (see <see cref="IKwpCommon.WakeUp"/>).
    /// </summary>
    internal enum EcuProtocol
    {
        Kw1281,
        Kwp2000,
    }

    /// <summary>
    /// The result of a protocol-detecting wakeup (<see cref="Tester.WakeUpAny"/>): exactly one of
    /// <see cref="Kw1281"/> / <see cref="Kwp2000"/> is non-null, per <see cref="Protocol"/>. Lets a
    /// single command implementation branch on which protocol actually answered instead of the app
    /// carrying a duplicate "*kwp" command per diagnostic.
    /// </summary>
    internal readonly struct EcuSession
    {
        public EcuProtocol Protocol { get; }

        /// <summary>Non-null when <see cref="Protocol"/> is Kw1281 -- the connected controller info.</summary>
        public ControllerInfo? Kw1281 { get; }

        /// <summary>Non-null when <see cref="Protocol"/> is Kwp2000 -- the live dialog (already in a session).</summary>
        public IKW2000Dialog? Kwp2000 { get; }

        private EcuSession(EcuProtocol protocol, ControllerInfo? kw1281, IKW2000Dialog? kwp2000)
        {
            Protocol = protocol;
            Kw1281 = kw1281;
            Kwp2000 = kwp2000;
        }

        public static EcuSession ForKw1281(ControllerInfo info) =>
            new(EcuProtocol.Kw1281, info, null);

        public static EcuSession ForKwp2000(IKW2000Dialog dialog) =>
            new(EcuProtocol.Kwp2000, null, dialog);

        public bool IsKwp2000 => Protocol == EcuProtocol.Kwp2000;
    }

    internal partial class Tester
    {
        /// <summary>
        /// Wakes the controller and reports which protocol it speaks, WITHOUT throwing on the
        /// "wrong" one -- unlike <see cref="Kwp1281Wakeup"/> (KW1281-only) and
        /// <see cref="Kwp2000Wakeup"/> (KWP2000-only). This is the single entry point the unified
        /// diagnostic commands use: the same `readfaultcodes` / `clearfaultcodes` / `adaptation*` /
        /// `setsoftwarecoding` / `login` command works on either protocol by branching on the
        /// returned <see cref="EcuSession"/>.
        ///
        /// Parity: like <see cref="Kwp2000Wakeup"/>, a null <paramref name="evenParityWakeup"/>
        /// tries odd then even 5-baud address parity, because on real hardware an ECU can answer
        /// under one parity and be silent under the other, and parity does NOT by itself imply the
        /// protocol (a dual-protocol ECU may report KW1281 under one and KWP2000 under the other).
        /// The first parity that yields a valid keyword wins.
        /// </summary>
        public EcuSession WakeUpAny(bool? evenParityWakeup = null, Func<bool>? isStopRequested = null,
            bool tryFastInit = false)
        {
            var parities = evenParityWakeup.HasValue
                ? new[] { evenParityWakeup.Value }
                : new[] { false, true };

            // Optional ISO 14230 fast init first, opt-in per command (only the group read enables it,
            // for a later CAN-init EDC16 that ignores a cold 5-baud slow init). It targets header
            // 0x10, so it is limited to the engine wakeup address; a miss falls through to the slow
            // init below. Off by default: an EDC15 also answers at 0x01 but must not be fast-inited.
            if (tryFastInit && (byte)_controllerAddress == 0x01)
            {
                var fastVersion = 0;
                for (var a = 0; a < 4; a++)
                {
                    try { fastVersion = _kwpCommon!.TryFastInit(0x10); }
                    catch (Exception) { fastVersion = 0; }
                    if (fastVersion >= 2000) break;
                }
                if (fastVersion >= 2000)
                {
                    Log.WriteLine($"Fast init connected (KW {fastVersion}).");
                    Thread.Sleep(300);
                    return EcuSession.ForKwp2000(CreateKwp2000Dialog(fastVersion));
                }
                Log.WriteLine("Fast init got no response; using slow init...");
            }

            for (var i = 0; i < parities.Length; i++)
            {
                var evenParity = parities[i];
                var isLastAttempt = i == parities.Length - 1;

                Log.WriteLine($"Sending wakeup message{(evenParity ? " (even parity)" : parities.Length > 1 ? " (odd parity)" : "")}");

                int kwpVersion;
                try
                {
                    kwpVersion = _kwpCommon!.WakeUp((byte)_controllerAddress, evenParity, failQuietly: !isLastAttempt, isStopRequested);
                }
                catch (UnableToProceedException) when (!isLastAttempt)
                {
                    // No response under this parity -- try the other before giving up.
                    continue;
                }

                if (kwpVersion == 1281)
                {
                    var ecuInfo = _kwp1281.Connect();
                    Log.WriteLine($"ECU: {ecuInfo}");
                    return EcuSession.ForKw1281(ecuInfo);
                }

                // A settle delay between the end of the 5-baud handshake and the first framed
                // request -- see Kwp2000Wakeup for why this is needed on real hardware.
                Thread.Sleep(300);
                var dialog = CreateKwp2000Dialog(kwpVersion);
                return EcuSession.ForKwp2000(dialog);
            }

            // Unreachable: the loop always returns or throws on its last iteration.
            throw new UnableToProceedException();
        }

        // ----------------------------------------------------------------------------------------
        // Unified diagnostic work-parts. Each takes an already-woken EcuSession and dispatches to
        // the KW1281 method (unchanged behavior) or the KWP2000 equivalent. CommandRunner calls
        // these instead of the protocol-specific methods so one command covers both protocols.
        // ----------------------------------------------------------------------------------------

        public void ReadIdentAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                ReadIdent();
                return;
            }

            var response = session.Kwp2000!.ReadEcuIdentification(0x9B);
            Log.WriteLine($"ECU identification: {Utils.DumpBytes(response)}");
            Log.WriteLine($"  As text: {FormatKwp2000Identity(response)}");
            // Decode the binary fields into readable numbers (coding is the 3-byte value at offset
            // 18; the 6-byte workshop code follows at offset 21). EDC16 coding is shown 7-digit,
            // zero-padded.
            if (response.Length >= 27 && response[0] == 0x9B)
            {
                var coding = (response[18] << 16) | (response[19] << 8) | response[20];
                var (wsc, importer, equipment) = DecodeKwp2000Wsc(response.Skip(21).Take(6).ToArray());
                Log.WriteLine($"  Coding: {coding.ToString("D7")} (0x{coding:X6})");
                Log.WriteLine($"  Workshop code (WSC): {wsc}   Importer: {importer}   Equipment: {equipment}");
            }
        }

        public List<FaultCode>? ReadFaultCodesAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                ReadFaultCodes();   // KW1281 path prints the codes
                return null;
            }

            StartDefaultDiagSession(session.Kwp2000!);
            var faultCodes = session.Kwp2000!.ReadFaultCodes();
            Log.WriteLine("Fault codes:");
            foreach (var faultCode in faultCodes)
            {
                Log.WriteLine($"    {faultCode}");
            }
            return faultCodes;
        }

        public void ClearFaultCodesAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                ClearFaultCodes();
                return;
            }

            StartDefaultDiagSession(session.Kwp2000!);
            session.Kwp2000!.ClearFaultCodes();
            Log.WriteLine("Fault codes cleared.");
        }

        public void GroupReadAny(EcuSession session, byte groupNumber)
        {
            if (!session.IsKwp2000)
            {
                GroupRead(groupNumber);
                return;
            }

            // Match the KW1281 GroupRead loop (upstream parity): browse groups with Up/Down and quit
            // with Q, continuously re-reading and overlaying the current group's values in place until
            // the user quits -- the same interactive behaviour the KW1281 path already has, so the
            // KWP2000 command now behaves identically.
            var d = session.Kwp2000!;
            Log.WriteLine("Sending Group Read blocks...");
            Log.WriteLine("[Up arrow | Down arrow | Q to quit]", LogDest.Console);

            // Human-readable only: suppress the per-frame Sent/RX/Received trace for the
            // interactive session (like Basic Setting / adaptation) so the overlay shows just the
            // decoded values, not raw K-line bytes. Restored in the finally.
            var prevQuiet = d.QuietFrames;
            d.QuietFrames = true;
            try
            {
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var keyInfo = Console.ReadKey(intercept: true);
                        if (keyInfo.Key == ConsoleKey.UpArrow)
                        {
                            if (groupNumber < 255)
                            {
                                groupNumber++;
                            }
                        }
                        else if (keyInfo.Key == ConsoleKey.DownArrow)
                        {
                            if (groupNumber > 1)
                            {
                                groupNumber--;
                            }
                        }
                        else if (keyInfo.Key == ConsoleKey.Q)
                        {
                            break;
                        }
                    }

                    List<SensorValue> values;
                    try
                    {
                        values = ReadKwp2000MeasuringBlock(d, groupNumber);
                    }
                    catch
                    {
                        values = new List<SensorValue>();
                    }

                    if (values.Count == 0)
                    {
                        GroupReadOverlay($"Group {groupNumber:D3}: Not Available");
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var value in values)
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append(" | ");
                            }
                            sb.Append(value.ToString());
                        }
                        GroupReadOverlay($"Group {groupNumber:D3}: {sb}");
                    }
                }
            }
            finally
            {
                d.QuietFrames = prevQuiet;
            }
            Log.WriteLine(LogDest.Console);
        }

        // In-place console overlay for the interactive group-read / basic-setting loops -- mirrors
        // KW1281Dialog.Overlay so the KWP2000 commands refresh a single line instead of scrolling a
        // new line per read. The current line is cleared and rewritten on the console (no newline);
        // the file log still records one line per refresh.
        private static void GroupReadOverlay(string message)
        {
            (int left, int top) = Console.GetCursorPosition();
            Console.SetCursorPosition(0, top);
            if (left > 0)
            {
                Log.Write(new string(' ', left), LogDest.Console);
                Console.SetCursorPosition(0, top);
            }
            Log.Write(message, LogDest.Console);
            Log.WriteLine(message, LogDest.File);
        }

        /// <summary>
        /// Reads one VAG measuring block over KWP2000 (readDataByLocalIdentifier 0x21, group number
        /// as the local identifier) and decodes it into <see cref="SensorValue"/> cells. The 0x21
        /// response body is the SAME 3-byte (formula, A, B) triple stream KW1281 group reads use --
        /// groups 1-3 decode byte-for-byte with the existing <see cref="SensorValue"/> formula table
        /// (formula 1 = rpm, 5 = coolant degC, 23/33 = %, 49 = mg, etc.). So no ECU-specific scaling
        /// catalog is needed; the ECU sends the display formula index per cell exactly as in KW1281.
        ///
        /// The response body is [localIdentifier echo][cell triples...], padded to a fixed 8 cells
        /// with 0x25 00 00 markers (formula 0x25 is unused). The leading echo byte is dropped and
        /// trailing 0x25-pad cells are trimmed, so this returns only the real cells (4 for groups
        /// 1-3).
        /// </summary>
        public List<SensorValue> ReadKwp2000MeasuringBlock(IKW2000Dialog dialog, byte groupNumber)
        {
            var body = dialog.ReadDataByLocalIdentifier(groupNumber);

            // Body starts with the local-identifier echo (== groupNumber); drop it so what remains
            // is a whole number of 3-byte cells (body length == echo + 3*cells, so length % 3 == 1).
            var cellBytes = body;
            if (cellBytes.Length % 3 == 1)
            {
                cellBytes = body.Skip(1).ToArray();
            }

            var values = new List<SensorValue>();
            for (var i = 0; i + 2 < cellBytes.Length; i += 3)
            {
                values.Add(new SensorValue(cellBytes[i], cellBytes[i + 1], cellBytes[i + 2]));
            }

            // Trim the fixed-record padding: unused slots come back as formula 0x25 (37), which is
            // not a real VAG measuring-block formula. Only trailing pad cells are removed, so a group
            // with fewer than 8 real cells keeps exactly its real ones.
            while (values.Count > 0 && values[values.Count - 1].SensorID == 0x25)
            {
                values.RemoveAt(values.Count - 1);
            }

            return values;
        }

        /// <summary>
        /// Reads several measuring blocks in one call, protocol-agnostically -- the shape Live Data
        /// Logging consumes. KW1281 delegates to <see cref="GroupReadMany"/> (unchanged); KWP2000
        /// reads each group via <see cref="ReadKwp2000MeasuringBlock"/>. A group that throws or comes
        /// back empty yields a null value list for that entry (same convention as GroupReadMany), so
        /// one bad group doesn't sink the whole logged row.
        /// </summary>
        public List<(byte GroupNumber, List<SensorValue>? Values)> GroupReadManyAny(
            EcuSession session, IReadOnlyList<byte> groupNumbers)
        {
            if (!session.IsKwp2000)
            {
                return GroupReadMany(groupNumbers);
            }

            var results = new List<(byte, List<SensorValue>?)>();
            foreach (var groupNumber in groupNumbers)
            {
                try
                {
                    results.Add((groupNumber, ReadKwp2000MeasuringBlock(session.Kwp2000!, groupNumber)));
                }
                catch
                {
                    results.Add((groupNumber, null));
                }
            }
            return results;
        }

        /// <summary>
        /// Keeps a woken session alive without requesting data, protocol-agnostically. KW1281 sends
        /// the KWP1281 keep-alive ACK exchange (<see cref="KeepAlive"/>); KWP2000 sends testerPresent
        /// (0x3E, no sub -- idle keep-alive `81 10 F1 3E`).
        /// </summary>
        public void KeepAliveAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                KeepAlive();
                return;
            }
            _ = session.Kwp2000!.SendReceive(Service.testerPresent, Array.Empty<byte>());
        }

        /// <summary>
        /// The controller's identification text, protocol-agnostically -- used for measuring-block
        /// label-file matching. KW1281 uses the wakeup ident string; KWP2000 reads
        /// readEcuIdentification 0x9B (part number + software ID) and returns its ASCII.
        /// </summary>
        public string IdentityTextAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                return session.Kw1281?.AsciiText ?? "";
            }
            return FormatKwp2000Identity(session.Kwp2000!.ReadEcuIdentification(0x9B));
        }

        /// <summary>
        /// Formats a KWP2000 readEcuIdentification(0x9B) record into human-readable text. The raw
        /// record interleaves ASCII fields with BINARY fields (a 0x03 marker, the 3-byte coding and
        /// the 6-byte workshop code), so dumping the whole record as ASCII renders those binary
        /// bytes as garbage. This extracts only the readable fields -- the 12-char part number, the
        /// 4-char software version and the trailing engine/description text -- skipping the binary
        /// middle. Layout (body incl. the leading 0x9B option byte): 9B | part#(12) | SW(4) | 03 |
        /// coding(3) | WSC(6) | description... . Falls back to a printable-only, whitespace-collapsed
        /// dump when the record is short or does not start with 0x9B.
        /// </summary>
        public static string FormatKwp2000Identity(byte[] record)
        {
            static string Printable(byte[] bytes, int start, int count)
            {
                var end = Math.Min(start + count, bytes.Length);
                var sb = new System.Text.StringBuilder();
                var lastSpace = false;
                for (var i = start; i < end; i++)
                {
                    var isPrintable = bytes[i] >= 0x20 && bytes[i] < 0x7F;
                    var c = isPrintable ? (char)bytes[i] : ' ';
                    var isSpace = c == ' ';
                    if (isSpace && (lastSpace || sb.Length == 0)) { lastSpace = true; continue; }
                    sb.Append(c);
                    lastSpace = isSpace;
                }
                var s = sb.ToString();
                return s.Length > 0 && s[s.Length - 1] == ' ' ? s.Substring(0, s.Length - 1) : s;
            }

            if (record.Length >= 27 && record[0] == 0x9B)
            {
                var partNumber = Printable(record, 1, 12);
                var software = Printable(record, 13, 4);
                var description = Printable(record, 27, record.Length - 27);
                var sb = new System.Text.StringBuilder(partNumber);
                if (software.Length > 0) sb.Append("  SW ").Append(software);
                if (description.Length > 0) sb.Append("  ").Append(description);
                return sb.ToString();
            }

            return Printable(record, 0, record.Length);
        }

        public void ResetAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                Reset();
                return;
            }

            session.Kwp2000!.EcuReset(0x01); // ISO14230-3 ecuReset, subfunction 0x01 (powerOn reset)
            Log.WriteLine("ECU reset.");
        }

        public void SetSoftwareCodingAny(EcuSession session, int softwareCoding, int workshopCode)
        {
            if (!session.IsKwp2000)
            {
                SetSoftwareCoding(softwareCoding, workshopCode);
                return;
            }

            // KWP2000 (EDC16) variant coding is a read-modify-write of the writeDataByLocalIdentifier
            // 0x9A record: the 6-byte WORKSHOP CODE (WSC) and 4-byte software-version string are
            // echoed from the current 1A 9B identification, with only the 3-byte coding replaced
            // (the WSC is left untouched). NOTE: the workshopCode ARGUMENT is not yet written -- the
            // 6-byte WSC field is preserved as-is, so this changes coding only. Writing a new WSC
            // needs its 6-byte encoding confirmed first.
            var (workshopCode6, sw) = ReadKwp2000CodingRecordFields(session.Kwp2000!);
            var coding = new[]
            {
                (byte)((softwareCoding >> 16) & 0xFF),
                (byte)((softwareCoding >> 8) & 0xFF),
                (byte)(softwareCoding & 0xFF),
            };
            var record = BuildKwp2000CodingRecord(workshopCode6, sw, coding);
            _ = session.Kwp2000!.WriteDataByLocalIdentifier(0x9A, record);
            Log.WriteLine($"Software coding set to {softwareCoding} (0x{softwareCoding:X6}) via KWP2000 0x9A record.");
            Thread.Sleep(3000); // EDC16 re-initialises after a coding write -- settle before returning.
        }

        /// <summary>
        /// KWP2000 coding write that also sets the workshop code (WSC / importer / equipment). Builds
        /// the 0x9A record with a freshly-encoded WSC field (see <see cref="EncodeKwp2000Wsc"/>) plus
        /// the new 3-byte coding, echoing only the software-version string from the current 1A 9B.
        /// Used by the ECU tab's 3-field workshop-code editor. KWP2000 only.
        /// </summary>
        public void SetKwp2000Coding(EcuSession session, int softwareCoding, int wsc, int importer, int equipment)
        {
            if (!session.IsKwp2000)
            {
                throw new InvalidOperationException("SetKwp2000Coding requires a KWP2000 session.");
            }
            var (_, sw) = ReadKwp2000CodingRecordFields(session.Kwp2000!);
            var wscBytes = EncodeKwp2000Wsc(wsc, importer, equipment);
            var coding = new[]
            {
                (byte)((softwareCoding >> 16) & 0xFF),
                (byte)((softwareCoding >> 8) & 0xFF),
                (byte)(softwareCoding & 0xFF),
            };
            var record = BuildKwp2000CodingRecord(wscBytes, sw, coding);
            Log.WriteLine($"KWP2000 coding write: coding {softwareCoding} (0x{softwareCoding:X6}), " +
                          $"WSC {wsc}/{importer}/{equipment}. Record: 3B 9A {Utils.DumpBytes(record)}");
            _ = session.Kwp2000!.WriteDataByLocalIdentifier(0x9A, record);
            Log.WriteLine("Coding + workshop code written.");
            // After a coding write the EDC16 briefly resets/re-initialises and will not answer a
            // fresh wakeup for ~2-3 s. Settle before returning so an immediately-following operation
            // (e.g. Read Identity) doesn't hit a dead bus, hang, and get force-stopped.
            Thread.Sleep(3000);
        }

        // KWP2000 output test and basic setting use the SAME startRoutineByLocalIdentifier (0x31)
        // B8/B9/BA family as adaptation, distinguished only by a 2-byte selector after the routine
        // id:
        //   adaptation    -> 01 03   (B8 enter, B9 select/value, BA read, BB save)
        //   output test   -> 01 02   (B8 enter, B9 advance-to-next-actuator, BA read status)
        //   basic setting -> 00 <grp>(B8 enter, BA poll values); grp = 0x03 in the capture
        private static readonly byte[] OutputTestSub = { 0x01, 0x02 };

        // Output-test ID -> name, keyed by the VAG 5-digit device number the ECU reports as each
        // output's code (the code is that component number in hex, e.g. 0x04F1 = 01265). A union
        // across EDC15 / EDC16 P350 / EDC16 P475 whose codes do not collide, so an ID self-selects
        // its family. Names are decoded from firmware the project owns (A2L / DAMOS); an
        // unrecognised code shows its VAG number instead of a name.
        private static readonly Dictionary<int, string> Edc16OutputTestNames = new()
        {
            [0x0243] = "N75 Valve (Boost Control)",
            [0x0270] = "A/C Compressor Intervention",
            [0x0272] = "Glow Plug / Diagnostic Lamp",
            [0x02EE] = "Malfunction Indicator Lamp (K83)",
            [0x0403] = "EGR Valve",
            [0x0404] = "GER Output Stage",
            [0x0480] = "Cooling Fan 1 Control",
            [0x04A9] = "Low Output Coolant Heater Relay",
            [0x04AA] = "High Output Coolant Heaters Relay",
            [0x04D5] = "Fuel Shut-Off Valve (N109 / ELAB)",
            [0x04EE] = "N75 Valve (Boost Control)",
            [0x04F1] = "EGR Valve",
            [0x04F2] = "Glow Plug Relay (J52)",
            [0x04F5] = "Injection Timing Solenoid (N108)",
            [0x0684] = "Glow Plug Control Module",
            [0x09E6] = "Cooling Fan 1 Control",
            [0x09E8] = "EGR Cooler Bypass Valve",
            [0x1495] = "EGR Cooler Bypass Valve",
            [0x1586] = "Engine Torque (CAN Output)",
            [0x1616] = "Glow Plug Light",
            [0x162D] = "Diesel Particulate Filter Light",
            [0x1690] = "Check Engine Light",
            [0x1937] = "Alternator Excitation Relay",
            [0x3003] = "Low Output Coolant Heater Relay",
            [0x3005] = "High Output Coolant Heaters Relay",
            [0x3100] = "Intake Flap",
            [0x3372] = "Variable-Swirl Actuator",
            [0x42AC] = "Glow Plug Control Module",
            [0x46B4] = "Check Engine Light",
            [0x4808] = "Variable-Swirl Actuator",
            [0x4C64] = "Intake Flap",
        };

        // The ECU reports this code once the output-test list has run past its last real output
        // (an end-of-list marker, not an actuator). Stop when we see it.
        private const int Edc16OutputTestEndCode = 0x04AB;

        public void ActuatorTestAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                // KW1281 cluster/engine actuator test: each actuator names itself from the block's
                // own id -> name table (ActuatorTestResponseBlock).
                ActuatorTest();
                return;
            }

            // Output test: 31 B8 01 02 enter, then step through actuators (31 B9 01 02 advances to
            // the next, 31 BA 01 02 reads its status), 32 B8 01 02 to exit. Each B9 actuates an
            // ECU output, so -- exactly like the KW1281 ActuatorTest above -- advancing is gated on a
            // user keypress (N = next / Q = quit), not fired automatically in a loop.
            var d = session.Kwp2000!;

            var endCode = Edc16OutputTestEndCode;

            // Human-readable only: suppress the per-frame Sent/RX/Received trace for the whole
            // output test (KW2000 mode) so the log shows just the actuator names, not raw K-line
            // bytes. Restored in the finally.
            var prevQuiet = d.QuietFrames;
            d.QuietFrames = true;
            try
            {
                _ = StartRoutine(d, AdaptRoutineEnter, new byte[] { 0x00, 0x00 }); // enumerate
                _ = StartRoutine(d, AdaptRoutineEnter, OutputTestSub);             // enter output-test mode
                Log.WriteLine("KWP2000 output test — each step activates an ECU output. Press 'N' for the " +
                              "next output or 'Q' to stop.");

                // EDC16 gives no explicit "no more tests" signal.
                // Each output is a TWO-PRESS cycle: press once to ACTIVATE the shown
                // output, press again to ADVANCE to the next one. The displayed output does NOT
                // change between the two presses — the id only changes when the NEXT output's
                // 0x03-type state record is read at the top of the loop. Each press is one 31 B9
                // step; the ECU streams 0x02-type live-value records between the state records.
                int? firstCode = null;
                var testNumber = 1;

                // Keep the K-line alive on a background thread while we BLOCK on Console.ReadKey.
                // ReadKey MUST be called directly: a non-blocking Console.KeyAvailable poll that
                // never calls ReadKey would leave the prompt unanswerable.
                using var keepAlive = new BackgroundKeepAlive(
                    () => StartRoutine(d, AdaptRoutineRead, OutputTestSub));

                ConsoleKey WaitForKey(string prompt)
                {
                    Console.Write(prompt);
                    keepAlive.Resume();              // ping in the background during the wait / dialog
                    ConsoleKeyInfo k;
                    try
                    {
                        do { k = Console.ReadKey(intercept: true); }
                        while (k.Key != ConsoleKey.N && k.Key != ConsoleKey.Q);
                    }
                    finally
                    {
                        keepAlive.Pause();           // stop pinging before we drive the dialog again
                    }
                    Console.WriteLine();
                    return k.Key;
                }

                while (true)
                {
                    // Settle onto the next 0x03-type state record (the output to display). After the
                    // previous output's ADVANCE step the ECU returns it right away; the bounded
                    // re-read just tolerates a stray live-value record.
                    int? outputCode = null;
                    for (var settle = 0; settle < 8 && outputCode == null; settle++)
                    {
                        var status = StartRoutine(d, AdaptRoutineRead, OutputTestSub);
                        outputCode = status.Length > 6 && status[3] == 0x03 && status[4] == 0x03
                            ? (status[5] << 8) | status[6] : (int?)null;
                    }
                    if (outputCode == null)
                    {
                        Log.WriteLine("No further output-test state reported — stopping.");
                        break;
                    }
                    if (outputCode == endCode)
                    {
                        Log.WriteLine("Reached the end of the output-test list — stopping.");
                        break;
                    }
                    if (firstCode == null)
                    {
                        firstCode = outputCode;
                    }
                    else if (outputCode == firstCode)
                    {
                        Log.WriteLine("Cycled through all output tests — stopping.");
                        break;
                    }

                    // Name from the embedded ID -> name table by the VAG device number the ECU
                    // reports as this output's code; an unrecognised code shows the VAG 5-digit
                    // number.
                    var name = Edc16OutputTestNames.TryGetValue(outputCode.Value, out var n) ? n : null;
                    var label = name != null
                        ? $"#{testNumber} {name} (VAG {outputCode.Value:D5})"
                        : $"Output test #{testNumber} (VAG {outputCode.Value:D5})";
                    Log.WriteLine($"Actuator Test: {label}");

                    // Press 1 — ACTIVATE the shown output (fires it); the same output stays displayed.
                    if (WaitForKey("Press 'N' to activate this test, or 'Q' to quit") == ConsoleKey.Q)
                    {
                        break;
                    }
                    _ = StartRoutine(d, AdaptRoutineSelectOrValue, OutputTestSub); // 31 B9 = activate
                    try { _ = StartRoutine(d, AdaptRoutineRead, OutputTestSub); } catch { /* poll feedback */ }

                    // Press 2 — ADVANCE to the next output (the id changes only after this).
                    if (WaitForKey("Press 'N' to advance to the next test, or 'Q' to quit") == ConsoleKey.Q)
                    {
                        break;
                    }
                    _ = StartRoutine(d, AdaptRoutineSelectOrValue, OutputTestSub); // 31 B9 = advance
                    testNumber++;
                }

                var stop = new List<byte> { AdaptRoutineEnter };
                stop.AddRange(OutputTestSub);
                _ = d.SendReceive((Service)0x32, stop.ToArray()); // 32 B8 01 02 exit
                Log.WriteLine("Output test ended.");
            }
            finally
            {
                d.QuietFrames = prevQuiet;
            }
        }

        /// <summary>
        /// Opens the default diagnostic session on a KWP2000 controller (no-op for KW1281). Some
        /// EDC16 ECUs answer readEcuIdentification in the default post-wakeup state but then IGNORE
        /// readDataByLocalIdentifier (measuring blocks) until a startDiagnosticSession has been sent
        /// — so a startDiagnosticSession (session 0x89) is opened right after wakeup. Live Data
        /// Logging calls this after WakeUpAny so a sustained measuring-block stream keeps getting
        /// answered.
        /// </summary>
        public void StartDiagnosticSessionAny(EcuSession session)
        {
            if (!session.IsKwp2000)
            {
                return;
            }
            StartDefaultDiagSession(session.Kwp2000!);
        }

        /// <summary>
        /// OPTIONAL: tries to raise the K-line link speed for a Live Data session by
        /// asking the ECU to switch speed via startDiagnosticSession(0x89) with a speed byte
        /// (0x87 => 124800), ack-gated, then VALIDATES with one measuring-block read and REVERTS to
        /// <paramref name="fallbackBaud"/> if the ECU NAKs the speed byte or stops answering. Applies
        /// the new rate to the interface as a side effect and returns the achieved baud. KWP2000
        /// only (returns fallbackBaud for KW1281).
        /// </summary>
        // Raises the KWP2000 (EDC16) Live Data link speed to targetBaud (speedByte is the ECU's
        // startDiagnosticSession rate selector — the SAME bytes EDC16 flashing uses: 0x50=38400,
        // 0x87=124800, 0xA7=244898). Ack-gated: on any refusal or post-change silence it reverts to
        // fallbackBaud and returns it, so the caller stays on the rate that already woke the ECU.
        // A zero/invalid target (or a KW1281 session) is a no-op.
        public int TryRaiseKwp2000Baud(EcuSession session, int fallbackBaud, byte speedByte, int targetBaud)
        {
            if (!session.IsKwp2000 || speedByte == 0 || targetBaud <= fallbackBaud)
            {
                return fallbackBaud;
            }
            // CH340 cables can't hold the 0xA7 (~245k) rate -- trim to 0x87/124800 (see Tester.cs).
            var d = session.Kwp2000!;

            try
            {
                // 10 89 <speed> -> expect 50 89 <speed>. SendReceive throws on a NAK or no reply.
                _ = d.SendReceive(Service.startDiagnosticSession, new byte[] { 0x89, speedByte });
            }
            catch (Exception)
            {
                Log.WriteLine($"Higher baud rate not accepted by the ECU — staying at {fallbackBaud} baud.");
                return fallbackBaud;
            }

            _kwpCommon!.Interface.SetBaudRate(targetBaud);
            try
            {
                _ = d.ReadDataByLocalIdentifier(0x01); // prove the ECU still answers at the new speed
                Log.WriteLine($"Live Data link raised to {targetBaud} baud.");
                return targetBaud;
            }
            catch (Exception)
            {
                _kwpCommon.Interface.SetBaudRate(fallbackBaud);
                Log.WriteLine(
                    $"ECU stopped answering after the baud change — reverted to {fallbackBaud} baud. " +
                    "(The session will re-establish at the standard rate.)");
                return fallbackBaud;
            }
        }

        public void BasicSettingAny(EcuSession session, byte groupNumber)
        {
            if (!session.IsKwp2000)
            {
                BasicSettingRead(groupNumber);
                return;
            }

            // Match the KW1281 BasicSettingRead loop (upstream parity): on KW1281 basic setting IS
            // the interactive Group Read loop (BasicSettingRead calls GroupRead(useBasicSetting:true))
            // -- browse groups with Up/Down, quit with Q, continuously re-reading and overlaying the
            // current group's values in place. The KWP2000 equivalent uses the 31 B8/BA 00 <grp>
            // routine family: 31 B8 00 00 enumerate (once), 31 B8 00 <grp> enter, 31 BA 00 <grp>
            // poll, 32 B8 00 <grp> exit. Each group is bracketed by enter/exit, so an Up/Down browse
            // exits the current group's routine and enters the new one. Values decode as the SAME
            // measuring-block (formula, A, B) SensorValue cells the group read uses (see
            // DecodeKwp2000BasicSetting), so the KWP2000 command now behaves identically to KW1281.
            var d = session.Kwp2000!;
            Log.WriteLine("Sending Basic Setting Read blocks...");
            Log.WriteLine("[Up arrow | Down arrow | Q to quit]", LogDest.Console);

            // Human-readable only: suppress the per-frame Sent/RX/Received trace for the whole
            // interactive session (like the output test / adaptation) so the overlay shows just the
            // decoded values, not raw K-line bytes. Restored in the finally.
            var prevQuiet = d.QuietFrames;
            d.QuietFrames = true;

            try { _ = StartRoutine(d, AdaptRoutineEnter, new byte[] { 0x00, 0x00 }); } catch { /* enumerate; best-effort */ }
            var entered = TryEnterBasicSettingGroup(d, groupNumber);
            try
            {
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var keyInfo = Console.ReadKey(intercept: true);
                        if (keyInfo.Key == ConsoleKey.UpArrow)
                        {
                            if (groupNumber < 255)
                            {
                                ExitBasicSettingGroup(d, groupNumber);
                                groupNumber++;
                                entered = TryEnterBasicSettingGroup(d, groupNumber);
                            }
                        }
                        else if (keyInfo.Key == ConsoleKey.DownArrow)
                        {
                            if (groupNumber > 1)
                            {
                                ExitBasicSettingGroup(d, groupNumber);
                                groupNumber--;
                                entered = TryEnterBasicSettingGroup(d, groupNumber);
                            }
                        }
                        else if (keyInfo.Key == ConsoleKey.Q)
                        {
                            break;
                        }
                    }

                    List<SensorValue> values;
                    if (entered)
                    {
                        try
                        {
                            var block = StartRoutine(d, AdaptRoutineRead, new byte[] { 0x00, groupNumber });
                            values = DecodeKwp2000BasicSetting(block);
                        }
                        catch
                        {
                            values = new List<SensorValue>();
                        }
                    }
                    else
                    {
                        values = new List<SensorValue>();
                    }

                    if (values.Count == 0)
                    {
                        GroupReadOverlay($"Basic Setting {groupNumber:D3}: Not Available");
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var value in values)
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append(" | ");
                            }
                            sb.Append(value.ToString());
                        }
                        GroupReadOverlay($"Basic Setting {groupNumber:D3}: {sb}");
                    }
                }
            }
            finally
            {
                if (entered)
                {
                    ExitBasicSettingGroup(d, groupNumber);
                }
                d.QuietFrames = prevQuiet;
            }
            Log.WriteLine(LogDest.Console);
        }

        // Enters KWP2000 basic-setting mode for one group (31 B8 00 <grp>); false if the ECU NAKs
        // (e.g. an unavailable group), so the loop shows "Not Available" and browsing continues.
        private static bool TryEnterBasicSettingGroup(IKW2000Dialog d, byte groupNumber)
        {
            try
            {
                _ = StartRoutine(d, AdaptRoutineEnter, new byte[] { 0x00, groupNumber });
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Exits KWP2000 basic-setting mode for one group (32 B8 00 <grp>). Best-effort: swallow a
        // fault so an Up/Down browse (which exits before re-entering) and the final cleanup never
        // throw out of the loop.
        private static void ExitBasicSettingGroup(IKW2000Dialog d, byte groupNumber)
        {
            try
            {
                _ = d.SendReceive((Service)0x32, new byte[] { AdaptRoutineEnter, 0x00, groupNumber });
            }
            catch
            {
                // best-effort exit
            }
        }

        // Decodes a KWP2000 basic-setting read (31 BA 00 <grp>) response body into measuring-block
        // cells. Body layout is BA 00 <grp> <status> then standard 3-byte (formula, A, B)
        // SensorValue cells -- the SAME stream group reads use -- so the existing SensorValue formula
        // table decodes it directly. Group 3's "04 01 69 00 27 46 A6 31 C8 00 17 65 FF" =>
        // 0 rpm | 45.4 mg/h | 0.0 mg/h | 100.6 % (the trailing FF is cell 4's B byte via formula 23,
        // not a terminator). Trailing 0x25 pad cells (unused slots) are trimmed as in the group read.
        private static List<SensorValue> DecodeKwp2000BasicSetting(byte[] block)
        {
            var values = new List<SensorValue>();
            // Drop the routine echo (BA), the 2-byte sub-selector (00 <grp>) and the status byte,
            // leaving whole 3-byte cells.
            if (block is null || block.Length < 4)
            {
                return values;
            }
            for (var i = 4; i + 3 <= block.Length; i += 3)
            {
                values.Add(new SensorValue(block[i], block[i + 1], block[i + 2]));
            }
            while (values.Count > 0 && values[values.Count - 1].SensorID == 0x25)
            {
                values.RemoveAt(values.Count - 1);
            }
            return values;
        }


        // --- Adaptation over both protocols -----------------------------------------------------
        // KWP2000 adaptation on EDC16 is a family of startRoutineByLocalIdentifier (0x31) calls with
        // routine ids 0xB8/0xB9/0xBA and the fixed sub-selector "01 03", plus stopRoutine (0x32)
        // 0xB8 to exit.
        //
        // The optional `unlockCode` is the code that unlocks the write routines (B9 test / BB save),
        // and its MEANING is protocol-specific -- this is deliberately NOT called "login":
        //   KW1281  -> the KW1281 login block (the historical adaptation login code).
        //   KWP2000 -> the securityAccess 0x27 code (e.g. 12233), key = seed + code.
        // Security access and login are different mechanisms with different codes; the
        // unlockCode here is security access, NOT a login.

        private const byte AdaptRoutineEnter = 0xB8; // 31 B8 00 00 enumerate, 31 B8 01 03 start
        private const byte AdaptRoutineSelectOrValue = 0xB9; // 31 B9 01 03 <chan> select, 31 B9 01 03 <val:2> test
        private const byte AdaptRoutineRead = 0xBA; // 31 BA 01 03 read current
        private const byte AdaptRoutineSave = 0xBB; // 31 BB 01 03 <val:2> <wsc:6>
        private static readonly byte[] AdaptSub = { 0x01, 0x03 };

        // Decode a KWP2000 adaptation StartRoutine (0x31 BA 01 03) response body.
        // Body layout (response SID 0x71 already stripped):
        //   BA[0] 01[1] 03[2] status[3] 03[4] val_hi[5] val_lo[6] cellType[7] a[8] b[9] c[10] ...
        // cellType 0x04 => the following 3 bytes are a SensorValue (measured), then min/max/default.
        // Value is the raw 16-bit adaptation value (e.g. 20 00 = 8192).
        private static (int Value, string? Measured) DecodeKwp2000Adaptation(byte[] block)
        {
            if (block is null || block.Length < 7)
                return (-1, null);
            int value = (block[5] << 8) | block[6];
            string? measured = null;
            if (block.Length >= 11 && block[7] == 0x04)
                measured = new SensorValue(block[8], block[9], block[10]).ToString();
            return (value, measured);
        }

        public void AdaptationReadAny(EcuSession session, byte channel, ushort? unlockCode, int workshopCode)
        {
            if (!session.IsKwp2000)
            {
                AdaptationRead(channel, unlockCode, workshopCode);
                return;
            }

            var d = session.Kwp2000!;
            // This EDC16 denies the adaptation channel-select (31 B9 01 03 <chan>) with
            // securityAccessDenied unless 0x27 security access is unlocked first — same as the test
            // and save paths. So unlock here too when the caller supplied a code.
            var prevQuiet = d.QuietFrames;
            d.QuietFrames = true; // adaptation: show only the decoded result, not the frames
            try
            {
                if (unlockCode.HasValue) Kwp2000SecurityUnlock(d, unlockCode.Value);
                EnterKwp2000Adaptation(d);
                SelectKwp2000AdaptationChannel(d, channel);
                var block = StartRoutine(d, AdaptRoutineRead, AdaptSub);
                var (value, measured) = DecodeKwp2000Adaptation(block);
                if (value >= 0)
                    Log.WriteLine($"Adaptation channel {channel}: value {value} (0x{value:X4})" +
                        (measured is null ? "" : $"   measured: {measured}"));
                else
                    Log.WriteLine($"Adaptation channel {channel}: {Utils.DumpBytes(block)}");
                ExitKwp2000Adaptation(d);
            }
            finally
            {
                d.QuietFrames = prevQuiet;
            }
        }

        public void AdaptationTestAny(EcuSession session, byte channel, ushort channelValue,
            ushort? unlockCode, int workshopCode)
        {
            if (!session.IsKwp2000)
            {
                AdaptationTest(channel, channelValue, unlockCode, workshopCode);
                return;
            }

            var d = session.Kwp2000!;
            var prevQuiet = d.QuietFrames;
            d.QuietFrames = true; // adaptation: show only the decoded result, not the frames
            try
            {
                if (unlockCode.HasValue) Kwp2000SecurityUnlock(d, unlockCode.Value);
                EnterKwp2000Adaptation(d);
                SelectKwp2000AdaptationChannel(d, channel);
                _ = StartRoutine(d, AdaptRoutineSelectOrValue, ConcatVal(channelValue));
                var block = StartRoutine(d, AdaptRoutineRead, AdaptSub);
                var (value, measured) = DecodeKwp2000Adaptation(block);
                if (value >= 0)
                    Log.WriteLine($"Adaptation channel {channel} test value {channelValue}: value {value} (0x{value:X4})" +
                        (measured is null ? "" : $"   measured: {measured}"));
                else
                    Log.WriteLine($"Adaptation channel {channel} test value {channelValue}: {Utils.DumpBytes(block)}");
                ExitKwp2000Adaptation(d);
            }
            finally
            {
                d.QuietFrames = prevQuiet;
            }
        }

        public void AdaptationSaveAny(EcuSession session, byte channel, ushort channelValue,
            ushort? unlockCode, int workshopCode)
        {
            if (!session.IsKwp2000)
            {
                AdaptationSave(channel, channelValue, unlockCode, workshopCode);
                return;
            }

            var d = session.Kwp2000!;
            var prevQuiet = d.QuietFrames;
            d.QuietFrames = true; // adaptation: show only the decoded result, not the frames
            try
            {
                if (unlockCode.HasValue) Kwp2000SecurityUnlock(d, unlockCode.Value);
                EnterKwp2000Adaptation(d);
                SelectKwp2000AdaptationChannel(d, channel);
                _ = StartRoutine(d, AdaptRoutineSelectOrValue, ConcatVal(channelValue));
                var (workshopCode6, _) = ReadKwp2000CodingRecordFields(d);
                var saveArgs = new List<byte>();
                saveArgs.AddRange(AdaptSub);       // 01 03 sub-selector (kept — see SelectKwp2000AdaptationChannel)
                saveArgs.Add((byte)(channelValue >> 8));
                saveArgs.Add((byte)(channelValue & 0xFF));
                saveArgs.AddRange(workshopCode6);  // 6-byte WSC, echoed
                _ = StartRoutine(d, AdaptRoutineSave, saveArgs.ToArray()); // 31 BB 01 03 <val:2> <wsc:6>
                Log.WriteLine($"Adaptation channel {channel} saved value {channelValue}.");
                ExitKwp2000Adaptation(d);
            }
            finally
            {
                d.QuietFrames = prevQuiet;
            }
        }

        // --- KWP2000 security access (0x27), key = seed + code (32-bit BE) -----------------------
        // No KW1281 analog; exposed as its own command (securityaccesskw2000) AND used as the
        // unlock step inside the adaptation write path above.

        public void Kwp2000SecurityAccess(uint code, bool? evenParityWakeup = null, Action? onWakeUp = null)
        {
            var session = WakeUpAny(evenParityWakeup);
            onWakeUp?.Invoke();
            if (!session.IsKwp2000)
            {
                Log.WriteLine("securityaccesskw2000 requires a KWP2000 ECU; this controller answered KW1281.");
                return;
            }
            StartDefaultDiagSession(session.Kwp2000!);
            var ok = Kwp2000SecurityUnlock(session.Kwp2000!, code);
            Log.WriteLine(ok ? "Security access granted." : "Security access DENIED (wrong code or locked out).");
        }

        /// <summary>
        /// Runs securityAccess level 0x03/0x04 with key = seed + <paramref name="code"/> (32-bit
        /// big-endian add), where the "security code" is exactly the added constant. Returns true if
        /// the ECU accepted the key.
        /// </summary>
        private static bool Kwp2000SecurityUnlock(IKW2000Dialog dialog, uint code)
        {
            return dialog.SecurityAccess(0x03, seed =>
            {
                uint s = 0;
                foreach (var b in seed.Take(4)) s = (s << 8) | b;
                uint k = s + code;
                return new[]
                {
                    (byte)((k >> 24) & 0xFF),
                    (byte)((k >> 16) & 0xFF),
                    (byte)((k >> 8) & 0xFF),
                    (byte)(k & 0xFF),
                };
            });
        }

        // --- KWP2000 helpers --------------------------------------------------------------------

        private static byte[] StartRoutine(IKW2000Dialog dialog, byte routineId, byte[] args)
        {
            var body = new List<byte> { routineId };
            body.AddRange(args);
            var response = dialog.SendReceive(Service.startRoutineByLocalIdentifier, body.ToArray());
            return response.Body.ToArray();
        }

        private static byte[] ConcatVal(ushort value)
        {
            var b = new List<byte>();
            b.AddRange(AdaptSub);
            b.Add((byte)(value >> 8));
            b.Add((byte)(value & 0xFF));
            return b.ToArray();
        }

        private static void EnterKwp2000Adaptation(IKW2000Dialog dialog)
        {
            _ = StartRoutine(dialog, AdaptRoutineEnter, new byte[] { 0x00, 0x00 }); // enumerate channels
            _ = StartRoutine(dialog, AdaptRoutineEnter, AdaptSub);                  // start adaptation
        }

        private static void SelectKwp2000AdaptationChannel(IKW2000Dialog dialog, byte channel)
        {
            // 31 B9 01 03 <channel> — the "01 03" sub-selector MUST be included. Omitting it
            // (sending 31 B9 <channel>) gives NRC 0x12 invalidFormat.
            var args = new List<byte>();
            args.AddRange(AdaptSub);
            args.Add(channel);
            _ = StartRoutine(dialog, AdaptRoutineSelectOrValue, args.ToArray());
        }

        private static void ExitKwp2000Adaptation(IKW2000Dialog dialog)
        {
            var body = new List<byte> { AdaptRoutineEnter };
            body.AddRange(AdaptSub);
            // stopRoutineByLocalIdentifier (0x32) isn't in the DiagnosticService enum; cast the raw SID.
            _ = dialog.SendReceive((Service)0x32, body.ToArray());
        }

        /// <summary>
        /// Reads the current 1A 9B identification and extracts the 6-byte WORKSHOP CODE field and
        /// 4-byte software-version string that the 0x9A coding/login write record must echo. Layout
        /// for this EDC16 family (body incl.
        /// leading option byte): 9B, part#(12), SW(4), 0x03, coding(3), workshopCode(6), engine...
        /// </summary>
        private static (byte[] WorkshopCode, byte[] Sw) ReadKwp2000CodingRecordFields(IKW2000Dialog dialog)
        {
            var body = dialog.ReadEcuIdentification(0x9B);
            if (body.Length < 27 || body[0] != 0x9B)
            {
                throw new InvalidOperationException(
                    $"Unexpected 1A 9B identification record (len {body.Length}); cannot build the " +
                    $"0x9A coding/login write. Raw: {Utils.DumpBytes(body)}");
            }
            var sw = body.Skip(13).Take(4).ToArray();               // "0314"
            var workshopCode = body.Skip(21).Take(6).ToArray();     // 6-byte WSC
            return (workshopCode, sw);
        }

        private static byte[] BuildKwp2000CodingRecord(byte[] workshopCode, byte[] sw, byte[] threeByteValue)
        {
            var record = new List<byte>();
            record.AddRange(workshopCode);   // 6 (WSC)
            record.AddRange(sw);             // 4
            record.Add(0x03);          // length of the value that follows
            record.AddRange(threeByteValue); // 3 (coding, or 0x800000|login)  [after WSC(6)+SW(4)+03]
            return record.ToArray();
        }

        // --- KWP2000 workshop-code (WSC) codec --------------------------------------------------
        // The 6-byte WSC field is a 48-bit big-endian pack of the three VAG workshop-identity
        // numbers. Worked example: WSC 45678, importer 345, equipment 987654 -> 78 90 32 B2 B2 6E
        // (re-encodes byte-for-byte). Layout:
        //   bits  0-16 (17 bits) = WSC        (workshop/dealer number, up to 5 digits / 131071)
        //   bits 17-26 (10 bits) = importer   (up to 1023)
        //   bits 27-46 (20 bits) = equipment  (up to 6 digits / 1048575)
        //   bit  47              = unused
        public static (int Wsc, int Importer, int Equipment) DecodeKwp2000Wsc(byte[] wsc6)
        {
            if (wsc6 is null || wsc6.Length != 6)
            {
                throw new ArgumentException("WSC field must be exactly 6 bytes.", nameof(wsc6));
            }
            ulong v = 0;
            foreach (var b in wsc6) v = (v << 8) | b;
            var wsc = (int)(v & 0x1FFFF);
            var importer = (int)((v >> 17) & 0x3FF);
            var equipment = (int)((v >> 27) & 0xFFFFF);
            return (wsc, importer, equipment);
        }

        public static byte[] EncodeKwp2000Wsc(int wsc, int importer, int equipment)
        {
            if ((uint)wsc > 0x1FFFF) throw new ArgumentOutOfRangeException(nameof(wsc), "WSC max 131071 (17-bit).");
            if ((uint)importer > 0x3FF) throw new ArgumentOutOfRangeException(nameof(importer), "Importer max 1023 (10-bit).");
            if ((uint)equipment > 0xFFFFF) throw new ArgumentOutOfRangeException(nameof(equipment), "Equipment max 1048575 (20-bit).");
            ulong v = ((ulong)(uint)wsc & 0x1FFFF)
                    | (((ulong)(uint)importer & 0x3FF) << 17)
                    | (((ulong)(uint)equipment & 0xFFFFF) << 27);
            var bytes = new byte[6];
            for (var i = 5; i >= 0; i--) { bytes[i] = (byte)(v & 0xFF); v >>= 8; }
            return bytes;
        }

        // Background keep-alive: pings the K-line on a worker thread while the interactive output
        // test blocks on Console.ReadKey, so the session doesn't time out during the user's
        // decision. Pause()/Resume() bracket every read so the ping thread never touches the
        // console concurrently with the main thread.
        private sealed class BackgroundKeepAlive : IDisposable
        {
            private readonly Action _ping;
            private readonly int _intervalMs;
            private volatile bool _cancel = true;
            private Thread? _thread;

            public BackgroundKeepAlive(Action ping, int intervalMs = 1500)
            {
                _ping = ping;
                _intervalMs = intervalMs;
            }

            public void Resume()
            {
                if (_thread != null) return;
                _cancel = false;
                _thread = new Thread(() =>
                {
                    while (!_cancel)
                    {
                        for (var w = 0; w < _intervalMs / 50 && !_cancel; w++) Thread.Sleep(50);
                        if (_cancel) break;
                        try { _ping(); } catch { break; }
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public void Pause()
            {
                _cancel = true;
                try { _thread?.Join(); } catch { /* ignore */ }
                _thread = null;
            }

            public void Dispose() => Pause();
        }
    }
}
