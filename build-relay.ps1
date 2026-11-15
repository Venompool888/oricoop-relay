# Windows build + tests, no .NET SDK needed (uses the csc.exe that ships with .NET Framework 4.x).
# Output: server\RelayServer.exe and tests\RelayTest.exe, then runs the tests (exit code 0 = all passed).
# Linux / Docker builds use server\RelayServer.csproj (net8.0) instead; see AGENTS.md.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4.x) not found' }
$serverSource = Join-Path $here 'server\RelayServer.cs'
$playerLogSource = Join-Path $here 'server\RelayPlayerLog.cs'
$testSource = Join-Path $here 'tests\RelayTest.cs'
$serverExe = Join-Path $here 'server\RelayServer.exe'
$testExe = Join-Path $here 'tests\RelayTest.exe'
# The mod's own relay client code, used by the tests to talk to the server like a real game would.
$clientSources = @(Get-ChildItem (Join-Path $here 'tests\client') -Filter *.cs | ForEach-Object { $_.FullName })

'Compiling RelayServer.exe...'
& $csc /nologo /target:exe /optimize+ /langversion:5 /out:$serverExe $serverSource $playerLogSource
if ($LASTEXITCODE -ne 0) { throw 'RelayServer compilation failed' }

'Compiling RelayTest.exe...'
& $csc /nologo /target:exe /optimize+ /langversion:5 /main:OriCoop.Relay.RelayTest /out:$testExe $testSource $serverSource $playerLogSource @clientSources
if ($LASTEXITCODE -ne 0) { throw 'RelayTest compilation failed' }

'Running RelayTest.exe...'
& $testExe
exit $LASTEXITCODE
