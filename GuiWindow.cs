using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace CasualtiesOllama
{
    /// <summary>The in-game control panel (Unity IMGUI, no assets needed). F8 shows/hides it.</summary>
    public class GuiWindow
    {
        readonly Plugin P;
        AiSettings Cfg { get { return P.Cfg; } }

        Rect rect = new Rect(30, 30, 700, 760);
        bool visible = true;
        int tab;
        readonly string[] tabNames = { "Control", "Chat", "Mission", "Senses", "Allowed", "Model", "Limits", "Memory", "Profiles", "Multiplayer", "Log" };
        readonly Vector2[] scrolls = new Vector2[16];

        public bool Typing;
        public bool MouseOver;

        GUIStyle wrap, header, small;
        bool stylesReady;

        string chatInput = "", newLesson = "", profileName = "", memName = "", mpTest = "";
        string confirmDelProfile = "", confirmDelMem = "";
        bool confirmWipeLessons;
        string previewText = "";

        List<string> models = new List<string>();
        string modelStatus = "";
        bool fetching, fetchedOnce;
        readonly ConcurrentQueue<Action> mq = new ConcurrentQueue<Action>();

        bool dirty;
        Vector2 outerScroll;
        bool resizing;
        Vector2 resizeStartMouse, resizeStartSize;
        float lastSave;

        public GuiWindow(Plugin p) { P = p; }

        public void Toggle()
        {
            visible = !visible;
            if (!visible) { Typing = false; MouseOver = false; Cfg.Save(); }
        }

        public void UpdateMouseState()
        {
            if (!visible) { MouseOver = false; return; }
            Vector2 mp = Input.mousePosition;
            mp.y = Screen.height - mp.y;
            MouseOver = rect.Contains(mp);
        }

        void DrawResizeGrip()
        {
            Rect grip = new Rect(rect.width - 26f, rect.height - 26f, 24f, 24f);
            GUI.Box(grip, "//");
            Event e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition))
            {
                resizing = true;
                resizeStartMouse = Input.mousePosition;
                resizeStartSize = new Vector2(rect.width, rect.height);
                e.Use();
            }
        }

        void EnsureStyles()
        {
            if (stylesReady) return;
            wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
            header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
            stylesReady = true;
        }

        // ------------------------------------------------------------------ window
        public void Draw()
        {
            Action job;
            while (mq.TryDequeue(out job)) { try { job(); } catch { } }

            if (!visible) { Typing = false; return; }
            EnsureStyles();

            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.12f, 0.12f, 0.16f, 1f);
            rect = GUI.Window(94712, rect, DrawWindow, "Casualties Ollama Agent v2   [F8 hide | F9 start/stop | F10 emergency stop | F7 pointer]");
            GUI.backgroundColor = oldBg;

            if (resizing)
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 m = Input.mousePosition;
                    rect.width = Mathf.Clamp(resizeStartSize.x + (m.x - resizeStartMouse.x), 520f, Screen.width - 10f);
                    rect.height = Mathf.Clamp(resizeStartSize.y - (m.y - resizeStartMouse.y), 380f, Screen.height - 10f);
                }
                else resizing = false;
            }

            string focused = GUI.GetNameOfFocusedControl();
            Typing = !string.IsNullOrEmpty(focused) && focused.StartsWith("f_");

            rect.x = Mathf.Clamp(rect.x, -rect.width + 60, Screen.width - 60);
            rect.y = Mathf.Clamp(rect.y, 0, Screen.height - 30);

            if (dirty && Time.realtimeSinceStartup - lastSave > 2f) { Cfg.Save(); dirty = false; lastSave = Time.realtimeSinceStartup; }
        }

        void DrawWindow(int id)
        {
            if (!fetchedOnce) { fetchedOnce = true; FetchModels(); }

            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { GUI.FocusControl(""); e.Use(); }

            int newTab = GUILayout.SelectionGrid(tab, tabNames, 6);
            if (newTab != tab) { tab = newTab; GUI.FocusControl(""); }

            GUI.changed = false;
            GUILayout.Space(4);
            outerScroll = GUILayout.BeginScrollView(outerScroll, GUILayout.Height(Mathf.Max(200f, rect.height - 100f)));
            switch (tab)
            {
                case 0: TabControl(); break;
                case 1: TabChat(); break;
                case 2: TabMission(); break;
                case 3: TabSenses(); break;
                case 4: TabAllowed(); break;
                case 5: TabModel(); break;
                case 6: TabLimits(); break;
                case 7: TabMemory(); break;
                case 8: TabProfiles(); break;
                case 9: TabMultiplayer(); break;
                case 10: TabLog(); break;
            }
            GUILayout.EndScrollView();
            if (GUI.changed) dirty = true;
            DrawResizeGrip();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        // ------------------------------------------------------------------ helpers
        string Field(string name, string val, float width = -1f)
        {
            GUI.SetNextControlName("f_" + name);
            return width > 0f ? GUILayout.TextField(val ?? "", GUILayout.Width(width)) : GUILayout.TextField(val ?? "");
        }

        string Area(string name, string val, float height)
        {
            GUI.SetNextControlName("f_" + name);
            return GUILayout.TextArea(val ?? "", GUILayout.Height(height));
        }

        float Slider(string label, float v, float min, float max, string fmt = "0.##")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": " + v.ToString(fmt), GUILayout.Width(300));
            v = GUILayout.HorizontalSlider(v, min, max);
            GUILayout.EndHorizontal();
            return v;
        }

        int IntSlider(string label, int v, int min, int max) { return Mathf.RoundToInt(Slider(label, v, min, max, "0")); }

        bool Tg(bool v, string text) { return GUILayout.Toggle(v, text); }

        // ------------------------------------------------------------------ tabs
        void TabControl()
        {
            var a = P.Agent;
            GUI.backgroundColor = a.Active ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.3f, 0.8f, 0.4f);
            if (GUILayout.Button(a.Active ? "STOP AI" : "START AI", GUILayout.Height(40))) a.Toggle();
            GUI.backgroundColor = new Color(0.12f, 0.12f, 0.16f, 1f);

            GUILayout.Label("Status: " + a.Status, wrap);
            GUILayout.Label("Model: " + (string.IsNullOrEmpty(Cfg.Model) ? "(none - Model tab)" : Cfg.Model) + "   Game running: " + (a.HasBody ? "yes" : "no")
                + "   Multiplayer mod: " + (MP.Present ? "detected" : "not found"), wrap);
            GUILayout.Label("Turn " + a.TurnNo + "   last think " + a.LastThinkSeconds.ToString("0.0") + "s   queued " + a.QueueCount + "   now: " + a.CurrentActionText, wrap);
            GUILayout.Label("LLM usage: " + P.Limiter.CallsLastMinute() + " calls/min, " + P.Limiter.CallsLastHour() + " calls/hour, " + P.Limiter.TokensLastHour() + " tokens/hour", small);
            if (!string.IsNullOrEmpty(a.LastError))
            {
                Color c = GUI.contentColor; GUI.contentColor = new Color(1f, 0.5f, 0.5f);
                GUILayout.Label("Last error: " + a.LastError, wrap);
                GUI.contentColor = c;
            }

            GUILayout.Space(4);
            GUILayout.Label("Pointer - show the AI a spot", header);
            Pointer.Armed = Tg(Pointer.Armed, "Pointer tool ON  (left-click in the world = place, right-click = clear; game clicks are disabled while on)  [F7]");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode:", GUILayout.Width(50));
            Pointer.Mode = GUILayout.Toolbar(Pointer.Mode, new[] { "Look here (attention)", "Follow it (tracks your mouse)" });
            if (GUILayout.Button("Clear", GUILayout.Width(60))) Pointer.Clear();
            GUILayout.EndHorizontal();
            Cfg.AutoFollowPointer = Tg(Cfg.AutoFollowPointer, "Follow mode walks without calling the LLM (saves credits)");

            GUILayout.Space(4);
            GUILayout.Label("Quick options", header);
            Cfg.PauseWhileThinking = Tg(Cfg.PauseWhileThinking, "Pause the game while the AI is thinking (turn-based)");
            Cfg.BlockHumanInput = Tg(Cfg.BlockHumanInput, "Ignore my keyboard/mouse while the AI plays (F10 gives control back)");
            Cfg.ForceNormalSpeed = Tg(Cfg.ForceNormalSpeed, "Cancel the game's fast-forward while the AI plays");
            Cfg.AutoJumpHelper = Tg(Cfg.AutoJumpHelper, "Movement helper: auto-jump over small obstacles");
            Cfg.LedgeGuard = Tg(Cfg.LedgeGuard, "Ledge guard: refuse to walk off big drops (use 'leap' to cross gaps)");
            Cfg.AutoCrouch = Tg(Cfg.AutoCrouch, "Auto-crouch: crouch by itself in 1-block-high tunnels while walking");
            Cfg.AutoGrabRope = Tg(Cfg.AutoGrabRope, "Auto-grab ropes the moment they are in reach (also while falling past them)");

            GUILayout.Space(4);
            GUILayout.Label("What the AI is thinking", header);
            GUILayout.Label(string.IsNullOrEmpty(a.LastThought) ? "-" : a.LastThought, wrap);
            GUILayout.Label("Last observation sent to the AI", header);
            scrolls[0] = GUILayout.BeginScrollView(scrolls[0], GUILayout.Height(200));
            GUILayout.Label(string.IsNullOrEmpty(a.LastObservation) ? "(nothing yet)" : a.LastObservation, small);
            GUILayout.EndScrollView();
        }

        void TabChat()
        {
            var a = P.Agent;
            GUILayout.Label("Talk to the AI. It reads your message on its next turn and answers here. (Multiplayer chat is in the Multiplayer tab.)", wrap);
            scrolls[1] = GUILayout.BeginScrollView(scrolls[1], GUILayout.Height(470));
            foreach (var line in a.ChatLog) GUILayout.Label(line, wrap);
            GUILayout.EndScrollView();
            Cfg.InterruptOnChat = Tg(Cfg.InterruptOnChat, "My message (or a player's) interrupts the AI's current action queue");

            bool send = false;
            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "f_chat") { send = true; e.Use(); }
            GUILayout.BeginHorizontal();
            chatInput = Field("chat", chatInput);
            if (GUILayout.Button("Send", GUILayout.Width(70))) send = true;
            GUILayout.EndHorizontal();
            if (send && chatInput.Trim().Length > 0) { a.SendHumanMessage(chatInput); chatInput = ""; scrolls[1].y = 1e6f; }
        }

        void TabMission()
        {
            GUILayout.Label("Current mission / instructions (sent every turn)", header);
            Cfg.Mission = Area("mission", Cfg.Mission, 110);
            GUILayout.Space(6);
            GUILayout.Label("Premade game-knowledge prompt", header);
            bool custom = !string.IsNullOrWhiteSpace(Cfg.SystemPromptOverride);
            GUILayout.Label(custom ? "Using YOUR edited prompt." : "Using the built-in v2 prompt. 'Edit a copy' lets you change it.", wrap);
            GUILayout.BeginHorizontal();
            if (!custom && GUILayout.Button("Edit a copy")) Cfg.SystemPromptOverride = Prompts.DefaultSystem;
            if (custom && GUILayout.Button("Reset to built-in")) Cfg.SystemPromptOverride = "";
            GUILayout.EndHorizontal();
            scrolls[2] = GUILayout.BeginScrollView(scrolls[2], GUILayout.Height(400));
            if (custom)
            {
                GUILayout.Label("Keep the JSON format section; {NAME} and {MAXACTIONS} are filled in automatically.", small);
                Cfg.SystemPromptOverride = Area("sysprompt", Cfg.SystemPromptOverride, 1800);
            }
            else GUILayout.Label(Prompts.DefaultSystem, small);
            GUILayout.EndScrollView();
        }

        void TabSenses()
        {
            var s = Cfg.Sense;
            GUILayout.Label("Which senses does the AI get? (fewer = shorter prompts, faster, cheaper)", header);
            s.Vitals = Tg(s.Vitals, "Vitals: blood, oxygen, brain, pain, bleeding, heart rate");
            s.Needs = Tg(s.Needs, "Needs: hunger, thirst, stamina, energy, temperature");
            s.StatusIcons = Tg(s.StatusIcons, "Status icons at the bottom of the screen, as text");
            s.Limbs = Tg(s.Limbs, "Limb injuries");
            s.Collision = Tg(s.Collision, "Touch: ground, walls, ceiling, ledges, climbing");
            s.Map = Tg(s.Map, "Vision: terrain / spatial awareness");
            {
                string[] mapModes = { "compact", "ascii", "both" };
                int mmi = Array.IndexOf(mapModes, Cfg.MapMode); if (mmi < 0) mmi = 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("     Vision style:", GUILayout.Width(130));
                mmi = GUILayout.Toolbar(mmi, new[] { "compact text (few lines)", "ASCII picture", "both" });
                Cfg.MapMode = mapModes[mmi];
                GUILayout.EndHorizontal();
            }
            s.Items = Tg(s.Items, "Vision: nearby items");
            s.Objects = Tg(s.Objects, "Vision: world objects (crates, buttons, plants, trees) with health");
            s.Players = Tg(s.Players, "Vision: other players");
            s.Creatures = Tg(s.Creatures, "Vision: creatures");
            s.Hazards = Tg(s.Hazards, "Vision: traps and hazards");
            s.Climbables = Tg(s.Climbables, "Vision: ropes / ladders");
            s.Inventory = Tg(s.Inventory, "Inventory / hands");
            s.Wearing = Tg(s.Wearing, "Worn clothing and armor");
            s.Craftables = Tg(s.Craftables, "Recipes it can craft right now");
            s.Events = Tg(s.Events, "Events: 'your leg just took damage'");
            s.DamageLog = Tg(s.DamageLog, "What hit me (fall / trap / explosion / creature)");
            s.Chat = Tg(s.Chat, "Multiplayer chat lines");
            s.Pointer = Tg(s.Pointer, "The pointer you place");
            s.Position = Tg(s.Position, "Absolute position / depth");
            Cfg.ShowItemFlags = Tg(Cfg.ShowItemFlags, "Always show what each item can do (off = it must 'inspect' items to learn)");

            Cfg.MapHalfWidth = IntSlider("Map half-width", Cfg.MapHalfWidth, 3, 25);
            Cfg.MapHalfHeight = IntSlider("Map half-height", Cfg.MapHalfHeight, 2, 15);
            Cfg.ItemRadius = Slider("Item sight radius", Cfg.ItemRadius, 4f, 30f, "0");
            Cfg.CreatureRadius = Slider("Creature / trap sight radius", Cfg.CreatureRadius, 4f, 40f, "0");

            if (GUILayout.Button("Preview what the AI would see right now"))
            {
                var pc = PlayerCamera.main;
                if (pc != null && pc.body != null)
                {
                    try { var o = Senser.Build(pc.body, Cfg, new WorldIndex(), null); previewText = o.Compact + o.Map; }
                    catch (Exception ex) { previewText = "Error: " + ex.Message + "\n" + ex.StackTrace; }
                }
                else previewText = "(no player in the scene - load a run first)";
            }
            scrolls[3] = GUILayout.BeginScrollView(scrolls[3], GUILayout.Height(200));
            GUILayout.Label(previewText, small);
            GUILayout.EndScrollView();
        }

        void TabAllowed()
        {
            var a = Cfg.Allow;
            GUILayout.Label("What is the AI allowed to do? (forbidden actions are rejected and the AI is told)", header);
            a.Move = Tg(a.Move, "Move / walk_to / follow / climb");
            a.Jump = Tg(a.Jump, "Jump / leap / wall-jump");
            a.Crouch = Tg(a.Crouch, "Crouch");
            a.Aim = Tg(a.Aim, "Aim");
            a.Attack = Tg(a.Attack, "Attack / use the item in hand");
            a.UseItems = Tg(a.UseItems, "Use items (eat, drink, pills)");
            a.ApplyToLimbs = Tg(a.ApplyToLimbs, "Treat wounds (bandages, splints, injections, relocate, shrapnel)");
            a.Grab = Tg(a.Grab, "Pick up items");
            a.Wear = Tg(a.Wear, "Wear clothing / armor");
            a.Inventory = Tg(a.Inventory, "Rearrange inventory");
            a.DropThrow = Tg(a.DropThrow, "Drop / throw items");
            a.Craft = Tg(a.Craft, "Craft");
            a.Inspect = Tg(a.Inspect, "Inspect items");
            a.Speak = Tg(a.Speak, "Speak out loud in the game");
            a.Chat = Tg(a.Chat, "Write in the multiplayer chat");
            a.Interact = Tg(a.Interact, "Interact with world objects (buttons, crates, plants)");
            a.Storage = Tg(a.Storage, "Bags and combining: store / take items, batteries, tools on items");
            a.Carry = Tg(a.Carry, "Piggyback / carry players (multiplayer)");
            GUILayout.Space(6);
            Cfg.MaxActionsPerTurn = IntSlider("Max actions per AI turn", Cfg.MaxActionsPerTurn, 1, 8);
            GUILayout.Label("Fewer actions per turn = it reacts more often but calls the LLM more often.", small);
        }

        void TabModel()
        {
            GUILayout.Label("Ollama server", header);
            GUILayout.BeginHorizontal();
            Cfg.OllamaUrl = Field("url", Cfg.OllamaUrl);
            if (GUILayout.Button(fetching ? "..." : "Refresh models", GUILayout.Width(120))) FetchModels();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("API key (cloud, optional):", GUILayout.Width(170));
            GUI.SetNextControlName("f_apikey");
            Cfg.ApiKey = GUILayout.PasswordField(Cfg.ApiKey ?? "", '*');
            GUILayout.EndHorizontal();
            GUILayout.Label(modelStatus, small);

            GUILayout.Label("Model (click to select, or type a name)", header);
            scrolls[5] = GUILayout.BeginScrollView(scrolls[5], GUILayout.Height(110));
            foreach (var m in models) if (GUILayout.Button((m == Cfg.Model ? ">> " : "") + m)) { Cfg.Model = m; dirty = true; }
            GUILayout.EndScrollView();
            Cfg.Model = Field("model", Cfg.Model);

            Cfg.Temperature = Slider("Temperature", Cfg.Temperature, 0f, 1.5f, "0.00");
            Cfg.NumCtx = Mathf.RoundToInt(Slider("Context window (num_ctx)", Cfg.NumCtx, 2048, 32768, "0") / 1024f) * 1024;
            Cfg.NumPredict = Mathf.RoundToInt(Slider("Max reply tokens", Cfg.NumPredict, 150, 2000, "0") / 50f) * 50;
            Cfg.TimeoutSec = IntSlider("Request timeout (s)", Cfg.TimeoutSec, 20, 600);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Thinking mode (reasoning models):", GUILayout.Width(280));
            string[] opts = { "default", "off", "on" };
            int ti = Array.IndexOf(opts, Cfg.Think); if (ti < 0) ti = 0;
            ti = GUILayout.Toolbar(ti, opts);
            Cfg.Think = opts[ti];
            GUILayout.EndHorizontal();
            GUILayout.Label("Tip: small fast models with thinking 'off' play best. The API key is never written into profiles.", small);
            if (GUILayout.Button("Save settings now")) { Cfg.Save(); P.ParseKeys(); dirty = false; }
        }

        void TabLimits()
        {
            GUILayout.Label("Speed / credit limits for the LLM (0 = unlimited)", header);
            Cfg.MinSecondsBetweenCalls = Slider("Minimum seconds between calls", Cfg.MinSecondsBetweenCalls, 0f, 60f, "0.0");
            Cfg.MaxCallsPerMinute = IntSlider("Max calls per minute", Cfg.MaxCallsPerMinute, 0, 60);
            Cfg.MaxCallsPerHour = IntSlider("Max calls per hour", Cfg.MaxCallsPerHour, 0, 2000);
            Cfg.MaxTokensPerHour = Mathf.RoundToInt(Slider("Max tokens per hour", Cfg.MaxTokensPerHour, 0, 2000000, "0") / 10000f) * 10000;
            Cfg.StopWhenBudgetHit = Tg(Cfg.StopWhenBudgetHit, "Stop the AI completely when an hourly budget is used up (instead of waiting)");
            Cfg.PauseGameWhileRateLimited = Tg(Cfg.PauseGameWhileRateLimited, "Pause the game while waiting for the limit (otherwise the character stands still)");
            GUILayout.Space(6);
            GUILayout.Label("Live usage (this session)", header);
            GUILayout.Label("Last minute: " + P.Limiter.CallsLastMinute() + " calls   |   last hour: " + P.Limiter.CallsLastHour() + " calls, " + P.Limiter.TokensLastHour() + " tokens   |   total: " + P.Limiter.TotalCalls + " calls, " + P.Limiter.TotalTokens + " tokens", wrap);
            GUILayout.Label("Every request counts (including journal summaries and death analyses). Smaller maps, fewer senses, fewer history turns and 'follow' pointer mode all reduce tokens.", small);
        }

        void TabMemory()
        {
            var m = P.Mem;
            GUILayout.Label("What may the AI remember?", header);
            Cfg.ShortMemory = Tg(Cfg.ShortMemory, "Short-term memory (recent turns + journal of this life)");
            Cfg.LongMemory = Tg(Cfg.LongMemory, "Long-term memory (lessons, post-mortems, previous lives, notes about players)");
            Cfg.KeepShortMemory = Tg(Cfg.KeepShortMemory, "Save short-term memory + journal in the memory profile (survives restarts and new lives)");

            GUILayout.Label("Memory profile: " + m.Profile, header);
            string switchTo = null, createName = null;
            bool wantDelete = false;
            GUILayout.BeginHorizontal();
            foreach (var n in MemoryStore.ListProfiles(P.DataDir))
                if (GUILayout.Button((n == m.Profile ? ">> " : "") + n)) switchTo = n;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            memName = Field("memname", memName);
            if (GUILayout.Button("Create + switch", GUILayout.Width(120)) && memName.Trim().Length > 0) createName = memName;
            string del = confirmDelMem == m.Profile ? "Click again: DELETE '" + m.Profile + "'" : "Delete this profile";
            if (GUILayout.Button(del, GUILayout.Width(190))) wantDelete = true;
            GUILayout.EndHorizontal();
            if (switchTo != null) { P.SwitchMemory(switchTo); return; }
            if (createName != null) { P.SwitchMemory(createName); memName = ""; return; }
            if (wantDelete)
            {
                if (confirmDelMem == m.Profile && MemoryStore.ListProfiles(P.DataDir).Count > 1)
                {
                    string old = m.Profile; confirmDelMem = "";
                    var others = MemoryStore.ListProfiles(P.DataDir).Where(x => x != old).ToList();
                    P.SwitchMemory(others.Count > 0 ? others[0] : "default");
                    MemoryStore.DeleteProfile(P.DataDir, old);
                    return;
                }
                confirmDelMem = m.Profile;
            }

            GUILayout.Label("Lessons (long-term; the AI adds its own; you can add or delete)", header);
            scrolls[6] = GUILayout.BeginScrollView(scrolls[6], GUILayout.Height(150));
            int remove = -1;
            for (int i = 0; i < m.Lessons.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("x" + m.Lessons[i].Importance + "  " + m.Lessons[i].Text, wrap);
                if (GUILayout.Button("delete", GUILayout.Width(60))) remove = i;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (remove >= 0) m.RemoveLesson(remove);
            GUILayout.BeginHorizontal();
            newLesson = Field("newlesson", newLesson);
            if (GUILayout.Button("Add lesson", GUILayout.Width(90)) && newLesson.Trim().Length > 0) { m.AddLesson(newLesson); newLesson = ""; }
            GUILayout.EndHorizontal();
            if (GUILayout.Button(confirmWipeLessons ? "Click again to DELETE ALL lessons" : "Delete all lessons"))
            { if (confirmWipeLessons) { m.ClearLessons(); confirmWipeLessons = false; } else confirmWipeLessons = true; }

            GUILayout.Label("Item notes (what it learned by inspecting)", header);
            scrolls[7] = GUILayout.BeginScrollView(scrolls[7], GUILayout.Height(80));
            string rmNote = null;
            foreach (var kv in m.Notes)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Key + ": " + kv.Value, small);
                if (GUILayout.Button("x", GUILayout.Width(26))) rmNote = kv.Key;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (rmNote != null) m.RemoveNote(rmNote);

            GUILayout.Label("Notes about players (long-term; the AI writes them with note_player + note)", header);
            string rmPlayer = null; int rmIndex = -1;
            foreach (var kv in m.PlayerNotes)
                for (int pi = 0; pi < kv.Value.Count; pi++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(kv.Key + ": " + kv.Value[pi], small);
                    if (GUILayout.Button("x", GUILayout.Width(26))) { rmPlayer = kv.Key; rmIndex = pi; }
                    GUILayout.EndHorizontal();
                }
            if (rmPlayer != null) m.RemovePlayerNote(rmPlayer, rmIndex);

            GUILayout.Label("Journal of the current life (editable)", header);
            m.Journal = Area("journal", m.Journal, 70);
            GUILayout.Label("Short-term turns held: " + m.Turns.Count + "    previous lives: " + m.Runs.Count, small);
            if (GUILayout.Button("Clear journal + short-term memory (keeps lessons)")) P.Agent.ClearRunMemory();
        }

        void TabProfiles()
        {
            GUILayout.Label("Settings profiles (model, mission, senses, permissions, prompt, limits... never the API key)", header);
            GUILayout.Label("Active profile: " + (string.IsNullOrEmpty(Cfg.ActiveProfile) ? "(unsaved settings)" : Cfg.ActiveProfile), wrap);
            string loadName = null, delName = null;
            scrolls[8] = GUILayout.BeginScrollView(scrolls[8], GUILayout.Height(260));
            foreach (var n in ProfileStore.List(P.DataDir))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((n == Cfg.ActiveProfile ? ">> " : "") + n);
                if (GUILayout.Button("Load", GUILayout.Width(70))) loadName = n;
                if (GUILayout.Button("Overwrite", GUILayout.Width(80))) ProfileStore.Save(P.DataDir, n, Cfg);
                if (GUILayout.Button(confirmDelProfile == n ? "Sure?" : "Delete", GUILayout.Width(70))) delName = n;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (loadName != null) { P.LoadProfile(loadName); return; }
            if (delName != null)
            {
                if (confirmDelProfile == delName) { ProfileStore.Delete(P.DataDir, delName); confirmDelProfile = ""; return; }
                confirmDelProfile = delName;
            }
            GUILayout.BeginHorizontal();
            profileName = Field("profname", profileName);
            if (GUILayout.Button("Save current settings as...", GUILayout.Width(200)) && profileName.Trim().Length > 0) { ProfileStore.Save(P.DataDir, profileName, Cfg); Cfg.Save(); profileName = ""; }
            GUILayout.EndHorizontal();
            GUILayout.Label("A profile also remembers which memory profile it uses (Memory tab), so 'Medic' and 'Scout' can have separate lessons.", small);
        }

        void TabMultiplayer()
        {
            GUILayout.Label("Multiplayer (Casualties Together mod)", header);
            GUILayout.Label(MP.Present ? "Multiplayer mod detected. Your player name: " + (MP.LocalName().Length > 0 ? MP.LocalName() : "(not connected)") + "    chat access: " + (MP.ChatAvailable ? "yes" : "no")
                : "Multiplayer mod not found (the AI plays single-player). Install it and restart the game.", wrap);

            GUILayout.Label("Identity", header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("AI name (what players call it):", GUILayout.Width(230));
            Cfg.AiName = Field("ainame", Cfg.AiName);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Other names it answers to (a, b, c):", GUILayout.Width(230));
            Cfg.AiAliases = Field("aialiases", Cfg.AiAliases);
            GUILayout.EndHorizontal();

            GUILayout.Label("Chat", header);
            Cfg.ReadChat = Tg(Cfg.ReadChat, "Read the multiplayer chat (it appears in the AI's observation)");
            Cfg.ReplyInChat = Tg(Cfg.ReplyInChat, "Let the AI write in the multiplayer chat (uses the 'chat' field)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("React immediately to:", GUILayout.Width(170));
            string[] modes = { "never", "mentions", "all" };
            int mi = Array.IndexOf(modes, Cfg.ChatMode); if (mi < 0) mi = 1;
            mi = GUILayout.Toolbar(mi, new[] { "never", "its name is said", "every message" });
            Cfg.ChatMode = modes[mi];
            GUILayout.EndHorizontal();
            Cfg.ChatLines = IntSlider("Chat lines shown to the AI", Cfg.ChatLines, 1, 20);
            Cfg.ChatCooldown = Slider("Min seconds between its chat messages", Cfg.ChatCooldown, 0f, 30f, "0.0");

            GUILayout.Label("Recent chat", header);
            scrolls[9] = GUILayout.BeginScrollView(scrolls[9], GUILayout.Height(150));
            foreach (var l in P.Agent.MpChat.Skip(Math.Max(0, P.Agent.MpChat.Count - 25)))
                GUILayout.Label("[" + l.Name + "] " + l.Text + (l.Mention ? "   (mentions the AI)" : ""), small);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            mpTest = Field("mptest", mpTest);
            if (GUILayout.Button("Send as AI", GUILayout.Width(90)) && mpTest.Trim().Length > 0) { P.Agent.SendChat(mpTest); mpTest = ""; }
            GUILayout.EndHorizontal();

            GUILayout.Label("Players in the scene", header);
            var pl = MP.Players();
            if (pl.Count == 0) GUILayout.Label("(none)", small);
            foreach (var x in pl) GUILayout.Label("  " + x.Name + (x.IsLocal ? "  (you / the AI's own body)" : ""), small);
        }

        void TabLog()
        {
            var a = P.Agent;
            GUILayout.Label("Activity log (newest at the bottom)", header);
            scrolls[10] = GUILayout.BeginScrollView(scrolls[10], GUILayout.Height(400));
            foreach (var l in a.Log) GUILayout.Label(l, small);
            GUILayout.EndScrollView();
            GUILayout.Label("Last raw model reply", header);
            scrolls[11] = GUILayout.BeginScrollView(scrolls[11], GUILayout.Height(170));
            GUILayout.Label(string.IsNullOrEmpty(a.LastRaw) ? "-" : a.LastRaw, small);
            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ model list
        void FetchModels()
        {
            if (fetching) return;
            fetching = true;
            modelStatus = "Contacting Ollama...";
            var cfg = Cfg;
            Task.Run(() =>
            {
                try
                {
                    var list = OllamaClient.ListModels(cfg);
                    mq.Enqueue(() =>
                    {
                        models = list;
                        modelStatus = list.Count + " model(s) found on " + cfg.OllamaUrl;
                        if (string.IsNullOrEmpty(cfg.Model) && list.Count > 0) cfg.Model = list[0];
                        fetching = false;
                    });
                }
                catch (Exception ex) { string msg = ex.Message; mq.Enqueue(() => { modelStatus = "Could not reach Ollama: " + msg; fetching = false; }); }
            });
        }
    }
}
