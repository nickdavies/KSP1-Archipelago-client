# Installation
## Automatic (Recommended)
Install [CKAN](https://github.com/KSP-CKAN/CKAN/releases) and use the mod manager to install the latest release into your KSP installation.
## Manual
Extract the .zip file from the [releases page](https://github.com/nickdavies/KSP1-Archipelago-client/releases) into the GameData folder in the KSP installation directory.
Once done, your GameData folder should look something like this:
```
Kerbal Space Program
 |
 -- GameData
     |
     -- Squad
     -- KSPArchipelago 
```
Note: In future and experimental releases, there may be dependencies to mods such as kerbal-konstructs. 
It is recommended to install these via CKAN to ensure the correct version is used.
## Additional Resources
- [KSP Archipelago Setup Guide]( https://github.com/nickdavies/Archipelago/blob/ksp1/worlds/ksp1/docs/setup_en.md)
- [KSP APWorld Info and FAQ](https://github.com/nickdavies/Archipelago/blob/ksp1/worlds/ksp1/docs/en_Kerbal%20Space%20Program%201.md)
- [A detailed guide to manual installation](https://forum.kerbalspaceprogram.com/topic/182950-tutorial-how-to-install-mods-manually-ckan/) (including caveats for Mac users).

# Additional Requirements
The [Making History DLC](https://store.steampowered.com/app/283740/Kerbal_Space_Program_Making_History_Expansion/) is **enabled by default** and recommended — the generated seed will use its parts. If you do **not** own the DLC you must **opt out**: remove `MakingHistory` from the **Enabled Part Packs** option in your player YAML, and the run will use only stock parts.

Playing without the DLC is tested best-effort and may have bugs that the DLC enabled runs don't.

# Credits
The in-game flags shipped with this mod are based on the wonderful work of [1Kerbonaut](https://github.com/1Kerbonaut) from the [KSP-Style-Flags](https://github.com/1Kerbonaut/KSP-Style-Flags/tree/v1.0) project (v1.0). Many thanks for making them available!
