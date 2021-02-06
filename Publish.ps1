dotnet publish kw1281test.csproj /p:PublishProfile=Win
dotnet publish kw1281test.csproj /p:PublishProfile=Mac
dotnet publish kw1281test.csproj /p:PublishProfile=LinuxArm

$PublishSourcDir = 'C:\Users\gmeno\src\kw1281test\bin\Release\net5.0\publish'
$GitHubDir = 'C:\Users\gmeno\src\kw1281test\GitHub'

Compress-Archive -Force -Path "$PublishSourcDir\Win\kw1281test.exe" -DestinationPath "$GitHubDir\kw1281test_Win10.zip"

Push-Location -Path "$PublishSourcDir\Mac\"
wsl zip kw1281test_macOS.zip kw1281test
Move-Item -Force -Path kw1281test_macOS.zip -Destination "$GitHubDir\"
Pop-Location

Push-Location -Path "$PublishSourcDir\LinuxArm\"
wsl zip kw1281test_LinuxArm.zip kw1281test
Move-Item -Force -Path kw1281test_LinuxArm.zip -Destination "$GitHubDir\"
Pop-Location

Start-Process .\GitHub
