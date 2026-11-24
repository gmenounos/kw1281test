# kw1281test
VW KW1281 Protocol Test Tool

This tool can send some KW1281 (and a few KW2000) commands over a dumb serial->KKL or USB->KKL cable.
If you have a legacy Ross-Tech USB cable, you can probably use that cable by
installing the Virtual COM Port drivers: https://www.ross-tech.com/vag-com/usb/virtual-com-port.php
Functionality includes reading/writing the EEPROMs of VW MKIV Golf/Jetta/Beetle/Passat instrument clusters and Comfort Control Modules, reading and clearing fault codes, changing the software coding of modules, performing an actuator test of various modules and retrieving the SAFE code of the Delco Premium V radio.

The tool is written in C#, targets .NET 10.0 and .NET 11.0, and runs under Windows 10/11 (most serial ports), macOS and Linux (macOS/Linux need an FTDI serial port and D2xx drivers). It may also run under
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
the source code. Type `dotnet build` to build both target frameworks.
Or, load up the project in Visual Studio and Ctrl-Shift-B.

4. Run one target framework by typing `dotnet run --framework net10.0` or
`dotnet run --framework net11.0`.

```
Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]
                
PORT = COM1|COM2|etc. (Windows)
    /dev/ttyXXXX (Linux)
    AABBCCDD (macOS/Linux FTDI cable serial number)
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
    DumpMarelliFlash [FILENAME] [--baud N]
        FILENAME = Optional filename
        --baud N = 9600 (default), 19200 or 38400
        (Dumps the cluster flash: eight 16 KB pages through the PPAGE window
         on 128 KB clusters, or $1000-$FFFF on 64 KB small-EEPROM clusters.)
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
    DumpS12H256 [FILENAME] [--flash] [--eeprom]
        FILENAME = Optional output filename
        --flash  = dump flash only
        --eeprom = dump EEPROM only
        (With neither or both flags, dumps both: flash to FILENAME and EEPROM
         to <name>_eeprom.bin. With one flag, FILENAME is that file.
         MC9S12H256 clusters: uploads a RAM copy service through KWP 0x0C
         and triggers it from the CAN0 TX interrupt. Dumps 256 KB flash and
         4 KB EEPROM, including the 0x03-protected EEPROM block.)
    DumpS12H256Eeprom [FILENAME]
        FILENAME = Optional filename
        (MC9S12H256 clusters: safe 0x03-only EEPROM read. Protected and
         register-overlaid ranges are left as 0xFF.)
    DumpS12H256Ram START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 0) or hex (e.g. 0x0)
        LENGTH = Number of bytes in decimal or hex
        FILENAME = Optional filename
        (MC9S12H256 clusters: reads $0000-$2FFF via KWP title 0x01 without
         the VDO unlock. Protected RAM ranges are left as 0xFF.)
    DumpVdoBank BANK [FILENAME] [START]
        BANK = ABR bank to dump (1-7; the V599 firmware uses 1, 2 and 3)
        FILENAME = Optional filename (default vdo_bankN.bin)
        START = Optional 0x0000-0x7FFF offset; a non-zero start resumes FILENAME at its
                current length, so an interrupted dump continues where it stopped
        (V599LLA-lineage clusters: the custom memory service is 16-bit and runs with
         ABR=1, so 0x18000 and 0x28000 both read bank 1. This command writes a RAM stub,
         hijacks the custom dispatcher's saved continuation, sets ABR, and copies one
         256-byte page of the $8000-$FFFF window per run. Bench use only; no EEPROM or
         I/O is written and a power cycle recovers a wedged cluster.)
    DumpVdoNecRom [FILENAME] (default vdo_nec_rom.bin)
        FILENAME = Optional filename
    DumpVdoNecRomHigh [FILENAME] [START]
        FILENAME = Optional filename (default vdo_nec_rom_high.bin)
        START = Optional upper-bank offset in decimal or hex (default 0); a non-zero
                start resumes FILENAME at its current length, so an interrupted dump
                continues where it stopped
        (FIS clusters with a 128 KB µPD784038: reads 0x10000-0x1FFFF through a PRAM stub
         and the display task's saved return address. Bench use only; it hijacks one
         return address per chunk, restores it when it exits, and reads every chunk twice
         so the two passes must agree.)
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