dotnet publish kw1281test.csproj /p:PublishProfile=Win
dotnet publish kw1281test.csproj /p:PublishProfile=Mac
dotnet publish kw1281test.csproj /p:PublishProfile=LinuxArm

$PublishSourcDir = 'C:\Users\gmeno\src\kw1281test\bin\Release\net5.0\publish'
$GitHubDir = 'C:\Users\gmeno\src\kw1281test\GitHub'

Compress-Archive -Force -Path "$PublishSourcDir\Win\kw1281test.exe" -DestinationPath "$GitHubDir\kw1281test_Win10.zip"
Compress-Archive -Force -Path "$PublishSourcDir\Mac\kw1281test" -DestinationPath "$GitHubDir\kw1281test_macOS.zip"
Compress-Archive -Force -Path "$PublishSourcDir\LinuxArm\kw1281test" -DestinationPath "$GitHubDir\kw1281test_LinuxArm.zip"
Start-Process .\GitHub
