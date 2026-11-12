using BitFab.KW1281Test.Interface;
using System.Buffers.Binary;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace BitFab.KW1281Test.Tests.Interface
{
    /// <summary>
    /// Ross-Tech HEX-USB support. The FTDI values come from USB captures of the cable
    /// (0403:FA24, an FT232R) under Ross-Tech's own driver: cluster, comfort and airbag
    /// modules, seven sessions in total.
    /// </summary>
    [TestClass]
    public class RossTechInterfaceTests
    {
        [TestMethod]
        [DataRow(9600, 0x4138)]    // port open
        [DataRow(10400, 0x4120)]   // cluster
        [DataRow(115200, 0x001A)]  // the cable firmware's command channel
        public void BaudRateDivisorMatchesTheCapture(int baudRate, int expectedValue)
        {
            var (value, index) = FtdiProtocol.EncodeBaudRate(baudRate);
            Assert.AreEqual((ushort)expectedValue, value);
            Assert.AreEqual((ushort)0, index);
        }

        [TestMethod]
        public void LinePropertiesMatchTheCapture()
        {
            // SET_DATA 0x0008 is 8N1, as in the capture; break is bit 14.
            Assert.AreEqual((ushort)0x0008, FtdiProtocol.EncodeLineProperties(Parity.None, breakOn: false));
            Assert.AreEqual((ushort)0x4008, FtdiProtocol.EncodeLineProperties(Parity.None, breakOn: true));
            Assert.AreEqual((ushort)0x0208, FtdiProtocol.EncodeLineProperties(Parity.Even, breakOn: false));
        }

        [TestMethod]
        public void PayloadSkipsTheStatusBytesOfEveryPacket()
        {
            // The wake-up reply from the capture: 01 60 is status, then 11 data bytes.
            var wakeupReply = Convert.FromHexString("01604D0A848C250000018AE10F");
            var output = new byte[64];
            var count = FtdiProtocol.ExtractPayload(wakeupReply, output);
            CollectionAssert.AreEqual(Convert.FromHexString("4D0A848C250000018AE10F"), output[..count]);

            // A packet of status bytes only carries no data.
            Assert.AreEqual(0, FtdiProtocol.ExtractPayload([0x01, 0x60], output));

            // Two packets in one read: a full 64-byte one and a short one.
            var first = new byte[64];
            first[0] = 0x01; first[1] = 0x60;
            for (var i = 2; i < 64; i++) first[i] = (byte)i;
            var both = first.Concat(new byte[] { 0x01, 0x60, 0xAA }).ToArray();
            var big = new byte[128];
            count = FtdiProtocol.ExtractPayload(both, big);
            Assert.AreEqual(63, count);
            Assert.AreEqual((byte)2, big[0]);
            Assert.AreEqual((byte)63, big[61]);
            Assert.AreEqual((byte)0xAA, big[62]);
        }

        [TestMethod]
        public void DriverServiceIsClassified()
        {
            Assert.AreEqual(UsbCableDriver.WinUsb, UsbCableLocator.ClassifyService("WinUSB"));
            Assert.AreEqual(UsbCableDriver.RossTech, UsbCableLocator.ClassifyService("RT-USB"));
            Assert.AreEqual(UsbCableDriver.RossTech, UsbCableLocator.ClassifyService("RTUS64"));
            Assert.AreEqual(UsbCableDriver.None, UsbCableLocator.ClassifyService(null));
            Assert.AreEqual(UsbCableDriver.Other, UsbCableLocator.ClassifyService("FTDIBUS"));
        }

        [TestMethod]
        public void LibraryResolverSelectsTheDllMatchingTheProcessArchitecture()
        {
            var root = Path.Combine(Path.GetTempPath(), "kw1281test-rtusb-" + Guid.NewGuid().ToString("N"));
            var application = Path.Combine(root, "app");
            var versionedVcds = Path.Combine(root, "VCDS 18.2.0");
            Directory.CreateDirectory(application);
            Directory.CreateDirectory(versionedVcds);

            try
            {
                var x86 = Path.Combine(application, "RT-USB.dll");
                var x64 = Path.Combine(versionedVcds, "RTUS64.dll");
                WriteMinimalPe(x86, 0x014C);
                WriteMinimalPe(x64, 0x8664);

                Assert.AreEqual(x86,
                    RossTechInterface.FindRossTechLibrary(application, [versionedVcds], Architecture.X86));
                Assert.AreEqual(x64,
                    RossTechInterface.FindRossTechLibrary(application, [versionedVcds], Architecture.X64));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void LibraryIsFoundInTheDriverPackage()
        {
            // The Ross-Tech driver package brings RT-USB.DLL along, so VCDS is not needed.
            var repository = Path.Combine(Path.GetTempPath(), "kw1281test-repo-" + Guid.NewGuid().ToString("N"));
            var rossTech = Path.Combine(repository, "rt-usb64.inf_amd64_936b05782467367d");
            var ftdi = Path.Combine(repository, "ftdibus.inf_amd64_4f6d99ceb69de87e");
            Directory.CreateDirectory(rossTech);
            Directory.CreateDirectory(ftdi);
            try
            {
                var packages = RossTechInterface.FindDriverPackageDirectories(repository).ToArray();
                CollectionAssert.AreEqual(new[] { rossTech }, packages);

                // The package holds a 32-bit library: right for an x86 build, wrong for x64.
                var dll = Path.Combine(rossTech, "RT-USB.DLL");
                WriteMinimalPe(dll, 0x014C);
                var empty = Path.Combine(repository, "app");
                Directory.CreateDirectory(empty);
                // Case-insensitive file system: RT-USB.DLL and RT-USB.dll are the same file.
                Assert.AreEqual(dll, RossTechInterface.FindRossTechLibrary(empty, packages, Architecture.X86),
                    ignoreCase: true);
                Assert.IsNull(RossTechInterface.FindRossTechLibrary(empty, packages, Architecture.X64));
            }
            finally
            {
                Directory.Delete(repository, recursive: true);
            }
        }

        [TestMethod]
        [DataRow("hex:RT00001")]    // seven characters
        [DataRow("hex:RT0000011")]  // nine
        [DataRow("hex:COM")]        // no number
        [DataRow("hex:")]
        [DataRow("vasya:A5028")]
        public void SomethingThatIsNeitherASerialNorAPortIsRejected(string portName)
        {
            var error = Assert.ThrowsExactly<ArgumentException>(() => Program.OpenPort(portName, 10400));

            StringAssert.Contains(error.Message, "serial number");
        }

        private static void WriteMinimalPe(string path, ushort machine)
        {
            var image = new byte[0x80];
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0), 0x5A4D); // MZ
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x40);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x40), 0x00004550); // PE\0\0
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x44), machine);
            File.WriteAllBytes(path, image);
        }
    }
}
