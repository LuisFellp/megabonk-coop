# Third-party software in the release zips

The `-limpo` (clean install) zip bundles, unmodified, the files needed to load the plugin. They are **not** part of this repository and keep their own licenses. Source code for each is available at the links below.

| Component | Where in the zip | License | Source |
|---|---|---|---|
| BepInEx 6 (IL2CPP, bleeding edge build 788) | `files/BepInEx/core`, `files/doorstop_config.ini`, `files/changelog.txt` | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| Unity Doorstop (the `winhttp.dll` proxy loader shipped with BepInEx) | `files/winhttp.dll` | see repository | https://github.com/NeighTools/UnityDoorstop |
| HarmonyX, MonoMod, Il2CppInterop, Mono.Cecil, AsmResolver, Iced and other libraries shipped inside BepInEx `core` | `files/BepInEx/core` | their own licenses (MIT / LGPL, see each project) | linked from the BepInEx repository |
| .NET runtime used by BepInEx IL2CPP | `files/dotnet` | MIT | https://github.com/dotnet/runtime |

Nothing from the game (Megabonk) is distributed: the interop assemblies BepInEx needs are generated on the player's own PC the first time the game starts with BepInEx.
