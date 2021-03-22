using BitFab.KW1281Test.Kwp2000;
using System;   
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test
{
    internal class KW2000Dialog
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

        public bool SecurityAccess(byte accessMode)
        {
            const byte identificationOption = 0x94;
            var responseMsg = SendReceive(Service.readEcuIdentification, new byte[] { identificationOption });
            if (responseMsg.Body[0] != identificationOption)
            {
                throw new InvalidOperationException($"Received unexpected identificationOption: {responseMsg.Body[0]:X2}");
            }
            Logger.WriteLine(DumpAscii(responseMsg.Body.Skip(1)));

            const int maxTries = 16;
            for (var i = 0; i < maxTries; i++)
            {
                responseMsg = SendReceive(Service.securityAccess, new byte[] { accessMode });
                if (responseMsg.Body[0] != accessMode)
                {
                    throw new InvalidOperationException($"Received unexpected accessMode: {responseMsg.Body[0]:X2}");
                }
                var seedBytes = responseMsg.Body.Skip(1).ToArray();
                var seed = (uint)(
                    (seedBytes[0] << 24) |
                    (seedBytes[1] << 16) |
                    (seedBytes[2] << 8) |
                    seedBytes[3]);
                var key = CalcRB8Key(seed);

                try
                {
                    responseMsg = SendReceive(Service.securityAccess,
                        new[] {
                            (byte)(accessMode + 1),
                            (byte)((key >> 24) & 0xFF),
                            (byte)((key >> 16) & 0xFF),
                            (byte)((key >> 8) & 0xFF),
                            (byte)(key & 0xFF)
                        });

                    Logger.WriteLine("Success!!!");
                    return true;
                }
                catch(NegativeResponseException)
                {
                    if (i < (maxTries - 1))
                    {
                        Logger.WriteLine("Trying again.");
                    }
                }
            }

            return false;
        }

        internal void DumpEeprom(uint address, uint length, string dumpFileName)
        {
            StartDiagnosticSession(0x84, 0x14);

            Thread.Sleep(350);

            Logger.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpMemory(address, length, maxReadLength: 32, dumpFileName);
            Logger.WriteLine($"Saved EEPROM dump to {dumpFileName}");

            EcuReset(0x01);
        }

        private void DumpMemory(
            uint startAddr, uint length, byte maxReadLength, string fileName)
        {
            using var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough);
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = ReadMemoryByAddress(addr, readLength);
                if (blockBytes.Length != readLength)
                {
                    throw new InvalidOperationException(
                        $"Expected {readLength} bytes from ReadMemoryByAddress() but received {blockBytes.Length} bytes");
                }
                fs.Write(blockBytes, 0, blockBytes.Length);
                fs.Flush();
            }
        }

        private void StartDiagnosticSession(byte v1, byte v2)
        {
            var responseMessage = SendReceive(Service.startDiagnosticSession, new[] { v1, v2 });
            if (responseMessage.Body[0] != v1)
            {
                throw new InvalidOperationException($"Unexpected diagnosticMode: {responseMessage.Body[0]:X2}");
            }
        }

        private void EcuReset(byte value)
        {
            var responseMessage = SendReceive(Service.ecuReset, new[] { value });
        }

        private byte[] ReadMemoryByAddress(uint address, byte count)
        {
            var addressBytes = BitConverter.GetBytes(address);

            var responseMessage = SendReceive(Service.readMemoryByAddress,
                new byte[]
                {
                    addressBytes[2], addressBytes[1], addressBytes[0],
                    count
                });

            return responseMessage.Body.ToArray();
        }

        public Kwp2000Message SendReceive(
            Service service, byte[] body, bool excludeAddresses = false)
        {
            SendMessage(service, body, excludeAddresses);

            while (true)
            {
                var message = ReceiveMessage();

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
            Sleep(P3);

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

            _kwpCommon.WriteByte(message.Checksum);

            Logger.WriteLine($"Sent: {message}");
        }

        public Kwp2000Message ReceiveMessage()
        {
            var formatByte = _kwpCommon.ReadByte();
            byte? destAddress = null;
            byte? srcAddress = null;
            if ((formatByte & 0x80) == 0x80)
            {
                destAddress = _kwpCommon.ReadByte();
                srcAddress = _kwpCommon.ReadByte();
            }
            byte? lengthByte = null;
            if ((formatByte & 63) == 0)
            {
                lengthByte = _kwpCommon.ReadByte();
            }
            var bodyLength = (lengthByte ?? (formatByte & 63)) - 1;
            var service = (Service)_kwpCommon.ReadByte();
            var body = new List<byte>();
            for (var i = 0; i < bodyLength; i++)
            {
                body.Add(_kwpCommon.ReadByte());
            }
            var checksum = _kwpCommon.ReadByte();

            var message = new Kwp2000Message(
                formatByte, destAddress, srcAddress, lengthByte, service, body, checksum);
            Logger.WriteLine($"Received: {message}");
            return message;
        }

        private static string DumpAscii(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static string DumpHex(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($" {b:X2}");
            }
            return sb.ToString();
        }

        static uint CalcRB8Key(uint seed)
        {
            uint key =
                0xFB4ACBBA
                + (seed & 0x07DA06B8)
                + (~seed | 0x07DA06B8)
                - 2 * (seed & 0x00004000);
            return key;
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
