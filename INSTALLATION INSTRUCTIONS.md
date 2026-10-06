# Installation Instructions

Two ways to install: use a **pre-built DLL** (easiest), or **compile it yourself**. Both need BepInEx and Ollama.

> **Disclaimer:** this mod was made by Claude. If you're building from source, remember the source must be compiled for the mod to work.

---

## Step 1 - Install BepInEx 5

1. Download the latest **BepInEx 5.x stable, Windows x64** build from the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases). Choose the **5.x** release, not 6.x.
2. Find your game folder, e.g.
   `D:\SteamLibrary\steamapps\common\Casualties Unknown Demo`
   (Steam: right-click the game → Manage → Browse local files)
3. Extract the contents of the zip **into the game folder**, so that `winhttp.dll` and the `BepInEx` folder sit **next to the game's .exe**.
4. **Run the game once**, then close it. This generates `BepInEx\plugins`, `BepInEx\config` and `BepInEx\LogOutput.log`.
5. Check that `BepInEx\LogOutput.log` exists. If it doesn't, BepInEx isn't loading. Make sure you extracted into the same folder as the .exe and that you used the x64 build.

---

## Step 2 - Install and set up Ollama

1. Install Ollama from [ollama.com](https://ollama.com).
2. Pull a model. **Recommended: `gemma4:31b-cloud`**
   ```
   ollama pull gemma4:31b-cloud
   ```
   - `-cloud` models run on Ollama's servers and need you to be signed in (`ollama signin`) and may use credits. This is what the rate limiter is for.
   - If you have a strong GPU, you can run the model locally instead.
3. Make sure Ollama is running before you start the game.

---

## Option A - Pre-built DLL

1. Download `CasualtiesOllamaMod.dll` from the Releases page.
2. **Delete any old `CasualtiesOllamaMod.dll`** (v1 or v2.0) from `BepInEx\plugins`. Two versions will both patch the game and conflict.
3. Copy the new DLL into:
   `<game folder>\BepInEx\plugins\`
4. Launch the game.

> The DLL must match your game version (**v7.0.1**). A DLL built against another version can fail with missing-member errors.

---

## Option B - Compile it yourself

### Requirements
- [.NET SDK](https://dotnet.microsoft.com/download) (the project targets `netstandard2.1`)
- BepInEx already installed and the game run once (Step 1), so the needed DLLs exist

### Steps
1. Open the `CasualtiesOllamaMod v2` folder.
2. Open `CasualtiesOllamaMod.csproj` in a text editor and set **`GamePath`** to your game folder, for example:
   ```xml
   <GamePath>D:\SteamLibrary\steamapps\common\Casualties Unknown Demo</GamePath>
   ```
   Don't leave stray text between the XML tags (this caused a `MSB4067` error before).
3. Open a terminal (PowerShell) in that folder and run:
   ```
   dotnet build -c Release
   ```
4. The compiled `CasualtiesOllamaMod.dll` will be in the build output folder (usually `bin\Release\netstandard2.1\`).
5. Delete any old copy from `BepInEx\plugins`, then copy the new DLL there.

### If the build fails
- **Missing member errors** (e.g. `'Body' does not contain a definition for …`): your game version differs from v7.0.1. The game renamed or removed a field. Send the error text in an issue.
- **`MSB4067`** on the .csproj: a malformed XML element. Check the lines it points to.
- **Missing references**: confirm `GamePath` is correct and the game's `*_Data\Managed` folder exists.

---

## Step 3 - First launch

1. Start Ollama, then start the game.
2. Press **F8** to open the panel.
3. In the **Model** tab, set the endpoint (default Ollama address), choose your model, and add an API key only if your cloud setup needs one.
4. Start a run, then press **F9** to start the AI. **F10** is the emergency stop.
5. Check `BepInEx\LogOutput.log` for:
   - `Multiplayer mod detected (players: …, chat: …)` (only if the multiplayer mod is installed)
   - `Damage hooks installed: N`

### Settings worth checking first
- **Rate limits** (Model tab): lower the call rate if you use a cloud service with limited credits.
- **Allowed actions / Senses**: switch off anything you don't want the AI to use or perceive.
- **Memory**: pick or create a memory profile.

---

## Optional - Multiplayer mod

To use chat reading, player detection and carrying, install [Casualties Together 4.0.1](https://github.com/creaturefeaturelarry/casualties-together) following its own instructions. The AI mod works with or without it.

In the **Multiplayer** tab you can toggle chat reading, set the reply mode (when addressed / always / never) and set the name and aliases the AI responds to.

---

## Uninstalling

Delete `CasualtiesOllamaMod.dll` from `BepInEx\plugins`. Saved profiles and memories can be deleted from the mod's config/data folder inside `BepInEx` if you want a clean slate.
