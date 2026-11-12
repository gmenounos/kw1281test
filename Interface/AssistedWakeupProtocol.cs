using System;

namespace BitFab.KW1281Test.Interface
{
    /// <summary>
    /// Implemented by adapters that perform the slow controller wake-up in their own
    /// firmware and then switch to a transparent byte transport.
    /// </summary>
    internal interface IAssistedWakeupInterface
    {
        /// <returns>The protocol version reported by the controller (e.g. 1281).</returns>
        int AssistedWakeUp(byte controllerAddress, bool evenParity);
    }

    /// <summary>
    /// The request/response framing shared by adapters that perform the slow controller
    /// wake-up in their own firmware: Ross-Tech HEX and the VasyaDiagnost/Car2diag clones
    /// of it.
    ///
    /// Request:  $53 $07 $84 &lt;wake byte&gt; $1E $03 &lt;xor&gt;
    /// Response: $4D &lt;length&gt; $84 ... &lt;keyword lsb&gt; &lt;keyword msb&gt; &lt;xor&gt;
    ///           or $4D $05 $FF &lt;error&gt; &lt;xor&gt; when the adapter refuses.
    ///
    /// Both the checksum and the keyword positions were confirmed against captured sessions
    /// of the vendor software; see the tests for the exact frames.
    /// </summary>
    internal static class AssistedWakeupProtocol
    {
        /// <summary>
        /// Speed of the adapter's USB command channel. The 9600/10400 baud given on the
        /// command line is the K-line speed, which the adapter firmware handles itself.
        /// </summary>
        public const int AdapterCommandBaudRate = 115200;

        public static byte[] CreateWakeupRequest(byte controllerAddress)
        {
            // The wake byte carries odd parity, exactly like the 5-baud address it replaces:
            // a capture of $17 (cluster) was refused with $4D $05 $FF $03, and the same
            // request with $97 connected.
            var wakeupAddress = Utils.AdjustParity(controllerAddress, evenParity: false);
            var request = new byte[] { 0x53, 0x07, 0x84, wakeupAddress, 0x1e, 0x03, 0x00 };
            request[^1] = CalculateXor(request, request.Length - 1);
            return request;
        }

        /// <param name="adapterName">Used only in error messages, so the log names the cable.</param>
        public static int ReadWakeupResponse(Func<byte> readByte, string adapterName)
        {
            var marker = readByte();
            if (marker != 0x4d)
            {
                throw new InvalidOperationException(
                    $"Unexpected {adapterName} response marker: expected $4D, actual ${marker:X2}");
            }

            var length = readByte();
            // Read the whole frame, including short adapter error responses,
            // before deciding whether it contains successful wake-up keywords.
            if (length < 4)
            {
                throw new InvalidOperationException($"Invalid {adapterName} response length: {length}");
            }

            var response = new byte[length];
            response[0] = marker;
            response[1] = length;
            for (var index = 2; index < response.Length; index++)
            {
                response[index] = readByte();
            }

            var frame = Convert.ToHexString(response);
            if (CalculateXor(response, response.Length) != 0)
            {
                throw new InvalidOperationException($"Invalid {adapterName} response checksum: {frame}");
            }
            if (response[2] == 0xff && length >= 5)
            {
                throw new InvalidOperationException(
                    $"{adapterName} adapter rejected wake-up (error ${response[3]:X2}, response {frame}).");
            }
            if (response[2] != 0x84)
            {
                throw new InvalidOperationException(
                    $"Unexpected {adapterName} response command: ${response[2]:X2}, response {frame}");
            }
            if (length < 6)
            {
                throw new InvalidOperationException(
                    $"{adapterName} wake-up response has no keywords: {frame}");
            }

            var keywordLsb = response[^3];
            var keywordMsb = response[^2];
            var protocolVersion = ((keywordMsb & 0x7f) << 7) + (keywordLsb & 0x7f);
            Log.WriteLine($"Keyword Lsb ${keywordLsb:X2}");
            Log.WriteLine($"Keyword Msb ${keywordMsb:X2}");
            Log.WriteLine($"Protocol is KW {protocolVersion} (adapter-assisted)");
            return protocolVersion;
        }

        public static byte CalculateXor(byte[] values, int count)
        {
            byte checksum = 0;
            for (var index = 0; index < count; index++)
            {
                checksum ^= values[index];
            }
            return checksum;
        }

        /// <summary>
        /// Sends the wake-up request over <paramref name="port"/>, already switched to the
        /// adapter command speed, and decodes the reply.
        /// </summary>
        public static int WakeUp(IInterface port, byte controllerAddress, string adapterName)
        {
            var request = CreateWakeupRequest(controllerAddress);

            Log.WriteLine($"Requesting {adapterName} assisted wake-up for controller ${controllerAddress:X2}");
            foreach (var value in request)
            {
                port.WriteByteRaw(value);
            }

            // The captures show about 2.2 seconds between the request and the reply, so the
            // usual per-byte timeout is far too short.
            var previousTimeout = port.ReadTimeout;
            port.ReadTimeout = 5000;
            try
            {
                return ReadWakeupResponse(port.ReadByte, adapterName);
            }
            finally
            {
                port.ReadTimeout = previousTimeout;
            }
        }
    }
}
