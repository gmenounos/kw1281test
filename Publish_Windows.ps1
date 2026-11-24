$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $exitCode."
    }
}

function Compress-UnixPublishOutput {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory,

        [Parameter(Mandatory)]
        [string]$ArchiveName,

        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    Push-Location -LiteralPath $SourceDirectory
    try {
        Invoke-NativeCommand -Command 'wsl' -Arguments @('zip', $ArchiveName, 'kw1281test')
        $destinationPath = Join-Path $DestinationDirectory $ArchiveName
        Move-Item -LiteralPath $ArchiveName -Destination $destinationPath -Force
    }
    finally {
        Pop-Location
    }
}

function Publish-Application {
    param(
        [Parameter(Mandatory)]
        [string]$Framework,

        [Parameter(Mandatory)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )

    Invoke-NativeCommand -Command 'dotnet' -Arguments @(
        'publish', $ProjectPath,
        '--configuration', 'Release',
        '--framework', $Framework,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $OutputDirectory,
        '/p:Platform=AnyCPU',
        '/p:PublishProtocol=FileSystem'
    )
}

$ProjectPath = Join-Path $PSScriptRoot 'kw1281test.csproj'
$PublishRoot = Join-Path $PSScriptRoot 'bin\Release'
$PublishSourceDir = Join-Path $PublishRoot 'net11.0\publish'
$GitHubDir = Join-Path $PSScriptRoot 'GitHub'

Publish-Application -Framework 'net11.0' -RuntimeIdentifier 'win-x86' -OutputDirectory (Join-Path $PublishSourceDir 'Windows-x86')
Publish-Application -Framework 'net11.0' -RuntimeIdentifier 'osx-x64' -OutputDirectory (Join-Path $PublishSourceDir 'macOS-x64')
Publish-Application -Framework 'net11.0' -RuntimeIdentifier 'linux-arm64' -OutputDirectory (Join-Path $PublishSourceDir 'Linux-Arm64')
Publish-Application -Framework 'net11.0' -RuntimeIdentifier 'linux-x64' -OutputDirectory (Join-Path $PublishSourceDir 'Linux-x64')

New-Item -ItemType Directory -Force -Path $GitHubDir | Out-Null
Get-ChildItem -LiteralPath $GitHubDir -File -Force | Remove-Item -Force

$WinExe = Join-Path $PublishSourceDir 'Windows-x86\kw1281test.exe'
$Version = (Get-Item -LiteralPath $WinExe).VersionInfo.ProductVersion
$WinArchive = Join-Path $GitHubDir "kw1281test_$($Version)_Windows.zip"
Compress-Archive -Path $WinExe -DestinationPath $WinArchive -Force

$MacZip = "kw1281test_$($Version)_macOS.zip"
Compress-UnixPublishOutput -SourceDirectory (Join-Path $PublishSourceDir 'macOS-x64') -ArchiveName $MacZip -DestinationDirectory $GitHubDir

$LinuxArmZip = "kw1281test_$($Version)_Linux-Arm64.zip"
Compress-UnixPublishOutput -SourceDirectory (Join-Path $PublishSourceDir 'Linux-Arm64') -ArchiveName $LinuxArmZip -DestinationDirectory $GitHubDir

$LinuxZip = "kw1281test_$($Version)_Linux-x64.zip"
Compress-UnixPublishOutput -SourceDirectory (Join-Path $PublishSourceDir 'Linux-x64') -ArchiveName $LinuxZip -DestinationDirectory $GitHubDir

### Begin .NET 10
$PublishSourceDirDotNet10 = Join-Path $PublishRoot 'net10.0\publish'

Publish-Application -Framework 'net10.0' -RuntimeIdentifier 'win-x86' -OutputDirectory (Join-Path $PublishSourceDirDotNet10 'Windows-x86')
Publish-Application -Framework 'net10.0' -RuntimeIdentifier 'osx-x64' -OutputDirectory (Join-Path $PublishSourceDirDotNet10 'macOS-x64')

$WinExeDotNet10 = Join-Path $PublishSourceDirDotNet10 'Windows-x86\kw1281test.exe'
$WinDotNet10Zip = Join-Path $GitHubDir "kw1281test_$($Version)_WindowsDotNet10.zip"
Compress-Archive -Path $WinExeDotNet10 -DestinationPath $WinDotNet10Zip -Force

$MacDotNet10Zip = "kw1281test_$($Version)_macOSDotNet10.zip"
Compress-UnixPublishOutput -SourceDirectory (Join-Path $PublishSourceDirDotNet10 'macOS-x64') -ArchiveName $MacDotNet10Zip -DestinationDirectory $GitHubDir

### End .NET 10

Start-Process -FilePath $GitHubDir
