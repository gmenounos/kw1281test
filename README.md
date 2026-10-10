# kw1281test
VW KW1281 Protocol Test Tool

This tool can send some KW1281 (and a few KW2000) commands over a dumb serial->KKL or USB->KKL cable.
Ross-Tech HEX-USB and VasyaDiagnost/Car2diag cables are supported too; see "Diagnostic adapters" below.
Functionality includes reading/writing the EEPROMs of VW MKIV Golf/Jetta/Beetle/Passat instrument clusters and Comfort Control Modules, reading and clearing fault codes, changing the software coding of modules, performing an actuator test of various modules and retrieving the SAFE code of the Delco Premium V radio.

The tool is written in C#, targetting .NET 10.0 and runs under Windows 10/11 (most serial ports), macOS and Linux (macOS/Linux need an FTDI serial port and D2xx drivers). It may also run under
Windows 10/11.

You can download a precompiled version for Windows, macOS and Linux from the Releases page: https://github.com/gmenounos/kw1281test/releases/

Otherwise, here's how to build it yourself:

##### Compiling the tool

1. You will need the .NET Core SDK,
which you can find here: https://dotnet.microsoft.com/download
(Click on the "Download .NET Core SDK" link and follow the instructions) or Microsoft Visual Studio
(free Community Edition here: https://visualstudio.microsoft.com/vs/community/)

2. Download the source code: https://github.com/gmenounos/kw1281test/archive/master.zip
and unzip it into a folder on your computer.

3. Open up a command prompt on your computer and go into the folder where you unzipped
the source code. Type `dotnet build` to build the tool.
Or, load up the project in Visual Studio and Ctrl-Shift-B.

4. You can run the tool by typing `dotnet run`

```
Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]
                
PORT = COM1|COM2|etc. (Windows)
    /dev/ttyXXXX (Linux)
    AABBCCDD (macOS/Linux FTDI cable serial number)
    vasya:AABBCCDD (VasyaDiagnost/Car2diag cable serial number, Windows/macOS)
    hex:AABBCCDD (Ross-Tech HEX-USB cable serial number, Windows/macOS)
    hex:COM1 (Ross-Tech HEX-USB cable on the Virtual COM Port driver)
BAUD = 10400|9600|etc.
ADDRESS = Controller address, e.g. 1 (ECU), 17 (cluster), 46 (CCM), 56 (radio)
COMMAND =
    ActuatorTest
    AdaptationRead CHANNEL [LOGIN]
        CHANNEL = Channel number (0-99)
        LOGIN = Optional login (0-65535)
    AdaptationSave CHANNEL VALUE [LOGIN]
        CHANNEL = Channel number (0-99)
        VALUE = Channel value (0-65535)
        LOGIN = Optional login (0-65535)
    AdaptationTest CHANNEL VALUE [LOGIN]
        CHANNEL = Channel number (0-99)
        VALUE = Channel value (0-65535)
        LOGIN = Optional login (0-65535)
    AutoScan
    BasicSetting GROUP
        GROUP = Group number (0-255)
        (Group 0: Raw controller data)
    ClarionVWPremium4SafeCode
    ClearFaultCodes
    DelcoVWPremium5SafeCode
    DumpEdc15Eeprom [FILENAME]
        FILENAME = Optional filename
    DumpEdc15Flash [SPEED] [FILENAME]
        SPEED = low|medium|high link speed (default medium)
        FILENAME = Optional filename
    DumpEdc15FlashBoot [FILENAME]
        FILENAME = Optional filename
        (ECU must be physically placed into C167 hardware boot mode first)
    DumpEdc16Flash [SPEED] [FILENAME]
        SPEED = low|medium|high link speed (default medium)
        FILENAME = Optional filename
    DumpEeprom START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 0) or hex (e.g. 0x0)
        LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. 0x800)
        FILENAME = Optional filename
    DumpEeprom FILENAME
        (For Airbag/Cluster address only) Dumps the whole EEPROM (size
         auto-detected from ReadIdent).
    DumpMarelliMem START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 3072) or hex (e.g. 0xC00)
        LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. 0x400)
        FILENAME = Optional filename
    DumpMem START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 8192) or hex (e.g. 0x2000)
        LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. 0x10000)
        FILENAME = Optional filename
    DumpRam START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 8192) or hex (e.g. 0x2000)
        LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. 0x10000)
        FILENAME = Optional filename
    DumpRBxMem START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 66560) or hex (e.g. 0x10400)
        LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. 0x400)
        FILENAME = Optional filename
    DumpRom START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 8192) or hex (e.g. 0x2000)
        LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. 0x10000)
        FILENAME = Optional filename
    FindLogins LOGIN
        LOGIN = Known good login (0-65535)
    GetSKC
    GroupRead GROUP [fastinit]
        GROUP = Group number (0-255)
        (Group 0: Raw controller data)
        fastinit = Connect with an ISO 14230 fast init (only a later CAN-init EDC16
                   that ignores a cold slow init needs this)
    LoadEdc15Eeprom [START] FILENAME
        START = Optional start address in decimal (e.g. 0) or hex (e.g. 0x0), default 0
        FILENAME = Name of file containing binary data to write into the EDC15 EEPROM
    LoadEdc15Flash [SPEED] [full] [noverify] FILENAME
        SPEED = low|medium|high link speed (default medium)
        full = Force a whole-chip write instead of the per-sector checksum decide
        noverify = Skip the post-write per-sector checksum verify
        FILENAME = Name of file containing the flash image to write
    LoadEdc15FlashBoot FILENAME
        FILENAME = Name of file containing the flash image to write
        (ECU must be physically placed into C167 hardware boot mode first)
    LoadEdc16Flash FILENAME [SPEED] [full] [unverified] [fastinit]
        FILENAME = Name of file containing the 2 MB flash image to write
        SPEED = low|medium|high link speed (default medium)
        full = Force a whole-chip write instead of the per-block checksum decide
        unverified = Skip the post-write checksum verify
        fastinit = Prime with an ISO 14230 fast init before the slow init (only a later
                   CAN-init EDC16 that ignores a cold slow init needs this)
    LoadEeprom START FILENAME
        START = Start address in decimal (e.g. 0) or hex (e.g. 0x0)
        FILENAME = Name of file containing binary data to load into EEPROM
    MapEeprom
    ReadFaultCodes
    ReadIdent
    ReadEeprom ADDRESS
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ReadRAM ADDRESS
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ReadROM ADDRESS
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ReadSoftwareVersion
    Reset
    SetSoftwareCoding CODING WORKSHOP
        CODING = Software coding in decimal (e.g. 4361) or hex (e.g. 0x1109)
        WORKSHOP = Workshop code in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ToggleRB4Mode
    WriteEdc15Eeprom ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ... ADDRESSn VALUEn]
        ADDRESS = EEPROM address in decimal (0-511) or hex (0x00-0x1FF)
        VALUE = Value to be stored in decimal (0-255) or hex (0x00-0xFF)
    WriteEeprom ADDRESS VALUE
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
        VALUE = Value in decimal (e.g. 138) or hex (e.g. 0x8A)
    WriteRAM ADDRESS VALUE
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
        VALUE = Value in decimal (e.g. 138) or hex (e.g. 0x8A)
```

##### Diagnostic adapters

Ross-Tech HEX-USB and VasyaDiagnost/Car2diag cables are not dumb KKL cables: their firmware
performs the slow 5-baud controller wake-up itself when asked, and then passes the KW1281 bytes
through. kw1281test talks to them in that mode, so give the cable as the PORT and the K-line
speed (10400, 9600, ...) as the BAUD, just like with any other cable:

```
KW1281Test vasya:A5028BIA 10400 17 ReadIdent
KW1281Test hex:RT000001 10400 17 ReadIdent
KW1281Test hex:COM3 10400 17 ReadIdent
```

The serial number is the cable's 8-character USB serial number. In Device Manager it is part of
the "Device instance path" on the device's Details tab: `USB\VID_0403&PID_FA24\RT000001` for a
HEX-USB on Ross-Tech's driver or WinUSB, `FTDIBUS\VID_0403+PID_FA3F+A5028BIAA\0000` for a
VasyaDiagnost on the FTDI driver (the serial number there is followed by the channel letter `A`).

- **VasyaDiagnost/Car2diag** (USB 0403:FA3F) uses the FTDI D2XX driver that comes with the
  cable on Windows, and the FTDI D2XX library on macOS.
- **Ross-Tech HEX-USB** (USB 0403:FA24) on Windows works with whichever driver the cable already has:
    - Ross-Tech's own driver: it is FTDI's D2XX driver built for Ross-Tech, so FTDI's own D2XX
      library works with it - put `ftd2xx64.dll` (from FTDI's D2XX driver package, `amd64`
      folder) next to kw1281test, or install FTDI's D2XX driver. Without it kw1281test falls back
      to Ross-Tech's RT-USB library: the 32-bit one ships with the driver package, the 64-bit
      one (RTUS64.dll) with VCDS. `KW1281_FTDI_DLL` can point at any of these.
    - WinUSB (e.g. installed with [Zadig](https://zadig.akeo.ie/)): kw1281test drives the
      cable's FTDI chip directly; no Ross-Tech library is needed.
    - Ross-Tech's Virtual COM Port driver
      (https://www.ross-tech.com/vag-com/usb/virtual-com-port.php): use `hex:COMx` as the PORT.

  On macOS the cable is opened through the FTDI D2XX library.

Adapter-assisted wake-up does not support KWP2000 even parity yet.

##### Credits
- Protocol Info: https://www.blafusel.de/obd/obd2_kw1281.html  
- VW Radio Reverse Engineering Info: https://github.com/mnaberez/vwradio  
- 6502bench SourceGen: https://6502bench.com/
- EDC15 flashing info and seed/key algorithm: https://github.com/fjvva/ecu-tool
- Contributions
    - [IJskonijn](https://github.com/IJskonijn)
    - [jpadie](https://github.com/jpadie)
    - [kerekt](https://github.com/kerekt)
    - [Olivier Fauchon](https://github.com/ofauchon)
    - [Jonathan Klamroth](https://github.com/jonnykl)
    - [Martin Sestak](https://github.com/poure-1)
    - [Dragonslab53](https://github.com/Dragonslab53)
    - [DiagProf](https://github.com/DiagProf)
    - [magna413](https://github.com/magna413)
    - [bbear3d](https://github.com/bbear3d)