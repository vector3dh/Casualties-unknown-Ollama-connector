# Casualties: Unknown - Ollama AI Player Mod (v2.1)

A BepInEx plugin for **Casualties: Unknown** that lets an LLM (via [Ollama](https://ollama.com), local or cloud) play the game on its own. It comes with an in-game control panel, memory, multiplayer chat support, and a rate limiter so cloud credits don't vanish.

> ## ⚠️ Disclaimer
> **This mod was made by Claude (Anthropic's AI).**
>
> **This file needs to be compiled for it to work.** The previous version had a lot of issues and bugs (mainly because it was written for the wrong game version). This version fixes a lot of these bugs and adds new features, but it could still be improved.
>
> Parts of v2.1 have **not been fully tested in-game** (see [Known limitations](#known-limitations)). Expect rough edges, and please report them.

> **Recommended model: `gemma4:31b-cloud`.** It works best for the price and speed in my experience. If you have a good GPU, you can run this model locally on your PC.

---

## Requirements

| Requirement | Version |
|---|---|
| Casualties: Unknown (Demo or full) | **v7.0.1** |
| BepInEx | **5.x (x64, Mono)** |
| Ollama | Local install and/or Ollama cloud |
| Multiplayer mod (optional) | [Casualties Together](https://github.com/creaturefeaturelarry/casualties-together) 4.0.1 |
| .NET SDK (only to compile yourself) | Any version that can target `netstandard2.1` |

Full install and build steps: **[INSTALLATION INSTRUCTIONS.md](INSTALLATION%20INSTRUCTIONS.md)**

---

## Quick start

1. Install BepInEx 5 into the game folder and run the game once.
2. Drop `CasualtiesOllamaMod.dll` into `BepInEx\plugins\` (or compile it yourself).
3. Start Ollama and pull a model (e.g. `gemma4:31b-cloud`).
4. Launch the game and press **F8** to open the panel.
5. Pick your model in the **Model** tab, press **F9** to start the AI.

### Hotkeys

| Key | Action |
|---|---|
| **F8** | Open / close the control panel |
| **F9** | Start / stop the AI |
| **F10** | Emergency stop, hands control back to you immediately |
| **F7** | Pointer mode (left-click places a marker, right-click clears it) |

While the AI is playing, your own keyboard and mouse input is ignored. Press **F10** to take over.

---

## How it works

The AI runs in a loop: **observe → ask Ollama → run the actions → repeat.**

- It reads a compact text description of the game (not raw screenshots).
- It refers to targets by short IDs (`I1` items, `E2` enemies, `P1` players, `H1` hazards, `W1` worn items, `L1` limbs) instead of coordinates, which small models handle much better.
- Every action returns a real result (e.g. "barely moved, probably blocked") that goes into the next prompt, so the AI can learn what worked.
- By default the game **pauses while the model is thinking**, so slow models are still playable turn by turn.

---

## Features

### In-game control panel (F8)
A resizable window (drag the corner) with tabs for:
**Control · Chat · Mission & Prompt · Senses · Allowed Actions · Model · Memory · Multiplayer · Log**

- Give instructions, or talk to the AI directly
- Toggle each sense and each allowed action individually
- Pick the model, endpoint and optional API key (for Ollama cloud)
- Live preview of exactly what the AI sees
- Editable default prompt with all game knowledge
- Settings profiles: save, load, overwrite, delete (the API key is never stored in a profile)

### What the AI can sense
- Overall health, per-limb health, heart rate, blood pressure, internal bleeding, irregular heartbeat
- The bottom status icons (moodles) and the game's on-screen alerts, read as text
- Collision/touching-wall status, surroundings, and where it can walk or jump
- Nearby items, creatures, hazards and **traps** (with armed / already-sprung state)
- **World objects**: crates, buttons, plants, trees, with health, usability, range and line of sight
- **Players vs enemies**: other players show as `P#` with their names, creatures as `E#`
- **Line of sight** (clear / blocked by terrain) for players, enemies and objects
- **What hit it**: falls, explosions, traps and creature attacks, plus a "nearest hazard" fallback
- Worn clothing, splints and tourniquets, bag contents
- The type of liquid it's submerged in or near (groundwater, dirty water, oil, etc.)
- Climbable ropes and ladders
- Compact vision summary (8 rays, floor left/right, pits, gaps, low tunnels, reachable spots). The old ASCII map is still available in the Senses tab.

### What the AI can do

| Category | Actions |
|---|---|
| Movement | move, walk_to, follow, jump, crouch, leap (run-up gap jump), walljump, mount (get onto a ledge), climb (ropes/ladders) |
| Combat | aim, attack, throw |
| Items | grab, drop, swap, wear, remove, store, take, combine, inspect |
| Medical | apply item to limb (bandages and injections are played automatically), pull_shrapnel, defibrillators (AED and manual) |
| World | interact (crates, buttons, plants), craft |
| Social | say, remember (notes about players) |
| Carrying (multiplayer) | piggyback, carry, dismount, drop_carried |

- **Pathfinding**: `walk_to` and `follow` plan routes over ledges, gaps, drops and crawl spaces using your real jump speed and gravity.
- **Ledge guard**: stops the AI walking off big drops.
- **Auto-crouch** toggle for small tunnels (Control tab).
- Bandages are applied until used up, so no useless 1% scraps are left.

### Memory
- **Short-term**: recent turns kept verbatim; older turns are folded into a journal by the model itself.
- **Long-term**: short lessons, written by the AI or by a post-mortem when it dies, saved to disk and injected into every prompt.
- **Item notes**: inspect results are cached so items aren't re-inspected.
- **Player notes**: long-term notes about specific players, shown next to their name and editable in the Memory tab.
- Short-term and long-term memory can be toggled separately.
- **Memory profiles**: create, switch and delete, each with its own lessons, runs, journal and notes.

### Multiplayer support
Works with and without the multiplayer mod (it's read via reflection).
- Read multiplayer chat (toggle)
- Reply mode: only when addressed / to every message / never
- Configurable name and aliases the AI answers to
- Players are separated from enemies
- Carry / piggyback support

### Rate limiting (save your credits)
- Minimum seconds between calls
- Calls per minute and per hour
- Tokens per hour
- Live counters, with a choice to wait or stop when the budget runs out

For real-time play, leave the defaults. If you're on limited cloud credits, raise the delay until it suits you.

---

## Known limitations

- The sandbox this was written in had no Unity or BepInEx DLLs, so **nothing was compiled or run by Claude**. Everything was checked against the extracted game source only.
- The following are **untested** and may need fixes: `interact` on some object types, the defibrillator driver, carry/piggyback, and `mount`.
- Very tall walls can't be climbed by wall-jumping, because the game limits wall-jumps on a single wall.
- Relocating joints and pulling shrapnel are shortcuts that apply the result plus its pain cost, not the full minigame (except shrapnel, which uses the game's minigame in v2.1).
- Crafting, sleeping and trading beyond the listed actions are limited. The AI cannot restart a run after death. It stops and writes a post-mortem lesson.
- If the multiplayer mod's own patches fight with the AI's movement, please report it.

## Troubleshooting

Check `BepInEx\LogOutput.log` for these lines on startup:
- `Multiplayer mod detected (players: …, chat: …)`
- `Damage hooks installed: N`

If either looks wrong, include them in your bug report.

## Reporting bugs

Open an issue with: the game version, the multiplayer mod version, the model you used, what the AI was doing, and the relevant lines from `BepInEx\LogOutput.log`.

## Credits

- Written by **Claude (Anthropic)**, directed and tested by the project owner.
- Multiplayer compatibility targets [Casualties Together](https://github.com/creaturefeaturelarry/casualties-together).
- Built on [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony).
