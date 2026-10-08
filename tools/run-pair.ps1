param(
  [int]$HostMap = 1, [int]$HostTier = 0, [int]$ClientMap = 2,
  [int]$Seconds = 80, [bool]$Portal = $false, [int]$ForceHat = 0, [double]$SpawnOffset = 2, [bool]$Split = $true, [int]$Loss = 0,
  [string]$Pattern = "\[run\] (host|client) gen|\[sync\] VERDICT|\[terraincheck\] \d+ points|\[objcheck\] total|\[seedcheck\].*MISMATCH|\[hat\]|\[spawn\]|\[dev\] (portal|Interact|entering|no usable|picked)|\[portal\]|\[Error|xception|\[enemy\] (sweep|client: STAGE)|\[seed\] (shared|crypt)"
)
# Runs one host (lab) + one client (lab2) headless on this PC and prints the interesting log lines of both.
# NEVER runs while a Megabonk process of the user is open.
if (Get-Process Megabonk -ErrorAction SilentlyContinue) { "JOGO ABERTO - abortei"; return }
$proj = Split-Path -Parent $PSScriptRoot
$root = if ($env:MEGABONK_DEVLAB) { $env:MEGABONK_DEVLAB } else { "C:\MegabonkCoop-DevLab" }
$dll = "$proj\src\MegabonkCoop\bin\Release\net6.0\MegabonkCoop.dll"
foreach ($d in 'lab','lab2') { Copy-Item $dll "$root\$d\Megabonk\BepInEx\plugins\MegabonkCoop\MegabonkCoop.dll" -Force }
function Cfg($path, $autostart, $autorun, $clientmap, $hostmap, $hosttier, $portal, $hat, $offset, $split, $loss) {
@"
[Logging]
Verbose = true

[Network]
AutoStart = $autostart
JoinIP = 127.0.0.1
Port = 27999

[Sync]
Enemies = true
PortalFollow = true
Projectiles = true
GateClientStart = true
ShareSeed = true
GateHostStart = true
NoPause = true
SweepNativeEnemies = true
SpawnTogether = true
BlockLeaderboardUpload = true
SplitSpawns = $($split.ToString().ToLower())

[Dev]
AutoRun = $autorun
ForceSeed = 0
ClientMap = $clientmap
SeedMode = 0
HostMap = $hostmap
HostTier = $hosttier
ForceHat = $hat
PortalTest = $($portal.ToString().ToLower())
SpawnOffset = $offset
PacketLoss = $loss
"@ | Set-Content $path -Encoding UTF8
}
Cfg "$root\lab\Megabonk\BepInEx\config\luis.megabonkcoop.cfg" host host 0 $HostMap $HostTier $Portal $ForceHat 2 $Split $Loss
Cfg "$root\lab2\Megabonk\BepInEx\config\luis.megabonkcoop.cfg" join client $ClientMap 0 0 $false 0 $SpawnOffset $Split $Loss
$h = Start-Process "$root\lab\Megabonk\Megabonk.exe" -ArgumentList "-batchmode","-nographics" -WorkingDirectory "$root\lab\Megabonk" -WindowStyle Hidden -PassThru
Start-Sleep 2
$c = Start-Process "$root\lab2\Megabonk\Megabonk.exe" -ArgumentList "-batchmode","-nographics" -WorkingDirectory "$root\lab2\Megabonk" -WindowStyle Hidden -PassThru
Start-Sleep $Seconds
foreach ($d in @('lab','lab2')) {
  "=================== $(if($d -eq 'lab'){'HOST'}else{'CLIENT'})  (map $HostMap tier $HostTier)"
  $log = "$root\$d\Megabonk\BepInEx\LogOutput.log"
  Select-String -Path $log -Pattern $Pattern | ForEach-Object { ($_.Line -replace '^\[Info   :Megabonk Coop\] ','') } | ForEach-Object { $_.Substring(0,[Math]::Min(230,$_.Length)) } | Select-Object -First 22
  "errors: " + (Select-String -Path $log -Pattern "\[Error").Count
}
foreach ($pp in @($h,$c)) { $a = Get-Process -Id $pp.Id -ErrorAction SilentlyContinue; if ($a -and $a.Path -like '*megabonkcoop*') { Stop-Process -Id $pp.Id -Force } }
Start-Sleep 3
"(fechei as duas instancias minhas)"
