param([switch]$TestOnly, [switch]$Cli)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/BeepDump.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'BeepDump tests failed' }
    if (-not $TestOnly) {
        New-Item -ItemType Directory -Force app | Out-Null
        dotnet publish src/BeepDump -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o app
        if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
        # Windows file names ignore case, so beepdump.exe cannot share a folder with BeepDump.exe. Keep this file ASCII: PowerShell 5.1 misreads UTF-8 without a BOM.
        dotnet publish src/BeepDump.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o app/cli/win-x64
        if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
        if ($Cli) {
            foreach ($rid in 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64') {
                dotnet publish src/BeepDump.Cli -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "app/cli/$rid"
                if ($LASTEXITCODE -ne 0) { throw "CLI publish failed: $rid" }
            }
        }
    }
} finally { Pop-Location }
