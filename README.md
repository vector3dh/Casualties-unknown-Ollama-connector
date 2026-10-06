# Casualties: Unknown - Ollama Agent v2.1

Targets **Casualties: Unknown v7.0.1 (demo)**; works with or without the **Casualties Together** multiplayer mod (4.0.1).
Build: `dotnet build -c Release` in this folder (GamePath is set in the .csproj). Delete any older `CasualtiesOllamaMod*.dll` from `BepInEx\plugins` first.
Hotkeys: F8 panel | F9 start/stop AI | F10 emergency stop | F7 pointer tool. The panel can now be resized by dragging the `//` corner.

## v2.1 changes (from your bug report)
| # | Item | What changed |
|---|---|---|
| 1 | World objects | New `WORLD OBJECTS` section (O#): crates, buttons, plants, trees... with health, USABLE flag + prompt text, range and line of sight. New `interact` action (sends the same "OnUse" the game sends when you click). Damage/harvest = aim + attack on an O#. |
| 2 | Shrapnel by hand | `pull_shrapnel` now starts the game's own shrapnel minigame and plays it (pain/bleeding cost like bare hands). Tweezers via `apply` use the same driver. |
| 2.1 | Things on the body | `WEARING` (W#) lists clothes; `ATTACHED TO YOUR LIMBS` lists splints/tourniquets. `remove` takes a splint/tourniquet off a limb or takes clothing off (into the inventory). |
| 3 | Bags | `BAG CONTENTS` shows what is inside each bag (held, worn or on the ground). `store` (item -> bag) and `take` (content # -> inventory). |
| 4 | Liquids | The liquid you are in is named in STATUS (groundwater, lumalgae, oil...), and `VISION` lists the liquids near you. |
| 5 | Short-term memory | Journal + recent turns are saved in the memory profile (`state.json`) and survive restarts and new lives (toggle: "Save short-term memory"). The death analysis is added to the journal. |
| 6 | Bandages | Applied until the bleeding stops AND the item is used up if little is left (no 1% scraps). |
| 7 | Defibrillators | STATUS shows irregular rhythm / cardiac arrest. `apply` an AED or manual defibrillator to the chest limb (L1); both minigames are played for you (manual: sets the charge to match the fibrillation and shocks until normal, max 4). |
| 8 | Getting on top of ledges | New `mount` action: full-height jump, kick off the wall, keep pushing toward the wall so it lands ON the ledge. Measures the wall height first and refuses if it is too high. |
| 9 / 9.1 | Ropes | New `climb`: walks to the rope, jumps to grab it, climbs up/down, can climb to a target's height (player/pointer/object) and leap off toward it, or exit left/right at the top. Auto-grab: the rope is grabbed the instant it is in reach (also while falling past it). |
| 10 | Clothing | See 2.1 (`remove target W#`). |
| 11 / 11.1 | Tools & batteries | `combine slot -> slot2`: tool on a device removes the battery, battery on a device inserts it, items into bags, stackable items together (the game's own drag-and-drop rules). |
| QoL 1 | Carry / piggyback | `piggyback P#` (climb on a player), `carry P#` (put a player on your back), `dismount`, `drop_carried`; the observation says who is on whose back. Needs the multiplayer mod; the game's own rules (distance, standing, stack limit) apply and the result text says why a request is refused. |
| QoL 2 | Path finding | `walk_to` / `follow` plan routes with A* over the real terrain: walking, steps, jumps over gaps and onto ledges (arcs simulated with your jump speed and gravity), drops, and crawl spaces. Falls back to the simple walker if no route exists. |
| QoL 3 | Auto-crouch | Toggle in Control tab: crouches by itself when a 1-block-high tunnel is ahead (and the planner treats such tunnels as passable). |
| QoL 4 | Notes about players | The AI can write `note_player` + `note`; saved per memory profile and shown next to that player's name. Editable in the Memory tab. |
| QoL 5 | Resizable menu | Drag the `//` corner. The tab content scrolls inside the window. |
| QoL 6 | Better vision | Senses tab: "compact text" (default) = 8 rays + what the floor does to the left/right (walls with height, pits, gaps, low tunnels) + which spots you can reach by walking/jumping/dropping + nearby liquids. ASCII picture is still available ("ASCII" or "both"). |
| QoL 7 | Line of sight | Players and world objects show `LOS clear / blocked by terrain` (creatures already did). |

## Things to watch (I could not run the game)
- `interact` sends `OnUse` to the object like the game does; if some object type needs another message, tell me which one.
- The AED driver positions the pads and presses the shock button by writing the minigame's private fields; if the game changes them the result text will say "treatment failed".
- Path planning is bounded (about 28 blocks sideways, 16 vertically). Far targets: it walks as far as the best partial route goes, then re-plans.
- Carry/piggyback depend on the multiplayer rules (`AlwaysAllowCarry`, stack limit).
