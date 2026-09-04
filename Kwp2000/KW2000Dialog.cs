using BitFab.KW1281Test.Blocks;
using BitFab.KW1281Test.Kwp2000;
using BitFab.KW1281Test.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test
{
    /// <summary>
    /// Manages a dialog with a VW controller using the KWP2000 protocol. Mirrors
    /// IKW1281Dialog's role for KW1281 -- pulled out as an interface so callers (Tester,
    /// BoschRBxCluster) can be tested/extended without depending on the concrete class, and so a
    /// future EDC16-specific dialog wrapper could implement the same surface. The
    /// DumpMem/StartDiagnosticSession/EcuReset/Read+WriteMemoryByAddress/SendReceive/SendMessage/
    /// ReceiveMessage members were already here (used by Edc15VM and BoschRBxCluster for EEPROM
    /// access); ReadFaultCodes/ClearFaultCodes/IoControlByLocalIdentifier/
    /// ReadDataByLocalIdentifier/WriteDataByLocalIdentifier/SecurityAccess are new -- the service
    /// IDs are the standard ISO14230-3 ones, but the exact response body layout for a couple of
    /// them (see ReadFaultCodes) is not yet certain.
    /// </summary>
    internal interface IKW2000Dialog
    {
        int P3 { get; set; }

        int P4 { get; set; }

        /// <summary>
        /// True (default) logs <see cref="SendMessage"/>/<see cref="ReceiveMessage"/>'s routine
        /// per-field trace lines ("Sent: ...", "RX: format=...", etc.) at the normal, on-screen-visible
        /// destination. Set false for callers that call this dialog in a tight loop -- e.g.
        /// <see cref="EDC15.Edc15VM.ReadWriteEeprom"/>'s per-byte EEPROM write loop, which can run
        /// this up to ~512 times for a full rewrite, each call logging ~5 lines -- to route those
        /// SAME lines to LogDest.File instead (still fully captured for later inspection, just not
        /// flooding the on-screen log -- see <see cref="EDC15.Edc15FlashVM.WriteRaw"/>'s
        /// doc comment for that history). Mirrors <see cref="EDC16.Edc16FlashVM"/>'s own
        /// <c>_verboseLog</c> field, which solves the identical problem for that class's
        /// higher-volume TX/RX tracing -- this is the shared-dialog-class equivalent, since
        /// KW2000Dialog (unlike Edc16FlashVM's private helpers) is used by many call sites, most of
        /// which are low-volume and should keep seeing this by default.
        /// </summary>
        bool VerboseLog { get; set; }

        /// <summary>
        /// When true, <see cref="SendMessage"/> transmits the whole frame in one batched write with
        /// no per-byte P4 delay, for a faster request cadence — used by Live Data Logging's optional
        /// "fast timing" mode. Default false (per-byte, spec-conservative).
        /// </summary>
        bool FastTiming { get; set; }

        /// <summary>
        /// When true, the per-frame Sent/Received/RX trace lines are NOT logged at all (neither the
        /// output pane nor the log file). Live Data Logging sets this because it streams reads
        /// continuously — the frame trace would flood the UI thread and hang the app. The decoded
        /// measuring-block VALUES are unaffected (they go to the data log via the read callbacks).
        /// </summary>
        bool QuietFrames { get; set; }

        void DumpMem(uint address, uint length, string dumpFileName);

        void StartDiagnosticSession(byte v1, byte v2);

        void EcuReset(byte value);

        byte[] ReadMemoryByAddress(uint address, byte count);

        byte[] WriteMemoryByAddress(uint address, byte count, byte[] data);

        /// <summary>
        /// Reads current/stored fault codes via readDiagnosticTroubleCodesByStatus (service
        /// 0x18). Reuses the same FaultCode type (and 3-byte DTC/status record shape) that
        /// KW1281's FaultCodesBlock uses -- see TroubleCodeConfig.cs's TroubleCodeDescriber for
        /// how these get turned into a display-ready list.
        /// </summary>
        List<FaultCode> ReadFaultCodes(byte statusMask = 0x02, ushort groupOfDtc = 0xFF00);

        /// <summary>
        /// Clears fault codes via clearDiagnosticInformation (service 0x14).
        /// </summary>
        void ClearFaultCodes(byte[]? groupOfDtc = null);

        /// <summary>
        /// Sends one raw request and reports what came back without throwing -- see
        /// <see cref="KW2000Dialog.Probe"/>. Used by Tester.Kwp2000Probe to let the ECU enumerate
        /// its own supported services / identification options / local identifiers.
        /// </summary>
        ProbeResult Probe(byte service, byte[] body, int timeoutMs = 1000);

        /// <summary>
        /// Output/actuator test via inputOutputControlByLocalIdentifier (service 0x30).
        /// </summary>
        byte[] IoControlByLocalIdentifier(byte localIdentifier, byte[] controlState);

        /// <summary>
        /// Reads a coding/parameter value via readDataByLocalIdentifier (service 0x21).
        /// </summary>
        byte[] ReadDataByLocalIdentifier(byte localIdentifier);

        /// <summary>
        /// Reads ECU identification data via readEcuIdentification (service 0x1A). See
        /// KW2000Dialog.ReadEcuIdentification's doc comment for the identificationOption values
        /// and why this is useful as a bare-minimum KWP2000 connectivity probe.
        /// </summary>
        byte[] ReadEcuIdentification(byte identificationOption = 0x9B);

        /// <summary>
        /// Writes a coding/parameter value via writeDataByLocalIdentifier (service 0x3B).
        /// </summary>
        byte[] WriteDataByLocalIdentifier(byte localIdentifier, byte[] data);

        /// <summary>
        /// Generic seed/key security-access handshake (service 0x27) -- see the implementation's
        /// doc comment for details.
        /// </summary>
        bool SecurityAccess(byte accessMode, Func<byte[], byte[]> computeKey);

        Kwp2000Message SendReceive(Service service, byte[] body, bool excludeAddresses = false);

        void SendMessage(Service service, byte[] body, bool excludeAddresses = false);

        Kwp2000Message ReceiveMessage();
    }

    internal enum ProbeOutcome
    {
        /// <summary>A positive response (service | 0x40) came back.</summary>
        Positive,
        /// <summary>A 0x7F negative response came back; <see cref="ProbeResult.Nrc"/> has the code.</summary>
        Negative,
        /// <summary>Nothing came back within the probe timeout.</summary>
        NoResponse,
        /// <summary>Something framed came back but wasn't a response to this request.</summary>
        Unexpected,
    }

    /// <summary>One <see cref="KW2000Dialog.Probe"/> result. Response is null unless Positive/Unexpected.</summary>
    internal sealed record ProbeResult(ProbeOutcome Outcome, byte Service, byte[] Request, Kwp2000Message? Response, byte? Nrc)
    {
        public bool IsSupported => Outcome == ProbeOutcome.Positive ||
            (Outcome == ProbeOutcome.Negative && Nrc != (byte)ResponseCode.serviceNotSupported);
    }

    internal class KW2000Dialog : IKW2000Dialog
    {
        private const byte _testerAddress = 0xF1;

        /// <summary>
        /// Inter-command delay (milliseconds)
        /// </summary>
        public int P3 { get; set; } = 55;

        /// <summary>
        /// Inter-byte delay (milliseconds)
        /// </summary>
        public int P4 { get; set; } = 5;

        public bool VerboseLog { get; set; } = true;

        public bool FastTiming { get; set; }

        public bool QuietFrames { get; set; }

        public void DumpMem(uint address, uint length, string dumpFileName)
        {
            StartDiagnosticSession(0x84, 0x14);

            Thread.Sleep(350);

            Log.WriteLine($"Saving memory dump to {dumpFileName}");
            DumpMemory(address, length, maxReadLength: 32, dumpFileName);
            Log.WriteLine($"Saved memory dump to {dumpFileName}");

            EcuReset(0x01);
        }

        private void DumpMemory(
            uint startAddr, uint length, byte maxReadLength, string fileName)
        {
            using var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough);
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                try
                {
                    var blockBytes = ReadMemoryByAddress(addr, readLength);
                    fs.Write(blockBytes, 0, blockBytes.Length);

                    if (blockBytes.Length != readLength)
                    {
                        throw new InvalidOperationException(
                            $"Expected {readLength} bytes from ReadMemoryByAddress() but received {blockBytes.Length} bytes");
                    }
                }
                catch (NegativeResponseException)
                {
                    // Access not allowed?
                    Log.WriteLine("Failed to read memory.");
                }
                finally
                {
                    fs.Flush();
                }
            }
        }

        public void StartDiagnosticSession(byte v1, byte v2)
        {
            var responseMessage = SendReceive(Service.startDiagnosticSession, new[] { v1, v2 });
            if (responseMessage.Body[0] != v1)
            {
                throw new InvalidOperationException($"Unexpected diagnosticMode: {responseMessage.Body[0]:X2}");
            }
        }

        public void EcuReset(byte value)
        {
            var responseMessage = SendReceive(Service.ecuReset, new[] { value });
        }

        public byte[] ReadMemoryByAddress(uint address, byte count)
        {
            var addressBytes = Utils.GetBytes(address);

            var responseMessage = SendReceive(Service.readMemoryByAddress,
                new byte[]
                {
                    addressBytes[2], addressBytes[1], addressBytes[0],
                    count
                });

            return responseMessage.Body.ToArray();
        }

        public byte[] WriteMemoryByAddress(uint address, byte count, byte[] data)
        {
            var addressBytes = Utils.GetBytes(address);

            var messageBytes = new List<byte>
            {
                addressBytes[2],
                addressBytes[1],
                addressBytes[0],
                count
            };
            messageBytes.AddRange(data);

            var responseMessage = SendReceive(Service.writeMemoryByAddress,
                messageBytes.ToArray());

            return responseMessage.Body.ToArray();
        }

        /// <summary>
        /// Reads current/stored fault codes via readDiagnosticTroubleCodesByStatus (0x18):
        /// request body is [statusMask, groupOfDTC high byte, groupOfDTC low byte]. Defaults
        /// (0xFF status mask, 0xFFFF group) mean "everything, any status".
        /// </summary>
        /// <remarks>
        /// The response body layout below (an optional leading count byte, then repeating
        /// 3-byte DTC-high/DTC-low/status records -- same shape as KW1281's FaultCodesBlock) is
        /// what ISO14230-3 documents. If an ECU uses a different layout (e.g. no count byte, or an
        /// extra statusOfDTCAvailabilityMask byte), this is the place to adjust it.
        /// </remarks>
        /// <summary>
        /// readDiagnosticTroubleCodesByStatus (0x18). Defaults: status mask 0x02, group 0xFF00
        /// (request `84 10 F1 18 02 FF 00`). The reply body is `count, then count x [DTC hi, DTC
        /// lo, status]` (e.g. `58 08 4C 66 40 40 E2 40 ...`) -- the count byte is a real field, so
        /// it's read as one; if the count doesn't match the body length the whole body is
        /// treated as 3-byte records instead (older ISO14230-3 layouts have no count).
        /// </summary>
        public List<FaultCode> ReadFaultCodes(byte statusMask = 0x02, ushort groupOfDtc = 0xFF00)
        {
            var responseMessage = SendReceive(Service.readDiagnosticTroubleCodesByStatus,
                new byte[] { statusMask, (byte)(groupOfDtc >> 8), (byte)(groupOfDtc & 0xFF) });

            var body = responseMessage.Body;
            var faultCodes = new List<FaultCode>();
            if (body.Count == 0)
            {
                return faultCodes;
            }

            var offset = body.Count == 1 + body[0] * 3 ? 1 : 0;
            if (offset == 0 && body.Count % 3 != 0)
            {
                Log.WriteLine(
                    $"Warning: readDTCByStatus body length {body.Count} matches neither " +
                    $"'count + 3-byte records' (count byte ${body[0]:X2}) nor plain 3-byte records.");
            }

            for (var i = offset; i + 2 < body.Count; i += 3)
            {
                var dtc = (body[i] << 8) | body[i + 1];
                var status = body[i + 2];

                var faultCode = new FaultCode(dtc, status);
                if (!faultCode.Equals(FaultCode.None))
                {
                    faultCodes.Add(faultCode);
                }
            }

            return faultCodes;
        }

        /// <summary>
        /// Clears fault codes via clearDiagnosticInformation (0x14). groupOfDtc defaults to the
        /// 2-byte 0xFF00 group (request `83 10 F1 14 FF 00`, ACKed `54 FF 00`). Pass a different
        /// value if another ECU wants e.g. the 3-byte 0xFFFFFF form.
        /// </summary>
        public void ClearFaultCodes(byte[]? groupOfDtc = null)
        {
            groupOfDtc ??= new byte[] { 0xFF, 0x00 };
            _ = SendReceive(Service.clearDiagnosticInformation, groupOfDtc);
        }

        /// <summary>
        /// Output/actuator test via inputOutputControlByLocalIdentifier (0x30): body is
        /// [localIdentifier, ...controlState]. What controlState means (and what
        /// localIdentifier values are valid) is entirely ECU/software-version specific --
        /// unlike KW1281's ActuatorTest, there's no single well-known value space here.
        /// </summary>
        public byte[] IoControlByLocalIdentifier(byte localIdentifier, byte[] controlState)
        {
            var body = new List<byte> { localIdentifier };
            body.AddRange(controlState);
            var responseMessage = SendReceive(Service.inputOutputControlByLocalIdentifier, body.ToArray());
            return responseMessage.Body.ToArray();
        }

        /// <summary>
        /// Reads a coding/parameter value via readDataByLocalIdentifier (0x21).
        /// </summary>
        public byte[] ReadDataByLocalIdentifier(byte localIdentifier)
        {
            var responseMessage = SendReceive(Service.readDataByLocalIdentifier, new[] { localIdentifier });
            return responseMessage.Body.ToArray();
        }

        /// <summary>
        /// Reads ECU identification data via readEcuIdentification (0x1A): body is
        /// [identificationOption]. ISO14230-3 defines a range of standard option values (0x80 =
        /// ECUIdentificationDataTable, 0x90 = VIN, etc.), but VAG overloads 0x9B specifically:
        /// VAG ECUs respond to 0x9B with the spare-part number and software-ID string (e.g.
        /// "022906032GK 6243...MOTRONIC ME7.1.*G...") rather than the generic-spec calibration
        /// date, so that's the default here.
        ///
        /// Unlike every other KWP2000 request this app sends, readEcuIdentification does NOT need
        /// a prior startDiagnosticSession -- ISO14230-3 6.1.1 lists it as one of the services
        /// already active in the default standardSession right after startCommunication/wakeup.
        /// That makes this useful as a bare-minimum KWP2000 connectivity probe: if this gets a
        /// real response but startDiagnosticSession still doesn't, the wakeup/addressing is fine
        /// and the problem is specifically in the diagnostic-session step (wrong subfunction,
        /// wrong timing, etc.) rather than in KWP2000 communication as a whole.
        /// </summary>
        public byte[] ReadEcuIdentification(byte identificationOption = 0x9B)
        {
            var responseMessage = SendReceive(Service.readEcuIdentification, new[] { identificationOption });
            return responseMessage.Body.ToArray();
        }

        /// <summary>
        /// Writes a coding/parameter value via writeDataByLocalIdentifier (0x3B): body is
        /// [localIdentifier, ...data]. This is very likely gated behind a prior successful
        /// SecurityAccess on a real ECU.
        /// </summary>
        public byte[] WriteDataByLocalIdentifier(byte localIdentifier, byte[] data)
        {
            var body = new List<byte> { localIdentifier };
            body.AddRange(data);
            var responseMessage = SendReceive(Service.writeDataByLocalIdentifier, body.ToArray());
            return responseMessage.Body.ToArray();
        }

        /// <summary>
        /// Generic seed/key security-access handshake (0x27): requests a seed at
        /// <paramref name="accessMode"/>, and if the ECU returns a non-zero seed, calls
        /// <paramref name="computeKey"/> to turn it into a key and sends that back at
        /// <paramref name="accessMode"/> + 1. Mirrors the handshake Edc15VM.ReadWriteEeprom does
        /// inline (see its LVL41Auth) but with the seed-to-key algorithm pulled out as a
        /// parameter, since EDC16's algorithm and access-level byte aren't known yet -- this is
        /// meant to be reused once those are confirmed, not to replace Edc15VM's own
        /// EDC15-specific call site. Returns true if the ECU accepted the key (or no key was
        /// needed because it was already unlocked).
        /// </summary>
        public bool SecurityAccess(byte accessMode, Func<byte[], byte[]> computeKey)
        {
            var seedResponse = SendReceive(Service.securityAccess, new[] { accessMode });
            var seedBytes = seedResponse.Body.Skip(1).ToArray();

            if (seedBytes.Length == 0 || seedBytes.All(b => b == 0))
            {
                // Already unlocked / no seed challenge required.
                return true;
            }

            var keyBytes = computeKey(seedBytes);
            var keyMessage = new List<byte> { (byte)(accessMode + 1) };
            keyMessage.AddRange(keyBytes);

            try
            {
                _ = SendReceive(Service.securityAccess, keyMessage.ToArray());
                return true;
            }
            catch (NegativeResponseException)
            {
                return false;
            }
        }

        /// <summary>
        /// How many times to (re)send a request if the ECU never answers at all. Set to 1 (no
        /// resend): a non-responding ECU never recovers on a later attempt -- a
        /// startDiagnosticSession timeout fails identically on every attempt, with zero response
        /// each time, so resending never once recovers a response; it just burns an extra receive
        /// timeout (each attempt waits out the full timeout before giving up) on every genuine
        /// failure. So a non-responding ECU fails fast instead of wasting that time.
        /// </summary>
        /// <summary>
        /// Sends <paramref name="service"/> with <paramref name="body"/> once and classifies the
        /// reply instead of throwing. The point is to let the ECU document itself: ISO 14230-3
        /// requires a negative response for anything it doesn't do, and the code it picks says
        /// why -- 0x11 serviceNotSupported means the SID isn't implemented at all, while 0x12
        /// (subfunction/format), 0x13 (length), 0x22 (conditions), 0x31 (out of range), 0x33
        /// (security) or 0x80 (not in this session) all mean the SID exists and only the argument
        /// was wrong. reqCorrectlyRcvdRspPending (0x78) is followed up like every other call.
        ///
        /// Runs with the interface read timeout lowered to <paramref name="timeoutMs"/> (the
        /// default 8 s makes a 256-entry sweep take forever when a few entries stay silent) and
        /// per-field trace logging off; both are restored before returning.
        /// </summary>
        public ProbeResult Probe(byte service, byte[] body, int timeoutMs = 1000)
        {
            var savedTimeout = _kwpCommon.Interface.ReadTimeout;
            var savedVerbose = VerboseLog;
            _kwpCommon.Interface.ReadTimeout = timeoutMs;
            VerboseLog = false;
            var request = new byte[body.Length + 1];
            request[0] = service;
            body.CopyTo(request, 1);
            try
            {
                var response = SendReceive((Service)service, body);
                return new ProbeResult(ProbeOutcome.Positive, service, request, response, null);
            }
            catch (NegativeResponseException ex)
            {
                var m = ex.Kwp2000Message;
                var nrc = m.Body.Count > 1 ? m.Body[1] : (byte?)null;
                return new ProbeResult(ProbeOutcome.Negative, service, request, m, nrc);
            }
            catch (TimeoutException)
            {
                _kwpCommon.Interface.ClearReceiveBuffer();
                return new ProbeResult(ProbeOutcome.NoResponse, service, request, null, null);
            }
            catch (InvalidOperationException)
            {
                // ReceiveFollowUp's "Unexpected response"/address checks -- something framed came
                // back that isn't ours. Drain and move on rather than abort the whole sweep.
                _kwpCommon.Interface.ClearReceiveBuffer();
                return new ProbeResult(ProbeOutcome.Unexpected, service, request, null, null);
            }
            finally
            {
                _kwpCommon.Interface.ReadTimeout = savedTimeout;
                VerboseLog = savedVerbose;
            }
        }

        private const int MaxSendAttempts = 1;

        public Kwp2000Message SendReceive(
            Service service, byte[] body, bool excludeAddresses = false)
        {
            for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
            {
                SendMessage(service, body, excludeAddresses);

                Kwp2000Message message;
                try
                {
                    message = ReceiveMessage();
                }
                catch (TimeoutException) when (attempt < MaxSendAttempts)
                {
                    Log.WriteLine(
                        $"No response to {service}; resending (attempt {attempt + 1}/{MaxSendAttempts})...");
                    // Discard any stray/partial bytes before resending so a late-arriving fragment
                    // of this timed-out attempt can't be misread as the start of the next response.
                    _kwpCommon.Interface.ClearReceiveBuffer();
                    continue;
                }

                return ReceiveFollowUp(service, message);
            }

            // Unreachable: the final iteration's TimeoutException (if any) isn't caught above, so
            // it propagates instead of falling through to here.
            throw new TimeoutException($"No response to {service} after {MaxSendAttempts} attempts.");
        }

        /// <summary>
        /// Validates a just-received response to <paramref name="service"/>, following any
        /// "response pending" (0x78) NAKs by waiting for the real response -- split out from
        /// <see cref="SendReceive"/> so only the very first receive of a fresh attempt is eligible
        /// for the resend-on-timeout behavior above; a timeout while waiting out a response-pending
        /// chain means the ECU already acknowledged the request and is just slow, so resending the
        /// request from scratch here would be wrong.
        /// </summary>
        private Kwp2000Message ReceiveFollowUp(Service service, Kwp2000Message message)
        {
            while (true)
            {
                if (message.SrcAddress.HasValue)
                {
                    if (message.SrcAddress != _controllerAddress)
                    {
                        throw new InvalidOperationException($"Unexpected SrcAddress: {message.SrcAddress:X2}");
                    }

                    if (message.DestAddress != _testerAddress)
                    {
                        throw new InvalidOperationException($"Unexpected DestAddress: {message.DestAddress:X2}");
                    }
                }

                if ((byte)message.Service == 0x7F)
                {
                    if (message.Body[0] == (byte)service &&
                        message.Body[1] == (byte)ResponseCode.reqCorrectlyRcvdRspPending)
                    {
                        message = ReceiveMessage();
                        continue;
                    }
                    throw new NegativeResponseException(message);
                }

                if (!message.IsPositiveResponse(service))
                {
                    throw new InvalidOperationException($"Unexpected response: {message.Service}");
                }

                return message;
            }
        }

        public void SendMessage(Service service, byte[] body, bool excludeAddresses = false)
        {
            static void Sleep(int ms)
            {
                var maxTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000 * ms;
                while (Stopwatch.GetTimestamp() < maxTick)
                    ;
            }

            Kwp2000Message message;
            if (excludeAddresses)
            {
                message = new Kwp2000Message(service, body);
            }
            else
            {
                message = new Kwp2000Message(
                    _controllerAddress, _testerAddress, service, body);
            }
            var checksum = message.CalcChecksum();
            Sleep(P3);

            if (FastTiming)
            {
                // One batched write (LinuxInterface/FTDI send it back-to-back and drain the K-line
                // echo in one go) — drops the per-byte P4 delay that otherwise dominates a short
                // request. Same primitive the EDC16 flash transfer uses.
                var frame = new List<byte>(message.HeaderBytes);
                frame.Add((byte)message.Service);
                frame.AddRange(message.Body);
                frame.Add(checksum);
                _kwpCommon.WriteBytes(frame.ToArray());
            }
            else
            {
                foreach (var b in message.HeaderBytes)
                {
                    _kwpCommon.WriteByte(b);
                    Sleep(P4);
                }

                _kwpCommon.WriteByte((byte)message.Service);
                Sleep(P4);

                foreach (var b in message.Body)
                {
                    _kwpCommon.WriteByte(b);
                    Sleep(P4);
                }

                _kwpCommon.WriteByte(checksum);
            }

            // See VerboseLog's own doc comment -- File-only for high-volume callers (a full EEPROM
            // rewrite can call this ~512 times) instead of the default/visible destination.
            if (!QuietFrames)
            {
                Log.WriteLine($"Sent: {message}", VerboseLog ? LogDest.All : LogDest.File);
            }
        }

        /// <summary>
        /// Reads and parses one KWP2000 response message. Logs each header field as soon as it's
        /// read (format/address/length/service bytes -- never the body, which for a bulk transfer
        /// caller like ME7.5's memory-read loop could be hundreds of calls long and would flood the
        /// log) specifically so that if a ReadByte call times out or fails partway through, the log
        /// still shows exactly how far the response got before failing -- e.g. "did the format byte
        /// arrive at all?" -- rather than only the caller's own generic "Failed to read byte from
        /// UART" with no indication of what, if anything, came back first. The final "Received:
        /// ..." line (unchanged from before) still covers the full parsed message on success,
        /// including its body.
        /// </summary>
        public Kwp2000Message ReceiveMessage()
        {
            // See VerboseLog's own doc comment -- File-only for high-volume callers instead of the
            // default/visible destination. Computed once per call rather than inline at each site
            // below purely for brevity; VerboseLog itself can still change between calls (nothing
            // here caches it beyond one ReceiveMessage invocation).
            var dest = VerboseLog ? LogDest.All : LogDest.File;
            // QuietFrames (Live Data) suppresses the whole per-frame trace; otherwise route it to
            // the output pane (verbose) or the file only.
            void Rx(string line) { if (!QuietFrames) Log.WriteLine(line, dest); }

            var formatByte = _kwpCommon.ReadByte();
            Rx($"  RX: format=0x{formatByte:X2}");

            byte? destAddress = null;
            byte? srcAddress = null;
            if ((formatByte & 0x80) == 0x80)
            {
                destAddress = _kwpCommon.ReadByte();
                srcAddress = _kwpCommon.ReadByte();
                Rx($"  RX: dest=0x{destAddress:X2} src=0x{srcAddress:X2}");
            }
            byte? lengthByte = null;
            if ((formatByte & 63) == 0)
            {
                lengthByte = _kwpCommon.ReadByte();
                Rx($"  RX: length=0x{lengthByte:X2}");
            }
            var bodyLength = (lengthByte ?? (formatByte & 63)) - 1;
            var service = (Service)_kwpCommon.ReadByte();
            Rx($"  RX: service=0x{(byte)service:X2} (bodyLength={bodyLength})");
            var body = new List<byte>();
            for (var i = 0; i < bodyLength; i++)
            {
                body.Add(_kwpCommon.ReadByte());
            }
            var checksum = _kwpCommon.ReadByte();

            var message = new Kwp2000Message(
                formatByte, destAddress, srcAddress, lengthByte, service, body, checksum);
            Rx($"Received: {message}");
            return message;
        }

        private readonly IKwpCommon _kwpCommon;
        private readonly byte _controllerAddress;

        public KW2000Dialog(IKwpCommon kwpCommon, byte controllerAddress)
        {
            _kwpCommon = kwpCommon;
            _controllerAddress = controllerAddress;
        }
    }
}
