dotnet publish kw1281test.csproj /p:PublishProfile=Win
dotnet publish kw1281test.csproj /p:PublishProfile=Mac
dotnet publish kw1281test.csproj /p:PublishProfile=Linux-Arm64
dotnet publish kw1281test.csproj /p:PublishProfile=Linux-x64

$Here = (Get-Location).Path
$PublishSourceDir = "$Here/bin/Release/net10.0/publish"
$GitHubDir = "$Here/GitHub"

Remove-Item -Path $GitHubDir/*.*

$ProjectXml = [xml](Get-Content ./kw1281test.csproj)
$Version = $ProjectXml.Project.PropertyGroup.Version

$WinExe = "$PublishSourceDir\Win\kw1281test.exe"
Compress-Archive -Force -Path $WinExe -DestinationPath "$GitHubDir/kw1281test_$($Version)_Win10.zip"

$MacZip = "kw1281test_$($Version)_macOS.zip"
Push-Location -Path "$PublishSourceDir/Mac/"
zip $MacZip kw1281test
Move-Item -Force -Path $MacZip -Destination "$GitHubDir/"
Pop-Location

$LinuxArmZip = "kw1281test_$($Version)_Linux-Arm64.zip"
Push-Location -Path "$PublishSourceDir/Linux-Arm64/"
zip $LinuxArmZip kw1281test
Move-Item -Force -Path $LinuxArmZip -Destination "$GitHubDir/"
Pop-Location

$LinuxZip = "kw1281test_$($Version)_Linux-x64.zip"
Push-Location -Path "$PublishSourceDir/Linux-x64/"
zip $LinuxZip kw1281test
Move-Item -Force -Path $LinuxZip -Destination "$GitHubDir/"
Pop-Location

Start-Process $GitHubDir
