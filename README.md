# kw1281test
VW KW1281 Protocol Test Tool

This tool can send some KW1281 (and a few KW2000) commands over a dumb serial->KKL or USB->KKL cable.
If you have a legacy Ross-Tech USB cable, you can probably use that cable by
installing the Virtual COM Port drivers: https://www.ross-tech.com/vag-com/usb/virtual-com-port.php
Functionality includes reading/writing the EEPROMs of many VW MKIV instrument clusters and Comfort Control Modules.

The tool is written in .NET 5.0 and runs under Windows 10 (most serial ports) and macOS (with an FTDI serial port and D2xx drivers). It may also run under
Windows 7 and Linux but I have not tried them.

You can download a precompiled version from the Releases page: https://github.com/gmenounos/kw1281test/releases/

Otherwise, here's how to build it yourself:

##### Compiling the tool

1. You will need the .NET Core SDK,
which you can find here: https://dotnet.microsoft.com/download
(Click on the "Download .NET Core SDK" link and follow the instructions)

2. Download the source code: https://github.com/gmenounos/kw1281test/archive/master.zip
and unzip it into a folder on your computer.

3. Open up a command prompt on your computer and go into the folder where you unzipped
the source code. Type `dotnet build` to build the tool.

4. You can run the tool by typing `dotnet run`

##### Credits
Protocol Info: https://www.blafusel.de/obd/obd2_kw1281.html  
VW Radio Reverse Engineering Info: https://github.com/mnaberez/vwradio  
6502bench SourceGen: https://6502bench.com/
