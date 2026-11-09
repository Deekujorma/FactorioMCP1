$ErrorActionPreference = 'Stop'
$root = 'C:\FactorioMCP1'
$pidFile = Join-Path $root '.local\factorio\factorio.pid'
if (-not (Test-Path $pidFile)) { Write-Output '{"stopped":false,"reason":"no owned PID file"}'; exit 0 }
$ownedPid = [int](Get-Content -Raw $pidFile)
$p = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
if ($null -ne $p -and $p.ProcessName -ne 'factorio') { throw "Refusing to stop PID $ownedPid because it is $($p.ProcessName), not factorio." }
if ($null -ne $p) { Stop-Process -Id $ownedPid -Force }
Remove-Item $pidFile -Force
Write-Output (ConvertTo-Json @{ stopped = $true; pid = $ownedPid })
