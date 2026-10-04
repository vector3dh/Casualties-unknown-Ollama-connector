# Casualties: Unknown - Ollama Agent v2

Targets **Casualties: Unknown v7.0.1 (demo)** and works with or without the **Casualties Together** multiplayer mod (4.0.1).
Not compiled by me - the sandbox has no Unity/BepInEx DLLs - so expect to fix a few compile errors on the first build. Paste them to me.

## Install
1. BepInEx 5 is already in `D:\SteamLibrary\steamapps\common\Casualties Unknown Demo`.
2. **Delete the old `CasualtiesOllamaMod.dll` from `BepInEx\plugins`** (v1 and v2 would both patch the game).
3. In this folder: `dotnet build -c Release` (GamePath is already set in the .csproj). The DLL is copied to `BepInEx\plugins` automatically.
4. Settings, lessons and logs stay in `BepInEx\config\CasualtiesOllama\` (v1 data is picked up; memory is now under `memory\default\`).

## Hotkeys
F8 panel | F9 start/stop AI | F10 emergency stop | F7 pointer tool

## What's new (your list)
| # | Feature | How |
|---|---|---|
| 1 | Sees traps | Finds hazards by component type on the whole scene (the v1 scan skipped the Ground layer where traps live). Shows trap state (armed / sprung / exploded) and what each does. Harmless "backgroundified" traps are ignored. |
| 2 | Players vs enemies | `OTHER PLAYERS` (P#, real humans, with names) are separate from `CREATURES` (E#). Uses the multiplayer mod's player dictionary via reflection. |
| 3 | What hit me | Hooks on falls/impacts, explosions, bear trap, fence, cactus, coil, mine, spike trap, crate, spider attacks. Falls back to "nearest hazard/creature within 4 blocks". Shown as `WHAT HIT YOU`. |
| 4 | Self-bandaging / limb items | Calls the game's own `ApplyWoundItem` (multiplayer-synced) and then plays the bandage / syringe minigame automatically. Dislocation = `relocate`, shrapnel = `pull_shrapnel` (direct shortcuts with their pain cost, not the minigames). |
| 5 | Reads MP chat | Toggle in Multiplayer tab. Echoes of its own messages are ignored. |
| 6 | Pointer | F7 or Control tab: left-click places a marker, right-click clears. Mode "look here" or "follow" (tracks your mouse; walking costs no LLM calls). Target id `PTR`. |
| 7 | Crafting | Only recipes craftable right now are listed (R#); crafted through the game's own `TryCraft`. |
| 8 | Inspect | Items show only a name; `inspect` returns the game's tooltip. Result is saved as a short note per item id. Toggle "always show item flags" if you prefer. |
| 9 | Memory toggles | Short-term (turns + journal) and long-term (lessons, post-mortems, runs) can be switched off separately. The prompt tells the AI which it has. |
| 10 | Bottom status bar | The status icons (moodles) are read as text, plus heart rate, blood pressure, internal bleeding, septic shock, and the game's on-screen alerts. |
| 11 | Answers players | Reacts immediately when its name (or an alias) is said, or to every message, or never. Replies through the `chat` field (150 chars, cooldown). |
| 12 | Settings profiles | Profiles tab: save / load / overwrite / delete. API key is never stored in a profile. |
| 13 | Memory profiles | Memory tab: create / switch / delete (separate lessons, runs, item notes). |
| 14 | New default prompt | Covers all of the above; editable in Mission tab. |
| 15 | Rate limits | Limits tab: min seconds between calls, calls/minute, calls/hour, tokens/hour, stop-or-wait, live usage counters, optional pause while waiting. Optional API key for Ollama cloud. |
| - | Movement | New actions: `walljump`, `climb` (ropes/ladders, listed as C#), `leap` (run-up jump), `follow`. Ledge guard stops walks off big drops. Sees wall contact. |

## Things I could not verify (tell me what the log says)
- Multiplayer chat polling uses the mod's private `CHAT_LOG`; the log line `Multiplayer mod detected (players: .., chat: ..)` tells whether it was found.
- Harmony hooks print `Damage hooks installed: N`. A hook that does not exist in your version is skipped silently.
- Wall-jumping depends on the game's alternate-wall rule; the result text says why it stopped.
- If the multiplayer mod's own patches change `PlayerCamera.Update`, AI movement may fight with them: report it.
