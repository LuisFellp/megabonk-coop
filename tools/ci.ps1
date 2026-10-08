<#
  Megabonk Coop - single entry point: build -> test scenarios -> report -> (optional) package.

    .\tools\ci.ps1                      build + all scenarios
    .\tools\ci.ps1 -Only forest,portal  only these scenarios
    .\tools\ci.ps1 -Soak                also a 10 min soak test (memory + stability)
    .\tools\ci.ps1 -Package             after everything PASSED: refresh dist/ and the zips, copy to $env:MEGABONK_OUT (default dist\out)

  Scenarios run a host (lab) and a client (lab2) HEADLESS, MUTED and DEAF to the keyboard in C:\MegabonkCoop-DevLab.
  It REFUSES to run while a Megabonk process is open (the user's game).
  Report: tools\last-report.md
#>
param([string[]]$Only = @(), [switch]$Soak, [switch]$Package, [switch]$SkipBuild, [switch]$PackageOnly, [int]$SoakMinutes = 10, [string]$InteractName = '', [string]$ExploreRole = 'client', [int]$ExploreMap = 1, [int]$ExploreSeconds = 135)
if ($PackageOnly) { $Package = $true; $SkipBuild = $true }
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$proj = Split-Path -Parent $PSScriptRoot
$root = if ($env:MEGABONK_DEVLAB) { $env:MEGABONK_DEVLAB } else { "C:\MegabonkCoop-DevLab" }
$dll  = "$proj\src\MegabonkCoop\bin\Release\net6.0\MegabonkCoop.dll"
$out  = if ($env:MEGABONK_OUT) { $env:MEGABONK_OUT } else { "$proj\dist\out" }; New-Item -ItemType Directory -Force $out | Out-Null
$report = New-Object System.Collections.Generic.List[string]
function Log($m) { Write-Host $m; $report.Add($m) }

if (Get-Process Megabonk -ErrorAction SilentlyContinue) { Write-Host "JOGO ABERTO - abortei (nao mexo no jogo do usuario)"; return }

# ------------------------------------------------------------------ 1) build
$version = (Select-String -Path "$proj\src\MegabonkCoop\Plugin.cs" -Pattern 'public const string Version = "([^"]+)"').Matches[0].Groups[1].Value
Log "# Megabonk Coop CI  -  v$version  -  $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
if (-not $SkipBuild) {
  $dn = "$proj\tools\dotnet\dotnet.exe"; if (Test-Path $dn) { $env:DOTNET_ROOT = "$proj\tools\dotnet" } else { $dn = "dotnet" }
  $out = & $dn build "$proj\src\MegabonkCoop" -c Release 2>&1 | Out-String
  if ($out -notmatch '0 Erro\(s\)|0 Error\(s\)') { Log "BUILD: FAIL"; $out -split "`n" | Where-Object { $_ -match 'error' } | Select-Object -Unique -First 10 | ForEach-Object { Log "  $_" }; $report | Set-Content "$proj\tools\last-report.md" -Encoding UTF8; return }
  Log "BUILD: ok"
}
foreach ($d in 'lab','lab2') { Copy-Item $dll "$root\$d\Megabonk\BepInEx\plugins\MegabonkCoop\MegabonkCoop.dll" -Force }

# ------------------------------------------------------------------ 2) scenario runner
function Write-Cfg($path, $o) {
@"
[Logging]
Verbose = true

[Network]
AutoStart = $($o.autostart)
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
SplitSpawns = $($o.split.ToString().ToLower())

[Dev]
AutoRun = $($o.autorun)
ForceSeed = 0
ClientMap = $($o.clientmap)
SeedMode = 0
HostMap = $($o.hostmap)
HostTier = 0
ForceHat = $($o.hat)
PortalTest = $($o.portal.ToString().ToLower())
SpawnOffset = $($o.offset)
PacketLoss = $($o.loss)
Latency = $($o.latency)
Interact = $($o.interact)
LogDir = C:\MegabonkCoop-DevLab\shots\logs
Jitter = $($o.jitter)
FakeVersion = $($o.fakever)
ShareTest = $($o.sharetest.ToString().ToLower())
PingTest = $($o.pingtest.ToString().ToLower())
FinalTest = $($o.finaltest.ToString().ToLower())
TeleportTest = $($o.tptest.ToString().ToLower())
"@ | Set-Content $path -Encoding UTF8
}

function Run-Scenario($s) {
  $o = @{ hostmap = 1; clientmap = 2; seconds = 60; portal = $false; hat = 0; offset = 2; split = $true; loss = 0; fakever = ''; soak = $false; kill = ''; killat = 45; relaunch = $false; sharetest = $false; clientrun = 'client'; pingtest = $false; finaltest = $false; tptest = $false; latency = 0; jitter = 0; interact = '' }
  foreach ($k in $s.Keys) { $o[$k] = $s[$k] }          # scenario values override the defaults
  Write-Cfg "$root\lab\Megabonk\BepInEx\config\luis.megabonkcoop.cfg"  (@{ autostart = 'host'; autorun = 'host'; clientmap = 0; hostmap = $o.hostmap; hat = $o.hat; portal = $o.portal; offset = 2; split = $o.split; loss = $o.loss; latency = $o.latency; jitter = $o.jitter; fakever = ''; sharetest = $o.sharetest; pingtest = $o.pingtest; finaltest = $o.finaltest; tptest = $false; interact = $(if ($ExploreRole -eq 'host') { $o.interact } else { '' }) })
  Write-Cfg "$root\lab2\Megabonk\BepInEx\config\luis.megabonkcoop.cfg" (@{ autostart = 'join'; autorun = $o.clientrun; clientmap = $o.clientmap; hostmap = 0; hat = 0; portal = $false; offset = $o.offset; split = $o.split; loss = $o.loss; latency = $o.latency; jitter = $o.jitter; fakever = $o.fakever; sharetest = $false; pingtest = $false; finaltest = $false; tptest = $o.tptest; interact = $(if ($ExploreRole -eq 'client') { $o.interact } else { '' }) })
  $h = Start-Process "$root\lab\Megabonk\Megabonk.exe"  -ArgumentList "-batchmode","-nographics" -WorkingDirectory "$root\lab\Megabonk"  -WindowStyle Hidden -PassThru
  Start-Sleep 2
  $c = Start-Process "$root\lab2\Megabonk\Megabonk.exe" -ArgumentList "-batchmode","-nographics" -WorkingDirectory "$root\lab2\Megabonk" -WindowStyle Hidden -PassThru
  $mem = @(); $killed = $false; $relaunched = $false
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt $o.seconds) {
    Start-Sleep 10
    if ($o.soak) { try { $mem += [math]::Round(((Get-Process -Id $h.Id).WorkingSet64 + (Get-Process -Id $c.Id).WorkingSet64) / 1MB) } catch { } }
    if ($o.kill -and -not $killed -and ((Get-Date) - $t0).TotalSeconds -ge $o.killat) { $killed = $true; $v = if ($o.kill -eq 'client') { $c } else { $h }; if ($v.Path -like '*MegabonkCoop-DevLab*') { Stop-Process -Id $v.Id -Force } }
    if ($o.relaunch -and $killed -and -not $relaunched -and ((Get-Date) - $t0).TotalSeconds -ge ($o.killat + 12)) { $relaunched = $true; Start-Sleep 1; $c = Start-Process "$root\lab2\Megabonk\Megabonk.exe" -ArgumentList "-batchmode","-nographics" -WorkingDirectory "$root\lab2\Megabonk" -WindowStyle Hidden -PassThru }
    if (-not $o.kill -and (-not (Get-Process -Id $h.Id -ErrorAction SilentlyContinue) -or -not (Get-Process -Id $c.Id -ErrorAction SilentlyContinue))) { break }   # a crash ends the scenario early   # a crash ends the scenario early
  }
  $alive = @((Get-Process -Id $h.Id -ErrorAction SilentlyContinue), (Get-Process -Id $c.Id -ErrorAction SilentlyContinue)) | Where-Object { $_ }
  $bothAlive = if ($o.kill -and -not $o.relaunch) { $alive.Count -eq 1 } else { ($alive.Count -eq 2) }
  $ht = Get-Content "$root\lab\Megabonk\BepInEx\LogOutput.log" -Raw
  $ct = Get-Content "$root\lab2\Megabonk\BepInEx\LogOutput.log" -Raw
  foreach ($pp in @($h, $c)) { $a = Get-Process -Id $pp.Id -ErrorAction SilentlyContinue; if ($a -and $a.Path -like '*MegabonkCoop-DevLab*') { Stop-Process -Id $pp.Id -Force } }
  Start-Sleep 3
  return @{ h = $ht; c = $ct; alive = $bothAlive; mem = $mem }
}

function Cnt($t, $re) { ([regex]::Matches($t, $re)).Count }
$results = New-Object System.Collections.Generic.List[object]
function Check($name, $cond, $detail) { $results.Add([pscustomobject]@{ Scenario = $script:cur; Check = $name; Pass = [bool]$cond; Detail = $detail }) }

function Common($r) {
  Check "both processes alive at the end" $r.alive ""
  Check "host: no [Error" ((Cnt $r.h '\[Error') -eq 0) "$(Cnt $r.h '\[Error') errors"
  Check "client: no [Error" ((Cnt $r.c '\[Error') -eq 0) "$(Cnt $r.c '\[Error') errors"
  Check "all Harmony patch classes applied" (($r.h -match '\[patch\] \d+ patch classes applied, 0 failed') -and ($r.c -match '\[patch\] \d+ patch classes applied, 0 failed')) ""
  Check "native UI did not fail" (((Cnt $r.h '\[nui\] menu failed') + (Cnt $r.c '\[nui\] menu failed')) -eq 0) ""
}
function Synced($r, $n) {
  Check "host SYNCED x$n" ((Cnt $r.h '\[sync\] VERDICT: seed OK, ground OK, objects OK => SYNCED') -ge $n) "$(Cnt $r.h 'VERDICT: seed OK, ground OK, objects OK => SYNCED')"
  Check "client SYNCED x$n" ((Cnt $r.c '\[sync\] VERDICT: seed OK, ground OK, objects OK => SYNCED') -ge $n) "$(Cnt $r.c 'VERDICT: seed OK, ground OK, objects OK => SYNCED')"
  Check "both learned the friend name" (($r.h -match "\[net\] friend's name:") -and ($r.c -match "\[net\] friend's name:")) ""
  $np = @([regex]::Matches($r.h, '\[terraincheck\] (\d+) points') | ForEach-Object { [int]$_.Groups[1].Value })
  Check "ground check compared >= 100 points (not an empty comparison)" ($np.Count -ge 1 -and (($np | Measure-Object -Maximum).Maximum -ge 100)) "points: $($np -join ',')"
  Check "no seed MISMATCH" (((Cnt $r.h 'MISMATCH') + (Cnt $r.c 'MISMATCH')) -eq 0) ""
}

$all = @(
  @{ name = 'forest';    p = @{ hostmap = 1; clientmap = 2; seconds = 60 };  t = { param($r) Common $r; Synced $r 1; Check "damage/kill stats flow host -> client" (($r.h -match "\[stats\] host dealt") -and ($r.c -match "\[stats\] received from host")) ""; Check "host scaled enemy HP for 2 players" ($r.h -match '\[enemy\] HP x[\d,\.]+ applied to \d+ enemies') ""; Check "client spawned next to host" ($r.c -match 'client spawn moved') "" } },
  @{ name = 'desert';    p = @{ hostmap = 2; clientmap = 1; seconds = 60 };  t = { param($r) Common $r; Synced $r 1; Check "client forced to host map" ($r.c -match 'forcing the HOST') "" } },
  @{ name = 'graveyard'; p = @{ hostmap = 8; clientmap = 1; seconds = 60 };  t = { param($r) Common $r; Synced $r 1 } },
  @{ name = 'portal';    p = @{ hostmap = 1; clientmap = 2; seconds = 95; portal = $true };
     t = { param($r) Common $r; Synced $r 2; Check "client followed the stage change" ($r.c -match 'follow: calling MapController\.LoadNextStage') ""; Check "no double generation at run start" ((Cnt $r.h '\[run\] host generating') -eq 2) "$(Cnt $r.h '\[run\] host generating') host generations" } },
  @{ name = 'final';     p = @{ hostmap = 1; clientmap = 1; seconds = 95; portal = $true; finaltest = $true };
     t = { param($r) Common $r; Synced $r 1
           Check "both ended in the FinalBossMap scene" (($r.h -match 'scene=FinalBossMap') -and ($r.c -match 'scene=FinalBossMap')) ""
           Check "host loaded the final stage" ($r.h -match 'calling MapController\.LoadFinalStage') ""
           Check "client followed to the final stage" ($r.c -match 'follow: calling MapController\.LoadFinalStage') "" } },
  @{ name = 'teleport';  p = @{ hostmap = 1; clientmap = 1; seconds = 80; offset = 150; tptest = $true };
     t = { param($r) Common $r; Synced $r 1
           Check "client teleported to the friend" ($r.c -match '\[tp\] teleported to the friend') ""
           $ds = @([regex]::Matches($r.c, 'dist=([\d,\.]+)') | ForEach-Object { [double]($_.Groups[1].Value -replace ',', '.') } | Select-Object -Last 2)
           $d = ($ds | Where-Object { $_ -ge 0 } | Measure-Object -Minimum).Minimum
           Check "afterwards the players are close (< 40 m, they were ~300 m apart)" ($null -ne $d -and $d -lt 40) "closest of the last readings = $d" } },
  @{ name = 'lag';       p = @{ hostmap = 1; clientmap = 1; seconds = 110; latency = 70; jitter = 40; loss = 5 };
     t = { param($r) Common $r; Synced $r 1
           $st = [regex]::Matches($r.h, 'rel sent=(\d+) resent=(\d+) acked=(\d+) dup=(\d+) dropped=(\d+)') | Select-Object -Last 1
           if ($st) { $sent = [int]$st.Groups[1].Value; $acked = [int]$st.Groups[3].Value; $dropped = [int]$st.Groups[5].Value; Check "reliable layer under ~140 ms RTT + jitter + 5% loss: >=95% acked, 0 dropped" (($sent -gt 0) -and (($acked / $sent) -ge 0.95) -and ($dropped -eq 0)) "sent=$sent acked=$acked dropped=$dropped" }
           $rtt = [regex]::Matches($r.c, 'rtt=(\d+)ms') | ForEach-Object { [int]$_.Groups[1].Value } | Select-Object -Last 1
           Check "latency simulation really active (rtt > 100 ms)" ($rtt -gt 100) "rtt=$rtt ms"
           Check "client saw enemy copies" ($r.c -match '\[enemy\] client: first copy spawned') "" } },
  @{ name = 'explore';   p = @{ hostmap = $ExploreMap; clientmap = $ExploreMap; seconds = $ExploreSeconds; interact = $InteractName };
     t = { param($r)
           Check "host and client alive" $r.alive ""
           foreach ($side in 'h', 'c') { $txt = $r.$side; $m = [regex]::Matches($txt, '\[interact-test\][^\r\n]*|\[scan\][^\r\n]*|\[items-test\][^\r\n]*|\[weapons-test\][^\r\n]*|\[kill-test\][^\r\n]*|\[model\][^\r\n]*|\[chat[^\r\n]*|\[portal[^\r\n]*|\[menu-test\][^\r\n]*|\[sync\] VERDICT[^\r\n]*|\[run\][^\r\n]*|\[player\][^\r\n]*|\[net\][^\r\n]*|\[toast\][^\r\n]*'); foreach ($x in $m) { Log ("    [{0}] {1}" -f $side, $x.Value) } } } },
  @{ name = 'interact'; p = @{ hostmap = 2; clientmap = 2; seconds = 115; interact = '*' };
     t = { param($r) Common $r
           $bad = [regex]::Matches($r.c, '\[scan\] \w+: [^\r\n]*exception=(?!none)[^\r\n]*')
           Check "no interactable throws on the client (shrines, eggs, graves...)" ($bad.Count -eq 0) $(if ($bad.Count) { $bad[0].Value } else { '' })
           Check "interactables were really exercised (>= 10 types)" ((Cnt $r.c '\[scan\] \w+: can=') -ge 10) "$(Cnt $r.c '\[scan\] \w+: can=') types"
           Check "interactable overrides wrapped for local spawns" ($r.c -match '\[patch\] \d+ interactable Interact\(\) overrides wrapped for local spawns \(0 failed\)') ""
           if ($r.c -match '\[scan\] BossSpawner:') { Check "boss request reached the host and was executed" (($r.c -match '\[interact\] boss spawner: asked the host') -and ($r.h -match '\[interact\] friend asked for the boss: summoned')) "" }
           $lg = Get-ChildItem "C:\MegabonkCoop-DevLab\shots\logs" -Filter 'coop-log-v*-amigo-*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) -and $_.Length -gt 2000 }
           Check "the options button saves a usable log file" (@($lg).Count -ge 1) "" } },
  @{ name = 'loss30';    p = @{ hostmap = 1; clientmap = 1; seconds = 110; loss = 30 };
     t = { param($r) Common $r; Synced $r 1
           $st = [regex]::Matches($r.h, 'rel sent=(\d+) resent=(\d+) acked=(\d+) dup=(\d+) dropped=(\d+)') | Select-Object -Last 1
           if ($st) { $sent = [int]$st.Groups[1].Value; $acked = [int]$st.Groups[3].Value; $dropped = [int]$st.Groups[5].Value; Check "reliable layer: >=95% acked, 0 dropped (30% loss)" (($sent -gt 20) -and ($acked / [math]::Max($sent,1) -ge 0.95) -and ($dropped -eq 0)) "sent=$sent acked=$acked dropped=$dropped" } else { Check "reliable stats present" $false "no status line" } } },
  @{ name = 'hat';       p = @{ hostmap = 1; clientmap = 1; seconds = 50; hat = 3 };
     t = { param($r) Common $r; Check "client attached the friend's hat (Head bone, node or model root)" ($r.c -match "\[hat\] hat \w+ attached to (bone|node|model root)") "" } },
  @{ name = 'split';     p = @{ hostmap = 1; clientmap = 1; seconds = 100; offset = 150 };
     t = { param($r) Common $r; Synced $r 1
           $near = ([regex]::Matches($r.h, '(\d+) within 60 m of the friend') | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Maximum).Maximum
           Check "enemies appear near the friend 260 m away (max >= 3)" ($near -ge 3) "max near friend = $near" } },
  @{ name = 'share';     p = @{ hostmap = 1; clientmap = 1; seconds = 75; sharetest = $true };
     t = { param($r) Common $r; Synced $r 1
           Check "host ran the share test" ($r.h -match '\[share\] DEV test') ""
           $m = [regex]::Match($r.c, 'received \+(\d+) xp, \+(\d+) gold from the friend: gold (\d+)->(\d+)', 'None')
           $big = [regex]::Matches($r.c, 'received \+(\d+) xp, \+(\d+) gold from the friend: gold (\d+)->(\d+)') | Where-Object { [int]$_.Groups[2].Value -ge 100 -and [int]$_.Groups[1].Value -ge 25 } | Select-Object -First 1
           Check "client received the +25 xp / +100 gold in full" ($null -ne $big) $(if ($big) { $big.Value } else { '' })
           Check "client gold really rose by 100" ($null -ne $big -and ([int]$big.Groups[4].Value - [int]$big.Groups[3].Value) -ge 100) "" } },
  @{ name = 'autojoin';  p = @{ hostmap = 1; clientmap = 1; seconds = 90; clientrun = 'ui' };
     t = { param($r) Common $r; Synced $r 1
           Check "client entered the run by itself (AutoJoin)" ($r.c -match '\[autojoin\] -> MapSelectionUi\.StartMap\(\)') "" } },
  @{ name = 'marker';    p = @{ hostmap = 1; clientmap = 1; seconds = 75; pingtest = $true };
     t = { param($r) Common $r; Synced $r 1
           Check "host sent a map marker" ($r.h -match '\[marker\] sent at') ""
           Check "client received the marker" ($r.c -match '\[marker\] received at') ""
           Check "native HUD built on both" (($r.h -match '\[nui\] native HUD built') -and ($r.c -match '\[nui\] native HUD built')) "" } },
  @{ name = 'portalrev'; p = @{ hostmap = 1; clientmap = 1; seconds = 100; interact = 'PORTAL' };
     t = { param($r) Common $r; Synced $r 2
           Check "client took the portal on its own" ($r.c -match '\[portal-test\] client calls LoadNextStage') ""
           Check "host followed the CLIENT into the next stage" ($r.h -match '\[portal\] follow: calling MapController\.LoadNextStage') ""
           Check "host confirmed the 2nd stage too (2 seed matches)" ((Cnt $r.h '\[seedcheck\] seed mine=\d+ friend=\d+ => MATCH') -ge 2) "$(Cnt $r.h '\[seedcheck\] seed mine=\d+ friend=\d+ => MATCH')" } },
  @{ name = 'killpeer';  p = @{ hostmap = 1; clientmap = 1; seconds = 100; kill = 'client'; killat = 45 };
     t = { param($r) Check "host survived the client dying" $r.alive "(exactly 1 process left)"; Check "host: no [Error" ((Cnt $r.h '\[Error') -eq 0) ""
           Check "host noticed the friend is GONE" ($r.h -match 'friend considered GONE') ""
           Check "host stopped managing enemies (reset)" ($r.h -match '\[enemy\] reset \(peer lost\)') "" } },
  @{ name = 'rejoin';    p = @{ hostmap = 1; clientmap = 1; seconds = 150; kill = 'client'; killat = 40; relaunch = $true };
     t = { param($r) Common $r
           Check "host noticed the friend is GONE" ($r.h -match 'friend considered GONE') ""
           Check "host saw the friend come back" ($r.h -match 'friend is back|peer address changed|Client connected') ""
           Check "host had verified the run before the friend left and kept it after he came back" (((Cnt $r.h 'VERDICT: seed OK, ground OK, objects OK => SYNCED') -ge 1) -and ($r.h -match '\[share\] friend asked for a catch-up')) "$(Cnt $r.h 'VERDICT: seed OK, ground OK, objects OK => SYNCED') verdict(s)"
           Check "returning client synced (SYNCED)" ((Cnt $r.c 'VERDICT: seed OK, ground OK, objects OK => SYNCED') -ge 1) ""
           Check "host answered the catch-up request" ($r.h -match '\[share\] friend asked for a catch-up') ""
           Check "returning client got the pair's xp/gold (catch-up)" ($r.c -match '\[share\] catch-up applied: \+\d+ xp') "" } },
  @{ name = 'killhost';  p = @{ hostmap = 1; clientmap = 1; seconds = 100; kill = 'host'; killat = 45 };
     t = { param($r) Check "client survived the host dying" $r.alive "(exactly 1 process left)"; Check "client: no [Error" ((Cnt $r.c '\[Error') -eq 0) ""
           Check "client noticed the friend is GONE" ($r.c -match 'friend considered GONE') ""
           Check "client dropped the enemy copies" ($r.c -match '\[enemy\] reset \(peer lost\)') "" } },
  @{ name = 'version';   p = @{ hostmap = 1; clientmap = 1; seconds = 35; fakever = '0.0.1' };
     t = { param($r) Check "host reports MISMATCH" ($r.h -match '\[version\] MISMATCH') ""; Check "client reports MISMATCH" ($r.c -match '\[version\] MISMATCH') ""; Check "no run was started" (-not ($r.h -match '\[mapgen\] START')) ""; Check "no [Error" (((Cnt $r.h '\[Error') + (Cnt $r.c '\[Error')) -eq 0) "" } }
)
if ($Soak) { $all += @{ name = 'soak'; p = @{ hostmap = 1; clientmap = 1; seconds = ($SoakMinutes * 60); soak = $true };
  t = { param($r) Common $r; Synced $r 1
        if ($r.mem.Count -ge 4) { $first = $r.mem[1]; $last = $r.mem[-1]; Check "memory growth < 600 MB over the soak" (($last - $first) -lt 600) "$first MB -> $last MB" } } } }

if ($PackageOnly) { $all = @() }   # re-package the DLL that is already in bin (use right after a green run)
foreach ($sc in $all) {
  if ($Only.Count -gt 0 -and ($Only -notcontains $sc.name)) { continue }
  $script:cur = $sc.name
  Log "`n## scenario: $($sc.name)"
  $r = Run-Scenario $sc.p
  & $sc.t $r
  foreach ($x in $results | Where-Object { $_.Scenario -eq $sc.name }) { Log ("  [{0}] {1}  {2}" -f ($(if ($x.Pass) { 'PASS' } else { 'FAIL' })), $x.Check, $x.Detail) }
  # keep the logs of a failing scenario for analysis
  if ($results | Where-Object { $_.Scenario -eq $sc.name -and -not $_.Pass }) {
    $keep = "$proj\tools\failed-$($sc.name)"; New-Item -ItemType Directory -Force $keep | Out-Null
    $r.h | Set-Content "$keep\host.log" -Encoding UTF8; $r.c | Set-Content "$keep\client.log" -Encoding UTF8
    Log "  (logs saved in tools\failed-$($sc.name))"
  }
}

$fail = @($results | Where-Object { -not $_.Pass }).Count
$total = $results.Count
Log "`n# RESULT: $($total - $fail)/$total checks passed  ->  $(if ($fail -eq 0) { 'ALL GREEN' } else { "$fail FAILED" })"
$report | Set-Content "$proj\tools\last-report.md" -Encoding UTF8

# ------------------------------------------------------------------ 3) package (only when everything is green)
if ($Package) {
  if ($fail -ne 0 -or $Only.Count -gt 0) { Log "PACKAGE: skipped (needs a full run with all checks green)"; return }
  # docs come from templates so the version is always right
  (Get-Content $proj\dist\LEIAME.template.txt -Raw -Encoding UTF8) -replace '@VERSION@', $version | Set-Content $proj\dist\pkg\LEIAME.txt -Encoding UTF8
  (Get-Content $proj\dist\README.template.md -Raw -Encoding UTF8) -replace '@VERSION@', $version | Set-Content $proj\dist\pkg\README.md -Encoding UTF8
  Copy-Item $proj\dist\pkg\README.md $proj\dist\README.md -Force
  (Get-Content $proj\dist\RELATORIO.template.md -Raw -Encoding UTF8) -replace '@VERSION@', $version | Set-Content $proj\dist\pkg\RELATORIO.md -Encoding UTF8
  Copy-Item $proj\dist\pkg\RELATORIO.md $out\RELATORIO-v$version.md -Force
  Copy-Item $dll "$proj\dist\pkg\files\BepInEx\plugins\MegabonkCoop\MegabonkCoop.dll" -Force
  Copy-Item $dll "$proj\dist\update\MegabonkCoop.dll" -Force
  $z = "C:\Program Files\7-Zip\7z.exe"
  Remove-Item "$proj\dist\MegabonkCoop-v$version-*.zip" -ErrorAction SilentlyContinue
  Push-Location "$proj\dist\pkg";    & $z a -tzip -mx=5 "..\MegabonkCoop-v$version-limpo.zip" . | Out-Null; Pop-Location
  Push-Location "$proj\dist\update"; & $z a -tzip -mx=9 "..\MegabonkCoop-v$version-ATUALIZAR.zip" . | Out-Null; Pop-Location
  Copy-Item "$proj\dist\MegabonkCoop-v$version-*.zip" "$out\" -Force
  Log "PACKAGE: dist + Downloads updated for v$version"
}
