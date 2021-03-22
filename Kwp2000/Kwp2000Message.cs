using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test.Kwp2000
{
    public class Kwp2000Message
    {
        public byte FormatByte { get; }

        public byte? DestAddress { get; }

        public byte? SrcAddress { get; }

        public byte? LengthByte { get; }

        public DiagnosticService Service { get; }

        public List<byte> Body { get; }

        public byte Checksum { get => CalcChecksum(); }

        public Kwp2000Message(
            DiagnosticService service, IList<byte> body)
        {
            FormatByte = CalcFormatByte(body, excludeAddresses: true);
            DestAddress = null;
            SrcAddress = null;
            LengthByte = CalcLengthByte(body);
            Service = service;
            Body = new List<byte>(body);
        }

        public Kwp2000Message(
            byte destAddress, byte srcAddress, DiagnosticService service, IList<byte> body)
        {
            FormatByte = CalcFormatByte(body);
            DestAddress = destAddress;
            SrcAddress = srcAddress;
            LengthByte = CalcLengthByte(body);
            Service = service;
            Body = new List<byte>(body);
        }

        public Kwp2000Message(
            byte formatByte, byte? destAddress, byte? srcAddress, byte? lengthByte,
            DiagnosticService service,
            IList<byte> body, byte checksum)
        {
            FormatByte = formatByte;
            DestAddress = destAddress;
            SrcAddress = srcAddress;
            LengthByte = lengthByte;
            Service = service;
            Body = new List<byte>(body);

            Debug.Assert(FormatByte == CalcFormatByte(Body, !destAddress.HasValue));
            Debug.Assert(LengthByte == CalcLengthByte(Body));
            Debug.Assert(checksum == CalcChecksum());
        }

        public IEnumerable<byte> HeaderBytes
        {
            get
            {
                yield return FormatByte;
                if (DestAddress.HasValue)
                {
                    yield return DestAddress.Value;
                }
                if (SrcAddress.HasValue)
                {
                    yield return SrcAddress.Value;
                }
                if (LengthByte.HasValue)
                {
                    yield return LengthByte.Value;
                }
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach(var b in HeaderBytes)
            {
                sb.Append($"{b:X2} ");
            }
            sb.Append($"{(byte)Service:X2}");
            sb.Append(Utils.Dump(Body));
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

        private static byte CalcFormatByte(IList<byte> body, bool excludeAddresses = false)
        {
            var length = body.Count + 1;
            byte formatByte = (byte)(length > 63 ? 0 : length);
            if (!excludeAddresses)
            {
                formatByte |= 0x80;
            }
            return formatByte;
        }

        private static byte? CalcLengthByte(IList<byte> body)
        {
            var length = body.Count + 1;
            return length > 63 ? (byte)length : null;
        }

        private byte CalcChecksum()
        {
            return (byte)(
                FormatByte +
                (DestAddress ?? 0) +
                (SrcAddress ?? 0) +
                (LengthByte ?? 0) +
                (byte)Service +
                Body.Sum(b => b));
        }
    }
}
