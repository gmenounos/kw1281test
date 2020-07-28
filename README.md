# kw1281test
VW KW1281 Protocol Test Tool

This tool can send a few KW1281 commands over a dumb serial->KKL or USB->KKL cable.
If you have a legacy Ross-Tech USB cable, you can probably use that cable by
installing the Virtual COM Port drivers: https://www.ross-tech.com/vag-com/usb/virtual-com-port.php

The tool is written in .NET Core 3.1 and runs under Windows 10. It may also run under
Windows 7, macOS and Linux but I have not tried them.

##### Compiling the tool
For now you will have to compile the tool yourself.

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
