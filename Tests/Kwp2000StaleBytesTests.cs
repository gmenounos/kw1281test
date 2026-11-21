using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using BitFab.KW1281Test.Interface;
using BitFab.KW1281Test.Kwp2000;
using Shouldly;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class Kwp2000StaleBytesTests
{
    [TestMethod]
    public void BytesLeftFromThePreviousReplyAreDroppedBeforeTheRequest()
    {
        byte[] request = [0x81, 0x28, 0xF1, 0x3E, 0xD8];
        byte[] reply = [0x81, 0xF1, 0x28, 0x7E, 0x18];
        var link = new FakeKLine(request, reply);
        link.Pending.Enqueue(0x83); // tail of a reply nobody read
        link.Pending.Enqueue(0xF1);
        link.Pending.Enqueue(0x28);

        var response = new KW2000Dialog(new KwpCommon(link), 0x28)
            .SendReceive(DiagnosticService.testerPresent, []);

        ((byte)response.Service).ShouldBe((byte)0x7E);
    }

    /// <summary>Echoes every written byte, and queues the reply once the whole request is sent.</summary>
    private sealed class FakeKLine(byte[] request, byte[] reply) : IInterface
    {
        public Queue<byte> Pending { get; } = new();
        private readonly List<byte> _sent = [];

        public byte ReadByte() =>
            Pending.Count > 0 ? Pending.Dequeue() : throw new TimeoutException();

        public void WriteByteRaw(byte b)
        {
            _sent.Add(b);
            Pending.Enqueue(b);
            if (_sent.SequenceEqual(request))
            {
                foreach (var r in reply)
                {
                    Pending.Enqueue(r);
                }
            }
        }

        public void SetBreak(bool on) { }
        public void ClearReceiveBuffer() => Pending.Clear();
        public void SetBaudRate(int baudRate) { }
        public void SetParity(Parity parity) { }
        public void SetDtr(bool on) { }
        public void SetRts(bool on) { }
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public void Dispose() { }
    }
}
