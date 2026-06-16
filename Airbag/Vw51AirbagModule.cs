using BitFab.KW1281Test.Interface;
using KW1281Test.Airbag;
using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Airbag
{
    public sealed class Vw51AirbagModule : IAirbagModule
    {
        private const int LogicalBaseAddress = 0x0800;
        private const byte MaxReadLength = 8;

        // Размеры EEPROM по версиям блока
        private const int EepromSizeVW51 = 512;  // 0x200
        private const int EepromSizeVW61 = 768;  // 0x300

        private readonly IKW1281Dialog _kwp1281;
        private readonly string _ecuText;

        internal Vw51AirbagModule(IKW1281Dialog kwp1281, string ecuText)
        {
            _kwp1281 = kwp1281;
            _ecuText = ecuText ?? string.Empty;
        }

        /// <summary>Версия блока: VW51 или VW61.</summary>
        public enum ModuleVersion { VW51, VW61 }

        private ModuleVersion _version = ModuleVersion.VW51;

        public bool IsSupportedIdent(string ecuIdent, out string reason)
        {
            var text = string.IsNullOrWhiteSpace(ecuIdent) ? _ecuText : ecuIdent;

            bool isAirbag = text.Contains("AIRBAG", StringComparison.OrdinalIgnoreCase);

            if (isAirbag && text.Contains("VW51", StringComparison.OrdinalIgnoreCase))
            {
                _version = ModuleVersion.VW51;
                reason = string.Empty;
                return true;
            }

            if (isAirbag && text.Contains("VW61", StringComparison.OrdinalIgnoreCase))
            {
                _version = ModuleVersion.VW61;
                reason = string.Empty;
                return true;
            }

            reason = "Not a supported VW Airbag module (expected VW51 or VW61)";
            return false;
        }

        public void PrepareSession()
        {
            Log.WriteLine("VW51 airbag: Login 0x4653");
            _kwp1281.Login(0x4653, 0);

            Log.WriteLine("VW51 airbag: send unlock block 1A 01 50 4D 00 (no response expected)");
            _kwp1281.SendBlock(new List<byte>
            {
                (byte)BlockTitle.WriteEeprom,
                0x01,
                0x50,
                0x4D,
                0x00
            });

        }

        public byte[] DumpEeprom(int startAddress, int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            try
            {
                PrepareSession();
                EnterRawReadMode();

                int absoluteAddress = ResolveAbsoluteAddress(startAddress);

                var result = new byte[length];
                int written = 0;
                int currentAddress = absoluteAddress;

                while (written < length)
                {
                    byte readLength = (byte)Math.Min(MaxReadLength, length - written);
                    var chunk = ReadRawChunk(currentAddress, readLength);

                    Buffer.BlockCopy(chunk, 0, result, written, chunk.Length);

                    written += chunk.Length;
                    currentAddress += chunk.Length;
                }

                // Завершаем raw-сессию — без этого ECU не возвращается
                // в нормальный режим и требует перезагрузки.
                Log.WriteLine("VW51 airbag: sending end-of-session frame 02 77 75");
                CommitWrite();

                return result;
            }
            catch
            {
                _kwp1281.SetDisconnected();
                throw;
            }
            finally
            {
                _kwp1281.SetDisconnected();
            }
        }

        public void ClearCrashData(byte fillValue = 0xFF)
        {
            if (_version == ModuleVersion.VW51)
            {
                // VW51: адреса 0x000-0x04F (80 байт)
                Log.WriteLine(
                    $"VW51 airbag: ClearCrashData (VW51) — 0x000-0x04F, значение 0x{fillValue:X2}");
                FillRange(0x000, 0x04F, fillValue);
            }
            else // VW61
            {
                // VW61: два диапазона:
                //   0x000-0x030 и 0x151-0x1EF
                Log.WriteLine(
                    $"VW51 airbag: ClearCrashData (VW61) — 0x000-0x030 и 0x151-0x1EF, значение 0x{fillValue:X2}");
                FillRange(0x000, 0x030, fillValue);
                FillRange(0x151, 0x1EF, fillValue);
            }
        }

        /// <summary>
        /// Заполняет диапазон файловых офсетов [startOffset..endOffset] включительно
        /// указанным байтом.
        /// </summary>
        private void FillRange(int startOffset, int endOffset, byte fillValue)
        {
            int length = endOffset - startOffset + 1;
            var data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = fillValue;

            Log.WriteLine(
                $"  FillRange 0x{startOffset:X3}-0x{endOffset:X3} ({length} байт) = 0x{fillValue:X2}");

            LoadEeprom(startOffset, data);
        }

        public void LoadEeprom(int startAddress, byte[] data)
        {
            if (startAddress < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startAddress));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length == 0)
            {
                throw new ArgumentException("EEPROM write buffer is empty.", nameof(data));
            }

            int absoluteAddress = ResolveAbsoluteAddress(startAddress);

            Log.WriteLine(
                $"VW51 airbag: LoadEeprom start=0x{startAddress:X4}, " +
                $"absolute=0x{absoluteAddress:X4}, length={data.Length}");

            try
            {
                PrepareSession();
                EnterRawReadMode(); // режим записи и чтения входят одинаково через 0x70

                for (int i = 0; i < data.Length; i++)
                {
                    int byteAbsoluteAddress = absoluteAddress + i;
                    byte page = (byte)(byteAbsoluteAddress >> 8);
                    byte index = (byte)(byteAbsoluteAddress & 0xFF);
                    byte value = data[i];

                    Log.WriteLine(
                        $"VW51 airbag: write byte [{i + 1}/{data.Length}] " +
                        $"page=0x{page:X2} index=0x{index:X2} (abs=0x{byteAbsoluteAddress:X4}) value=0x{value:X2}");

                    WriteRawByte(page, index, value);
                }

                Log.WriteLine("VW51 airbag: sending commit frame 02 77 75");
                CommitWrite();

                Log.WriteLine("VW51 airbag: LoadEeprom complete.");
            }
            catch
            {
                _kwp1281.SetDisconnected();
                throw;
            }
            finally
            {
                _kwp1281.SetDisconnected();
            }
        }

        private void EnterRawWriteMode()
        {
            var kwpCommon = _kwp1281.KwpCommon;

            var savedTimeout = kwpCommon.Interface.ReadTimeout;
            kwpCommon.Interface.ReadTimeout = 50;
            try
            {
                while (true) kwpCommon.ReadByte();
            }
            catch { /* буфер пуст */ }
            finally
            {
                kwpCommon.Interface.ReadTimeout = savedTimeout;
            }

            Log.WriteLine("VW51 airbag: enter raw write mode (0x70)");
            kwpCommon.WriteByte(0x70);

            var response = ReadRawFrame();
            Log.WriteLine(
                $"VW51 airbag: raw unlock response ({response.Length} bytes): " +
                BitConverter.ToString(response).Replace("-", " "));

            Log.WriteLine("VW51 airbag: acknowledge raw unlock with 0x2D");
            kwpCommon.Interface.WriteByteRaw(0x2D);

            for (int attempt = 0; attempt < 16; attempt++)
            {
                byte b = kwpCommon.Interface.ReadByte();
                Log.WriteLine($"VW51 airbag: post-ack byte[{attempt}] = 0x{b:X2}");
                if (b == 0x71)
                {
                    Log.WriteLine("VW51 airbag: raw write mode confirmed (0x71)");
                    return;
                }
            }
            throw new InvalidOperationException("VW51 airbag: 0x71 not received after 0x2D.");
        }

        private void CommitWrite()
        {
            // Финализирующий фрейм: 02 77 75
            // Без него ECU не записывает данные из буфера в EEPROM постоянно.
            var request = new byte[] { 0x02, 0x77, 0x00 };
            request[2] = ComputeXor(request, 2);
            SendRawBytes(request);
        }

        private void WriteRawByte(byte page, byte index, byte value)
        {
            // Frame format: 0D 73 <page> <index> 01 <value> FF FF FF FF FF FF FF <xor>
            // index = absoluteAddress & 0xFF
            // 13 bytes before XOR, XOR is XOR of all 13 preceding bytes.
            var request = new byte[]
            {
                0x0D, 0x73, page,
                index,
                0x01,
                value,
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                0x00  // placeholder for XOR
            };
            request[request.Length - 1] = ComputeXor(request, request.Length - 1);

            SendRawBytes(request);

            // Read and validate the acknowledgement frame
            var response = ReadRawFrame();
            ValidateRawWriteResponse(response, page, index, value);
        }

        private static void ValidateRawWriteResponse(byte[] response, byte page, byte index, byte value)
        {
            // Minimum sanity check: response must not be empty and XOR must match.
            if (response.Length < 2)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag write ack too short ({response.Length} bytes) " +
                    $"for page=0x{page:X2} index=0x{index:X2} value=0x{value:X2}.");
            }

            byte expectedChecksum = ComputeXor(response, response.Length - 1);
            byte actualChecksum = response[response.Length - 1];
            if (expectedChecksum != actualChecksum)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag write ack checksum mismatch for page=0x{page:X2} index=0x{index:X2} value=0x{value:X2}. " +
                    $"Expected 0x{expectedChecksum:X2}, got 0x{actualChecksum:X2}.");
            }
        }

        private void EnterRawReadMode()
        {
            var kwpCommon = _kwp1281.KwpCommon;

            kwpCommon.Interface.ClearReceiveBuffer();

            Log.WriteLine("VW51 airbag: enter raw read mode (0x70)");
            kwpCommon.WriteByte(0x70);

            var response = new List<byte>();
            while (true)
            {
                byte b = kwpCommon.ReadByte();
                response.Add(b);

                if (b == 0x2D)
                {
                    break;
                }

                if (response.Count > 64)
                {
                    throw new InvalidOperationException(
                        "VW51 airbag raw unlock response is too long.");
                }
            }

            Log.WriteLine("VW51 airbag: acknowledge raw unlock with 0x2D");
            kwpCommon.WriteByte(0x2D);

            byte ready = kwpCommon.ReadByte();
            if (ready != 0x71)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag did not enter raw read mode. Expected 0x71, got 0x{ready:X2}.");
            }

            Log.WriteLine("VW51 airbag: raw read mode confirmed (0x71)");
        }

        private byte[] ReadRawChunk(int absoluteAddress, byte count)
        {
            byte addressHi = (byte)(absoluteAddress >> 8);
            byte addressLo = (byte)(absoluteAddress & 0xFF);

            var request = new byte[] { 0x05, 0x72, addressHi, addressLo, count, 0x00 };
            request[5] = ComputeXor(request, request.Length - 1);

            SendRawBytes(request);

            var response = ReadRawFrame();
            ValidateRawReadResponse(response, addressHi, addressLo, count);

            var data = new byte[count];
            Buffer.BlockCopy(response, 5, data, 0, count);
            return data;
        }

        private byte[] ReadRawFrame()
        {
            var kwpCommon = _kwp1281.KwpCommon;

            int payloadLength = kwpCommon.ReadByte();
            var frame = new byte[payloadLength + 1];
            frame[0] = (byte)payloadLength;

            for (int i = 0; i < payloadLength; i++)
            {
                frame[i + 1] = kwpCommon.ReadByte();
            }

            return frame;
        }

        private void SendRawBytes(byte[] bytes)
        {
            var kwpCommon = _kwp1281.KwpCommon;

            foreach (byte b in bytes)
            {
                kwpCommon.WriteByte(b);
            }
        }

        private static void ValidateRawReadResponse(
            byte[] response,
            byte addressHi,
            byte addressLo,
            byte count)
        {
            if (response.Length != count + 6)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag raw response has unexpected size. " +
                    $"Expected {count + 6}, got {response.Length}.");
            }

            if (response[1] != 0x8D)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag raw response title mismatch. Expected 0x8D, got 0x{response[1]:X2}.");
            }

            if (response[2] != addressHi || response[3] != addressLo)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag raw response address mismatch. " +
                    $"Expected 0x{addressHi:X2}{addressLo:X2}, got 0x{response[2]:X2}{response[3]:X2}.");
            }

            if (response[4] != count)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag raw response length mismatch. " +
                    $"Expected 0x{count:X2}, got 0x{response[4]:X2}.");
            }

            byte expectedChecksum = ComputeXor(response, response.Length - 1);
            byte actualChecksum = response[response.Length - 1];
            if (expectedChecksum != actualChecksum)
            {
                throw new InvalidOperationException(
                    $"VW51 airbag raw response checksum mismatch. " +
                    $"Expected 0x{expectedChecksum:X2}, got 0x{actualChecksum:X2}.");
            }
        }

        private static byte ComputeXor(byte[] bytes, int count)
        {
            byte value = 0;
            for (int i = 0; i < count; i++)
            {
                value ^= bytes[i];
            }

            return value;
        }

        private static int ResolveAbsoluteAddress(int address)
        {
            if (address < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(address));
            }

            // Чтобы команда ReadEeprom 0 читала с 0x0800, как в штатной программе.
            return address < LogicalBaseAddress
                ? LogicalBaseAddress + address
                : address;
        }
    }
}