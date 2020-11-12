using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test.Kwp2000
{
    public class Kwp2000Message
    {
        public byte Header { get => (byte)(0x80 + 1 + Body.Count); }

        public byte DestAddress { get; }

        public byte SrcAddress { get; }

        public DiagnosticService Service { get; }

        public List<byte> Body { get; }

        public byte Checksum { get => CalcChecksum(); }

        public Kwp2000Message(
            byte destAddress, byte srcAddress, DiagnosticService service, IEnumerable<byte> body)
        {
            DestAddress = destAddress;
            SrcAddress = srcAddress;
            Service = service;
            Body = new List<byte>(body);
        }

        public Kwp2000Message(
            byte header, byte destAddress, byte srcAddress, DiagnosticService service,
            List<byte> body, byte checksum)
            : this(destAddress, srcAddress, service, body)
        {
            Debug.Assert(header == Header);
            var expectedChecksum = CalcChecksum();
            Debug.Assert(checksum == expectedChecksum);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"{Header:X2}");
            sb.Append($" {DestAddress:X2}");
            sb.Append($" {SrcAddress:X2}");
            sb.Append($" {(byte)Service:X2}");
            sb.Append(DumpHex(Body));
            sb.Append($" {CalcChecksum():X2}");
            sb.Append($" ({DescribeService()})");
            return sb.ToString();
        }

        public bool IsPositiveResponse(DiagnosticService service)
        {
            return ((byte)service | 0x40) == (byte)Service;
        }

        public string DescribeService()
        {
            var serviceByte = (byte)Service;
            if (serviceByte == 0x7F)
            {
                return $"{(DiagnosticService)Body[0]} NAK {(ResponseCode)Body[1]}";
            }

            var bareServiceByte = (byte)(serviceByte & ~0x40);

            string bareServiceString;
            if (Enum.TryParse(bareServiceByte.ToString(), out DiagnosticService bareService))
            {
                bareServiceString = bareService.ToString();
            }
            else
            {
                bareServiceString = $"{bareServiceByte:X2}";
            }

            if ((serviceByte & 0x40) == 0x40)
            {
                return $"{bareServiceString} ACK";
            }
            else
            {
                return bareServiceString;
            }
        }

        private byte CalcChecksum()
        {
            return (byte)(
                (Header + DestAddress + SrcAddress + (byte)Service + Body.Sum(b => b)) & 0xFF);
        }

        private string DumpHex(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($" {b:X2}");
            }
            return sb.ToString();
        }
    }
}
