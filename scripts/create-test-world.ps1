$ErrorActionPreference = 'Stop'
$root = 'C:\FactorioMCP1'
$factorio = 'D:\Steam\steamapps\common\Factorio\bin\x64\factorio.exe'
$local = Join-Path $root '.local\factorio'
$config = Join-Path $local 'config\config.ini'
$save = Join-Path $local 'saves\factorio-mcp-test.zip'
$gen = Join-Path $local 'map-gen-settings.json'
$settings = Join-Path $local 'map-settings.json'
if (Test-Path $save) { Write-Output (ConvertTo-Json @{ reused = $true; save = $save }); exit 0 }
& $factorio --config $config --create $save --map-gen-settings $gen --map-settings $settings
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $save)) { throw "Factorio did not create $save" }
Write-Output (ConvertTo-Json @{ created = $true; save = $save; seed = 424242; enemies = 'disabled' })
