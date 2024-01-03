dotnet publish kw1281test.csproj /p:PublishProfile=Win
dotnet publish kw1281test.csproj /p:PublishProfile=Mac
# dotnet publish kw1281test.csproj /p:PublishProfile=LinuxArm
dotnet publish kw1281test.csproj /p:PublishProfile=Linux-x64

$PublishSourceDir = 'C:\Users\gmeno\src\kw1281test\bin\Release\net8.0\publish'
$GitHubDir = 'C:\Users\gmeno\src\kw1281test\GitHub'

Remove-Item -Path $GitHubDir\*.*

$WinExe = "$PublishSourceDir\Win\kw1281test.exe"
$Version = (Get-Item $WinExe).VersionInfo.ProductVersion

Compress-Archive -Force -Path $WinExe -DestinationPath "$GitHubDir\kw1281test_$($Version)_Win10.zip"

$MacZip = "kw1281test_$($Version)_macOS.zip"
Push-Location -Path "$PublishSourceDir\Mac\"
wsl zip $MacZip kw1281test
Move-Item -Force -Path $MacZip -Destination "$GitHubDir\"
Pop-Location

# $LinuxArmZip = "kw1281test_$($Version)_LinuxArm.zip"
# Push-Location -Path "$PublishSourceDir\LinuxArm\"
# wsl zip $LinuxArmZip kw1281test
# Move-Item -Force -Path $LinuxArmZip -Destination "$GitHubDir\"
# Pop-Location

$LinuxZip = "kw1281test_$($Version)_Linux-x64.zip"
Push-Location -Path "$PublishSourceDir\Linux-x64\"
wsl zip $LinuxZip kw1281test
Move-Item -Force -Path $LinuxZip -Destination "$GitHubDir\"
Pop-Location

Start-Process .\GitHub
