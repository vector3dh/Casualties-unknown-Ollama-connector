using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CasualtiesOllama
{
    [BepInPlugin(Guid, "Casualties Ollama Agent v2", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.vector.casualties.ollama2";

        public static Plugin Instance;
        public static ManualLogSource Log;

        public AiSettings Cfg;
        public MemoryStore Mem;
        public RateLimiter Limiter;
        public Agent Agent;
        public GuiWindow Gui;
        public string DataDir;

        KeyCode guiKey = KeyCode.F8, toggleKey = KeyCode.F9, stopKey = KeyCode.F10, pointerKey = KeyCode.F7;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            DataDir = Path.Combine(Paths.ConfigPath, "CasualtiesOllama");   // same folder as v1: settings carry over
            Directory.CreateDirectory(DataDir);

            Cfg = AiSettings.Load(DataDir);
            Limiter = new RateLimiter();
            Mem = new MemoryStore(DataDir, Cfg.MemoryProfile);
            Agent = new Agent(this);
            Gui = new GuiWindow(this);
            ParseKeys();

            var harmony = new Harmony(Guid);
            Type[] patches = { typeof(Patch_Update), typeof(Patch_HandleInput), typeof(Patch_PointerOverUI), typeof(Patch_PointerOverUIList), typeof(Patch_Alert) };
            foreach (var t in patches)
            {
                try { harmony.PatchAll(t); }
                catch (Exception e) { Log.LogError("Patch failed for " + t.Name + ": " + e.Message); }
            }
            try { DamageHooks.Install(harmony); } catch (Exception e) { Log.LogWarning("Damage hooks: " + e.Message); }

            Log.LogInfo("Casualties Ollama Agent v2 loaded. Data folder: " + DataDir + ". Multiplayer mod: " + (MP.Present ? "found" : "not found (yet)"));
        }

        public void ParseKeys()
        {
            KeyCode k;
            if (Enum.TryParse(Cfg.GuiKey, true, out k)) guiKey = k;
            if (Enum.TryParse(Cfg.ToggleKey, true, out k)) toggleKey = k;
            if (Enum.TryParse(Cfg.StopKey, true, out k)) stopKey = k;
            if (Enum.TryParse(Cfg.PointerKey, true, out k)) pointerKey = k;
        }

        public void SwitchMemory(string name)
        {
            name = MemoryStore.Safe(name);
            Cfg.MemoryProfile = name;
            Mem = new MemoryStore(DataDir, name);
            Cfg.Save();
            Agent.AddLog("Memory profile: " + name + " (" + Mem.Lessons.Count + " lessons)");
        }

        public bool LoadProfile(string name)
        {
            if (!ProfileStore.Load(DataDir, name, Cfg)) return false;
            ParseKeys();
            SwitchMemory(Cfg.MemoryProfile);
            Cfg.Save();
            Agent.AddLog("Settings profile loaded: " + name);
            return true;
        }

        void Update()
        {
            if (Input.GetKeyDown(guiKey)) Gui.Toggle();
            if (!Gui.Typing)
            {
                if (Input.GetKeyDown(toggleKey)) Agent.Toggle();
                if (Input.GetKeyDown(pointerKey)) Pointer.Armed = !Pointer.Armed;
            }
            if (Input.GetKeyDown(stopKey)) { Agent.Stop("emergency stop"); Pointer.Armed = false; }

            Gui.UpdateMouseState();
            if (Pointer.Armed && !Gui.MouseOver)
            {
                if (Input.GetMouseButtonDown(0)) Pointer.SetFromMouse();
                else if (Pointer.Mode == 1 && Pointer.Set) Pointer.SetFromMouse();   // live-follow mode: pointer tracks the mouse
                if (Input.GetMouseButtonDown(1)) Pointer.Clear();
            }

            try { Agent.Tick(); }
            catch (Exception e) { Log.LogError(e); }
        }

        void OnGUI()
        {
            if (Gui != null) Gui.Draw();
            Pointer.DrawMarker();
        }

        void OnApplicationQuit()
        {
            Agent?.Stop("game closing");
            Cfg?.Save();
        }
    }

    /// <summary>The spot the human points at for the AI (world coordinates).</summary>
    public static class Pointer
    {
        public static bool Armed;      // pointer tool active (game clicks are suppressed)
        public static bool Set;
        public static int Mode;        // 0 = look here, 1 = follow it (pointer tracks the mouse)
        public static Vector2 World;
        static GameObject go;
        static GUIStyle style;

        public static Transform Tf
        {
            get
            {
                if (go == null) { go = new GameObject("AIPointer"); go.hideFlags = HideFlags.HideAndDontSave; }
                go.transform.position = new Vector3(World.x, World.y, 0f);
                return go.transform;
            }
        }

        static Camera Cam() { Camera c = Camera.main; if (c == null && Camera.allCamerasCount > 0) c = Camera.allCameras[0]; return c; }

        public static void SetFromMouse()
        {
            Camera cam = Cam();
            if (cam == null) return;
            Vector3 sp = Input.mousePosition;
            sp.z = Mathf.Abs(cam.transform.position.z);
            Vector3 w = cam.ScreenToWorldPoint(sp);
            World = new Vector2(w.x, w.y);
            Set = true;
        }

        public static void Clear() { Set = false; }

        public static void DrawMarker()
        {
            if (!Set && !Armed) return;
            if (style == null) { style = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter }; }
            Camera cam = Cam();
            if (cam == null) return;
            if (Set)
            {
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(World.x, World.y, 0f));
                if (sp.z >= 0f)
                {
                    Color old = GUI.color;
                    GUI.color = new Color(1f, 0.9f, 0.1f, 1f);
                    GUI.Label(new Rect(sp.x - 25f, Screen.height - sp.y - 20f, 50f, 40f), "X", style);
                    GUI.Label(new Rect(sp.x - 60f, Screen.height - sp.y + 10f, 120f, 22f), Mode == 1 ? "AI: follow" : "AI: look here", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12 });
                    GUI.color = old;
                }
            }
            if (Armed)
                GUI.Label(new Rect(Screen.width / 2f - 230f, 8f, 460f, 24f), "POINTER TOOL: left-click = place pointer, right-click = clear (F7 = off)", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 13, fontStyle = FontStyle.Bold });
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Harmony patches
    // ---------------------------------------------------------------------------------------------

    /// <summary>After PlayerCamera.Update (which reads the human's input): push the AI's controls into the Body.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "Update")]
    static class Patch_Update
    {
        static void Postfix(PlayerCamera __instance)
        {
            try { if (Plugin.Instance != null && Plugin.Instance.Agent != null) Plugin.Instance.Agent.ApplyControls(__instance); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    /// <summary>Skip the game's keyboard handling while typing in the mod's text boxes or while the AI is in control.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleInput")]
    static class Patch_HandleInput
    {
        static bool Prefix()
        {
            var p = Plugin.Instance;
            if (p == null) return true;
            if (p.Gui != null && p.Gui.Typing) return false;
            if (p.Agent != null && p.Agent.SkipHumanInput) return false;
            return true;
        }
    }

    /// <summary>The game thinks the mouse is over UI while it is over our window or while the pointer tool is on, so clicks don't also hit the world.</summary>
    [HarmonyPatch(typeof(UIUtil), "IsPointerOverUIElement", new Type[0])]
    static class Patch_PointerOverUI
    {
        static void Postfix(ref bool __result)
        {
            var p = Plugin.Instance;
            if (p != null && ((p.Gui != null && p.Gui.MouseOver) || Pointer.Armed)) __result = true;
        }
    }

    [HarmonyPatch(typeof(UIUtil), "IsPointerOverUIElement", new Type[] { typeof(List<RaycastResult>) })]
    static class Patch_PointerOverUIList
    {
        static void Postfix(ref bool __result)
        {
            var p = Plugin.Instance;
            if (p != null && ((p.Gui != null && p.Gui.MouseOver) || Pointer.Armed)) __result = true;
        }
    }

    /// <summary>Captures the game's on-screen alert messages so the AI can read why things fail.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "DoAlert")]
    static class Patch_Alert
    {
        static void Postfix(string text)
        {
            try { if (Plugin.Instance != null && Plugin.Instance.Agent != null) Plugin.Instance.Agent.AddAlert(text); } catch { }
        }
    }

    /// <summary>Hooks that tell the AI WHAT hurt it (traps, creatures, explosions, impacts). A missing method is simply skipped.</summary>
    public static class DamageHooks
    {
        static Body Local() { return PlayerCamera.main != null ? PlayerCamera.main.body : null; }

        static readonly string[][] Hooks =
        {
            new[] { "BearTrap", "OnTriggerEnter2D" },
            new[] { "BarbedFence", "OnTriggerEnter2D" },
            new[] { "CactusScript", "OnCollisionEnter2D" },
            new[] { "CoilScript", "Shock" },
            new[] { "MineScript", "OnCollisionEnter2D" },
            new[] { "SpikeStabberScript", "Stab" },
            new[] { "DamagingCrate", "OnCollisionEnter2D" },
            new[] { "StalactiteDropper", "Drop" },
            new[] { "SpiderHandler", "CheckForLimbDamage" }
        };

        public static void Install(Harmony h)
        {
            int ok = 0;
            Assembly game = typeof(Body).Assembly;
            foreach (var hk in Hooks)
            {
                try
                {
                    Type t = game.GetType(hk[0]);
                    if (t == null) continue;
                    MethodInfo m = AccessTools.Method(t, hk[1]);
                    if (m == null) continue;
                    h.Patch(m, postfix: new HarmonyMethod(typeof(DamageHooks), nameof(HazardPost)));
                    ok++;
                }
                catch (Exception e) { Plugin.Log.LogWarning("hook " + hk[0] + "." + hk[1] + ": " + e.Message); }
            }
            try
            {
                MethodInfo m = AccessTools.Method(typeof(Limb), "ImpactDamage");
                if (m != null) { h.Patch(m, postfix: new HarmonyMethod(typeof(DamageHooks), nameof(ImpactPost))); ok++; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("hook ImpactDamage: " + e.Message); }
            try
            {
                MethodInfo m = AccessTools.Method(typeof(WorldGeneration), "CreateExplosion");
                if (m != null) { h.Patch(m, postfix: new HarmonyMethod(typeof(DamageHooks), nameof(ExplosionPost))); ok++; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("hook CreateExplosion: " + e.Message); }
            Plugin.Log.LogInfo("Damage hooks installed: " + ok);
        }

        public static void HazardPost(object __instance, MethodBase __originalMethod)
        {
            try
            {
                Component c = __instance as Component;
                Body b = Local();
                if (c == null || b == null) return;
                float d = Vector2.Distance(c.transform.position, b.transform.position);
                if (d > 5f) return;
                string tn = c.GetType().Name;
                string label = Senser.HazardInfo.ContainsKey(tn) ? Senser.HazardInfo[tn].Key : tn.Replace("Handler", " creature").Replace("Script", "");
                DamageLog.Note(label + " triggered right next to you (" + d.ToString("0.0") + " blocks away, " + __originalMethod.Name + ")");
            }
            catch { }
        }

        public static void ImpactPost(Limb __instance, float force)
        {
            try
            {
                Body b = Local();
                if (b == null || __instance == null || __instance.body != b || force < 5f) return;
                DamageLog.Note("hard impact / fall on " + __instance.name + " (force " + Mathf.RoundToInt(force) + ")");
            }
            catch { }
        }

        public static void ExplosionPost(object[] __args)
        {
            try
            {
                Body b = Local();
                if (b == null || __args == null || __args.Length == 0 || __args[0] == null) return;
                object p = __args[0];
                Type t = p.GetType();
                object pos = null;
                FieldInfo f = t.GetField("position") ?? t.GetField("pos");
                if (f != null) pos = f.GetValue(p);
                else { PropertyInfo pr = t.GetProperty("position") ?? t.GetProperty("pos"); if (pr != null) pos = pr.GetValue(p, null); }
                Vector2 ep;
                if (pos is Vector2) ep = (Vector2)pos; else if (pos is Vector3) ep = (Vector3)pos; else return;
                float d = Vector2.Distance(ep, b.transform.position);
                if (d < 14f) DamageLog.Note("an explosion " + d.ToString("0.0") + " blocks away");
            }
            catch { }
        }
    }
}
