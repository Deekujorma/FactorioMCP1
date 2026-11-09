$ErrorActionPreference = 'Stop'
$root = 'C:\FactorioMCP1'
$factorio = 'D:\Steam\steamapps\common\Factorio\bin\x64\factorio.exe'
$local = Join-Path $root '.local\factorio'
$config = Join-Path $local 'config\config.ini'
$save = Join-Path $local 'saves\factorio-mcp-test.zip'
$log = Join-Path $root '.local\logs\factorio-graphical.log'
$errorLog = Join-Path $root '.local\logs\factorio-graphical.stderr.log'
$pidFile = Join-Path $local 'factorio.pid'
if (Test-Path $pidFile) { throw "Refusing to start: owned PID file exists. Run scripts/stop-test-world.ps1 after verifying it." }
if (-not (Test-Path $save)) { throw "Missing disposable save $save. Run scripts/create-test-world.ps1 first." }
$env:SteamAppId = '427520'
$p = Start-Process -FilePath $factorio -WorkingDirectory (Split-Path $save) -ArgumentList @('--config', $config, '--host', $save, '--window-size', '1280x720', '--disable-audio') -RedirectStandardOutput $log -RedirectStandardError $errorLog -PassThru
Set-Content -NoNewline -Path $pidFile -Value $p.Id
Write-Output (ConvertTo-Json @{ pid = $p.Id; save = $save; rconPort = 27015; log = $log; errorLog = $errorLog })
