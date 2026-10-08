# Megabonk Coop

Online co-op for **2 players** in [Megabonk](https://store.steampowered.com/app/3405340/Megabonk/) (host + 1 friend) over a direct IP (Radmin VPN, Hamachi, ZeroTier, LAN or an open UDP port). A BepInEx 6 (IL2CPP) plugin for **Megabonk 1.0.69**.

> Unofficial fan mod, built with heavy help from AI (Claude Code). Nothing is sent to any server, and co-op runs are **not** uploaded to the Steam leaderboards.

**Português:** cooperativo online para 2 jogadores por IP direto. Baixe o zip em [Releases](../../releases), rode `INSTALAR.bat` nos dois PCs e siga o `LEIAME.txt`. O `CHANGELOG.md` está em português.

## What is synchronized
| | How |
|---|---|
| **Map** (terrain, chests, shrines) on all 3 maps | A shared session seed; both of the game's RNGs are seeded identically for every generation, then verified (seed + 162 terrain samples + object counts) |
| **Enemies, bosses, damage, deaths** | Host-authoritative; the friend's hits are applied on the host. Enemy HP x1.5 with 2 players (configurable) |
| **XP and gold** | Summed: whatever one player gains, the other gains in full. Spending stays individual |
| **Revive with parity** | A player who died and re-joins the run gets the pair's XP/gold at once |
| **Projectiles, model, skin, hat, HP, aura/shield** of the friend | Streamed and drawn on the friend's model |
| **Stage change (portal)** | One enters, the other follows (both directions) |
| **No pause** | The game never pauses in co-op; you are invincible (capped) while a window is open |
| **Chat (T), map markers (G), teleport to friend (H), pickup notices, damage/kill board** | Reliable messages over UDP |
| **Shrines/eggs/graves that spawn enemies** | Run on the PC of whoever activated them; the **boss** is summoned by the host and shared |

Native in-game UI (uGUI + TextMeshPro with the game's own font): join menu with a live checklist, recent IPs, auto-join, options, chat, notices.

## Install (players)
Download the **`-limpo.zip`** from [Releases](../../releases), extract, close the game, run **`INSTALAR.bat`** (clean install with a backup). Both players must run the **same mod version**. Uninstall with `DESINSTALAR.bat`. Full steps in `LEIAME.txt` inside the zip.

## Build (developers)
- .NET 6 SDK (or newer, with roll-forward).
- A Megabonk install where BepInEx 6 IL2CPP has been run once (it generates `BepInEx\interop`, which the project references).
- `dotnet build src/MegabonkCoop -c Release -p:GameDir="C:\path\to\Megabonk"` (or set `MEGABONK_DIR`). The build copies the DLL to `<GameDir>\BepInEx\plugins\MegabonkCoop`; add `-p:NoDeploy=1` to skip that.

### Test harness
`tools/ci.ps1` runs two headless, muted copies of the game (host + client) through ~25 scenarios (sync on every map, portal both ways, packet loss/latency, reconnect, death, items, weapons, interactables, long soak) and writes `tools/last-report.md`. It needs two game copies under `C:\MegabonkCoop-DevLab\lab` and `lab2` (override with `MEGABONK_DEVLAB`) and refuses to run while a normal Megabonk is open. Examples:
```
.\tools\ci.ps1                      # build + all scenarios
.\tools\ci.ps1 -Only forest,portal  # some scenarios
.\tools\ci.ps1 -Soak -SoakMinutes 20
.\tools\ci.ps1 -Only explore -InteractName '*' -ExploreRole client   # exercise every interactable as the client
```

## Notes
- Two players only. Depends on game version 1.0.69 (patches are applied one by one, so a game update disables only the affected feature, and the menu warns about a version mismatch).
- `docs/RELATORIO-1.7.0.md` (Portuguese) documents how the game was scanned for co-op bugs and what was fixed.

## Credits and licenses
- This mod's code: MIT (see `LICENSE`).
- Bundled in the release zip (not in this repository): BepInEx 6 and its dependencies, and a .NET runtime. See `THIRD-PARTY-NOTICES.md`.
- Approach studied (no code copied) from MegabonkTogether, BonkCoop, multibonk and MegaBonk.Multiplayer.
- No game files, decompiled code or extracted assets are distributed.
