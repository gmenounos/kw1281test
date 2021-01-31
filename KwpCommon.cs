using BitFab.KW1281Test.Interface;
using System;

namespace BitFab.KW1281Test
{
    interface IKwpCommon
    {
        int WakeUp(byte controllerAddress, bool evenParity = false);

        byte ReadByte();

        /// <summary>
        /// Write a byte to the interface and receive its echo.
        /// </summary>
        /// <param name="b">The byte to write.</param>
        void WriteByte(byte b);

        byte ReadAndAckByte();

        void ReadComplement(byte b);
    }

    class KwpCommon : IKwpCommon
    {
        public int WakeUp(byte controllerAddress, bool evenParity)
        {
            _interface.BitBang5Baud(controllerAddress, evenParity);

            Logger.WriteLine("Reading sync byte");
            var syncByte = _interface.ReadByte();
            if (syncByte != 0x55)
            {
                throw new InvalidOperationException(
                    $"Unexpected sync byte: Expected $55, Actual ${syncByte:X2}");
            }

            var keywordLsb = _interface.ReadByte();
            Logger.WriteLine($"Keyword Lsb ${keywordLsb:X2}");

            var keywordMsb = ReadAndAckByte();
            Logger.WriteLine($"Keyword Msb ${keywordMsb:X2}");

            var protocolVersion = ((keywordMsb & 0x7F) << 7) + (keywordLsb & 0x7F);
            Logger.WriteLine($"Protocol is KW {protocolVersion} (8N1)");

            if (protocolVersion >= 2000)
            {
                ReadComplement(controllerAddress);
            }

            return protocolVersion;
        }

        public byte ReadByte()
        {
            return _interface.ReadByte();
        }

        public void WriteByte(byte b)
        {
            _interface.WriteByteAndDiscardEcho(b);
        }

        public byte ReadAndAckByte()
        {
            var b = _interface.ReadByte();
            WriteComplement(b);
            return b;
        }

        public void ReadComplement(byte b)
        {
            var expectedComplement = (byte)~b;
            var actualComplement = _interface.ReadByte();
            if (actualComplement != expectedComplement)
            {
                throw new InvalidOperationException(
                    $"Received complement ${actualComplement:X2} but expected ${expectedComplement:X2}");
            }
        }

        private void WriteComplement(byte b)
        {
            var complement = (byte)~b;
            _interface.WriteByteAndDiscardEcho(complement);
        }

        private readonly IInterface _interface;

        public KwpCommon(IInterface @interface)
        {
            _interface = @interface;
        }
    }
}
