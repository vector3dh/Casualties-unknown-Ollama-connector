using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasualtiesOllama
{
    /// <summary>Which "senses" the AI receives.</summary>
    public class SenseSettings
    {
        public bool Vitals = true;
        public bool Needs = true;
        public bool StatusIcons = true;   // the icons at the bottom of the screen, as readable text
        public bool Limbs = true;
        public bool Collision = true;
        public bool Map = true;
        public bool Items = true;
        public bool Objects = true;       // world objects: crates, buttons, plants, trees (with health)
        public bool Players = true;       // other human players (multiplayer)
        public bool Creatures = true;
        public bool Hazards = true;       // traps, mines, saws...
        public bool Climbables = true;    // ropes / ladders
        public bool Inventory = true;
        public bool Wearing = true;
        public bool Craftables = true;    // only recipes that can be crafted right now
        public bool Events = true;
        public bool DamageLog = true;     // "what hit you"
        public bool Chat = true;          // multiplayer chat lines
        public bool Pointer = true;       // the spot the human pointed at
        public bool Position = false;
    }

    /// <summary>What the AI may DO. Forbidden actions are rejected and the AI is told.</summary>
    public class PermissionSettings
    {
        public bool Move = true;
        public bool Jump = true;
        public bool Crouch = true;
        public bool Aim = true;
        public bool Attack = true;
        public bool UseItems = true;
        public bool ApplyToLimbs = true;
        public bool Grab = true;
        public bool DropThrow = true;
        public bool Wear = true;
        public bool Inventory = true;
        public bool Craft = true;
        public bool Inspect = true;
        public bool Speak = true;
        public bool Interact = true;       // use world objects (buttons, crates, plants)
        public bool Storage = true;        // put items into / take items out of bags, combine items, batteries
        public bool Carry = true;          // piggyback / carry players (multiplayer)
        public bool Chat = true;
    }

    public class AiSettings
    {
        // ---- Ollama ----
        public string OllamaUrl = "http://localhost:11434";
        public string ApiKey = "";             // optional, for Ollama cloud / proxies (never saved in profiles)
        public string Model = "";
        public float Temperature = 0.4f;
        public int NumCtx = 8192;
        public int NumPredict = 700;
        public string Think = "default";       // default | off | on
        public string KeepAlive = "30m";
        public int TimeoutSec = 180;

        // ---- identity ----
        public string AiName = "Bot";
        public string AiAliases = "";          // comma separated extra names it answers to

        // ---- agent behaviour ----
        public int MaxActionsPerTurn = 4;
        public float MinThinkInterval = 0.3f;
        public bool PauseWhileThinking = true;
        public bool ForceNormalSpeed = true;
        public bool BlockHumanInput = true;
        public bool AutoJumpHelper = true;
        public bool LedgeGuard = true;         // refuse to walk off big drops unless it uses 'leap'
        public bool InterruptOnChat = true;
        public bool AutoFollowPointer = true;  // follow the pointer without calling the LLM (saves credits)
        public bool LogToFile = true;
        public bool AutoCrouch = true;         // crouch automatically when a 1-block-high tunnel is ahead
        public bool AutoGrabRope = true;       // grab a rope the moment it is within reach (also while falling past it)
        public bool KeepShortMemory = true;    // short-term memory + journal are saved to disk and survive restarts / new lives
        public string MapMode = "compact";     // compact | ascii | both

        // ---- rate limits (0 = unlimited) ----
        public int MaxCallsPerMinute = 0;
        public int MaxCallsPerHour = 0;
        public int MaxTokensPerHour = 0;
        public float MinSecondsBetweenCalls = 0f;
        public bool StopWhenBudgetHit = false;
        public bool PauseGameWhileRateLimited = false;

        // ---- memory ----
        public bool ShortMemory = true;        // recent turns + journal
        public bool LongMemory = true;         // lessons / post-mortems
        public int HistoryTurns = 6;
        public int SummarizeEvery = 6;
        public int MaxLessonsInPrompt = 15;
        public string MemoryProfile = "default";
        public string ActiveProfile = "";

        // ---- senses tuning ----
        public int MapHalfWidth = 12;
        public int MapHalfHeight = 6;
        public float ItemRadius = 14f;
        public float CreatureRadius = 18f;
        public bool ShowItemFlags = false;     // false = the AI must 'inspect' items to learn what they do

        // ---- multiplayer ----
        public bool ReadChat = true;
        public string ChatMode = "mentions";   // never | mentions | all   (when to react immediately)
        public bool ReplyInChat = true;
        public int ChatLines = 8;
        public float ChatCooldown = 3f;

        // ---- text ----
        public string Mission = "Survive as long as possible. Stay alive first (stop bleeding, treat injuries, eat and drink), avoid traps and dangerous creatures, and explore carefully to find useful items.";
        public string SystemPromptOverride = "";

        // ---- hotkeys ----
        public string GuiKey = "F8";
        public string ToggleKey = "F9";
        public string StopKey = "F10";
        public string PointerKey = "F7";

        public SenseSettings Sense = new SenseSettings();
        public PermissionSettings Allow = new PermissionSettings();

        [JsonIgnore] public string Path;

        public static AiSettings Load(string dir)
        {
            string path = System.IO.Path.Combine(dir, "settings.json");
            AiSettings s = null;
            try { if (File.Exists(path)) s = JsonConvert.DeserializeObject<AiSettings>(File.ReadAllText(path)); }
            catch (Exception e) { Plugin.Log?.LogWarning("settings.json unreadable, using defaults: " + e.Message); }
            if (s == null) s = new AiSettings();
            if (s.Sense == null) s.Sense = new SenseSettings();
            if (s.Allow == null) s.Allow = new PermissionSettings();
            s.Path = path;
            return s;
        }

        public void Save()
        {
            try { File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented)); }
            catch (Exception e) { Plugin.Log?.LogWarning("Could not save settings: " + e.Message); }
        }
    }

    /// <summary>Named snapshots of all settings (model, mission, senses, permissions, prompt...). API key is never stored.</summary>
    public static class ProfileStore
    {
        static string Dir(string root) { string d = Path.Combine(root, "profiles"); Directory.CreateDirectory(d); return d; }
        public static string Safe(string n)
        {
            if (string.IsNullOrWhiteSpace(n)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Trim();
        }

        public static List<string> List(string root)
        {
            try { return Directory.GetFiles(Dir(root), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(x => x).ToList(); }
            catch { return new List<string>(); }
        }

        public static bool Save(string root, string name, AiSettings cfg)
        {
            name = Safe(name);
            if (name.Length == 0) return false;
            try
            {
                JObject jo = JObject.FromObject(cfg);
                jo.Remove("ApiKey");
                jo.Remove("ActiveProfile");
                File.WriteAllText(Path.Combine(Dir(root), name + ".json"), jo.ToString(Formatting.Indented));
                cfg.ActiveProfile = name;
                return true;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Profile save failed: " + e.Message); return false; }
        }

        public static bool Load(string root, string name, AiSettings cfg)
        {
            name = Safe(name);
            string path = Path.Combine(Dir(root), name + ".json");
            if (!File.Exists(path)) return false;
            try
            {
                JsonConvert.PopulateObject(File.ReadAllText(path), cfg);
                cfg.ActiveProfile = name;
                return true;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Profile load failed: " + e.Message); return false; }
        }

        public static void Delete(string root, string name)
        {
            try { string p = Path.Combine(Dir(root), Safe(name) + ".json"); if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
