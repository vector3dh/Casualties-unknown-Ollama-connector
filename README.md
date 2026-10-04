# Casualties: Unknown - Ollama Agent v2

# MOD NOT MADE BY ME
it was vibe coded by claude, so there is no copyright
> but i do like a little attention

Targets **Casualties: Unknown v7.0.1 (demo)** and works with or without the **Casualties Together** multiplayer mod (4.0.1).

# Setup & Build Guide
> assuming you have BepInEx installed
### Prerequisites
* **.NET SDK**: Make sure you have the [.NET SDK installed](https://dotnet.microsoft.com/download). 
  * To check if it’s already installed, open your terminal/command prompt and run:
    ```bash
    dotnet --version
    ```

---

### Step 1: Download and Extract
1. Go to the **Releases** page of the GitHub repository.
2. Download the release `.zip` file (or Source code `.zip`).
3. Extract the downloaded `.zip` file to a folder on your computer.

---

### Step 2: Set Your Game Path
1. Open the extracted folder.
2. Open the project configuration file (usually a `.csproj` file) with any text editor (Notepad, VS Code, etc.).
3. Look for the following line:
   ```xml
   <GamePath>D:\SteamLibrary\steamapps\common\Casualties Unknown Demo</GamePath>
   ```
4. Change the path inside the tags to the actual directory where your game is installed. 
   * *Example:*
     ```xml
     <GamePath>C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown Demo</GamePath>
     ```
5. **Save** and close the file.

---

### Step 3: Build the Project
1. Open your terminal (Command Prompt or PowerShell) inside the extracted folder.
   * *Tip:* In Windows File Explorer, click the address bar, type `cmd`, and press **Enter**.
2. Run the following command:
   ```bash
   dotnet build -c Release
   ```
3. Once the build finishes successfully, your compiled files will be ready (typically inside the `bin/Release` folder).

## Hotkeys
F8 panel | F9 start/stop AI | F10 emergency stop | F7 pointer tool

## What's new
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

