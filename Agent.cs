using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesOllama
{
    /// <summary>
    /// The autonomous loop: observe -> ask Ollama -> queue the returned actions -> execute them -> repeat.
    /// Network calls run on a background thread; everything that touches Unity runs on the main thread.
    /// </summary>
    public class Agent
    {
        const string JournalMarker = "\u0001JOURNAL\u0001";

        readonly Plugin P;
        public readonly Executor Exec;
        AiSettings Cfg { get { return P.Cfg; } }
        MemoryStore Mem { get { return P.Mem; } }
        RateLimiter Limiter { get { return P.Limiter; } }

        // ---- visible to the GUI ----
        public bool Active { get; private set; }
        public string Status = "Idle";
        public string LastThought = "", LastRaw = "", LastObservation = "", LastError = "";
        public int TurnNo;
        public float LastThinkSeconds;
        public readonly List<string> ChatLog = new List<string>();     // operator <-> AI
        public readonly List<ChatLine> MpChat = new List<ChatLine>();  // multiplayer chat history
        public readonly List<string> Log = new List<string>();
        public bool Thinking { get { return thinking; } }
        public string CurrentActionText { get { return cur != null ? cur.Describe() : "-"; } }
        public int QueueCount { get { return queue.Count; } }
        public bool HasBody { get { return lastBody != null; } }
        public bool SkipHumanInput { get { return Active && Cfg.BlockHumanInput; } }

        // ---- internals ----
        readonly ConcurrentQueue<Action> mainQ = new ConcurrentQueue<Action>();
        readonly Queue<AiAction> queue = new Queue<AiAction>();
        AiAction cur;
        bool thinking, pausedByUs, rateWaiting;
        int reqId;
        float thinkStartRT, lastThinkEnd, savedTimeScale = 1f, nextChatPoll, lastChatSent = -99f;
        WorldIndex idx = new WorldIndex();
        Snapshot prevSnap;
        Body lastBody;
        float runStartTime;
        readonly List<string> pendingResults = new List<string>();
        readonly List<string> pendingHuman = new List<string>();
        readonly List<string> pendingAlerts = new List<string>();
        readonly List<string> sentByUs = new List<string>();
        string parseFeedback;
        int consecutiveFailures;
        bool postmortemDone;
        string logFile;

        public Agent(Plugin p)
        {
            P = p;
            Exec = new Executor(p);
            try
            {
                string dir = Path.Combine(p.DataDir, "logs");
                Directory.CreateDirectory(dir);
                logFile = Path.Combine(dir, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
            }
            catch { logFile = null; }
        }

        // ------------------------------------------------------------------ controls
        public void Start()
        {
            if (string.IsNullOrEmpty(Cfg.Model)) { Status = "Pick a model first (Model tab)"; AddLog("Cannot start: no model selected."); return; }
            Active = true;
            consecutiveFailures = 0;
            lastThinkEnd = 0f;
            Status = "Started";
            AddLog("AI started with model " + Cfg.Model);
        }

        public void Stop(string reason)
        {
            if (Active) AddLog("AI stopped: " + reason);
            Active = false; thinking = false; rateWaiting = false;
            reqId++;
            queue.Clear(); cur = null;
            Exec.ResetControls();
            ReleasePause();
            Status = "Stopped: " + reason;
        }

        public void Toggle() { if (Active) Stop("toggled off"); else Start(); }

        void Interrupt()
        {
            queue.Clear(); cur = null;
            Exec.StopMotion();
            lastThinkEnd = 0f;
        }

        public void SendHumanMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            AddChat("You", text);
            pendingHuman.Add(text);
            if (Cfg.InterruptOnChat && Active) Interrupt();
            if (!Active) AddChat("system", "(the AI is stopped - it will read this when you start it)");
        }

        public void ClearRunMemory() { Mem.StartNewRun(); pendingResults.Clear(); AddLog("Short-term memory and journal cleared."); }

        public void AddLog(string s)
        {
            Log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s);
            while (Log.Count > 300) Log.RemoveAt(0);
            Plugin.Log?.LogInfo(s);
        }

        public void AddChat(string who, string text)
        {
            ChatLog.Add(who + ": " + text);
            while (ChatLog.Count > 200) ChatLog.RemoveAt(0);
        }

        /// <summary>Called by the DoAlert hook: the game's own on-screen messages (they explain why things fail).</summary>
        public void AddAlert(string text)
        {
            text = Senser.Strip(text);
            if (text.Length == 0) return;
            if (pendingAlerts.Count > 0 && pendingAlerts[pendingAlerts.Count - 1] == text) return;
            pendingAlerts.Add(Senser.Trunc(text, 160));
            while (pendingAlerts.Count > 6) pendingAlerts.RemoveAt(0);
        }

        void FileLog(object o)
        {
            if (!Cfg.LogToFile || logFile == null) return;
            try { File.AppendAllText(logFile, JsonConvert.SerializeObject(o) + "\n"); } catch { }
        }

        // ------------------------------------------------------------------ multiplayer chat
        bool Mentions(string text)
        {
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(Cfg.AiName)) names.Add(Cfg.AiName.Trim());
            if (!string.IsNullOrWhiteSpace(Cfg.AiAliases)) names.AddRange(Cfg.AiAliases.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
            foreach (var n in names) if (text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        void PollChat()
        {
            if (!Cfg.ReadChat || !MP.ChatAvailable || Time.realtimeSinceStartup < nextChatPoll) return;
            nextChatPoll = Time.realtimeSinceStartup + 0.5f;
            foreach (var line in MP.PollNew())
            {
                if (sentByUs.Contains(line.Text)) { sentByUs.Remove(line.Text); continue; }   // our own message echoed back
                line.Mention = !line.Server && Mentions(line.Text);
                MpChat.Add(line);
                while (MpChat.Count > 60) MpChat.RemoveAt(0);
                bool react = Active && !line.Server && !line.FromLocal && Cfg.ChatMode != "never" && (Cfg.ChatMode == "all" || line.Mention);
                if (react)
                {
                    AddLog("CHAT from " + line.Name + (line.Mention ? " (mentions me)" : "") + ": " + line.Text);
                    if (Cfg.InterruptOnChat) Interrupt(); else lastThinkEnd = 0f;
                }
            }
        }

        public void SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 150) text = text.Substring(0, 150);
            if (!Cfg.Allow.Chat) { AddChat("system", "(chat not allowed) " + text); return; }
            if (Time.realtimeSinceStartup - lastChatSent < Cfg.ChatCooldown) { AddChat("system", "(chat throttled) " + text); return; }
            lastChatSent = Time.realtimeSinceStartup;
            AddChat("AI -> multiplayer chat", text);
            if (Cfg.ReplyInChat && MP.Present)
            {
                sentByUs.Add(text);
                while (sentByUs.Count > 20) sentByUs.RemoveAt(0);
                MP.Send(text);
            }
        }

        // ------------------------------------------------------------------ pause handling
        void HoldPause()
        {
            if (!pausedByUs) { savedTimeScale = Time.timeScale > 0.01f ? Time.timeScale : 1f; pausedByUs = true; }
            Time.timeScale = 0f;
        }

        void ReleasePause()
        {
            if (!pausedByUs) return;
            pausedByUs = false;
            Time.timeScale = savedTimeScale > 0.01f ? savedTimeScale : 1f;
        }

        // ------------------------------------------------------------------ per-frame update (main thread)
        static Body CurrentBody() { var pc = PlayerCamera.main; return pc == null ? null : pc.body; }

        public void Tick()
        {
            Action job;
            while (mainQ.TryDequeue(out job)) { try { job(); } catch (Exception e) { Plugin.Log?.LogError(e); } }

            Body b = CurrentBody();
            if (b != lastBody) { lastBody = b; if (b != null) OnNewBody(); }

            PollChat();

            if (!Active) { ReleasePause(); return; }
            if (b == null) { Status = "Waiting for a game (no player in the scene)"; Exec.ResetControls(); ReleasePause(); return; }
            if (!b.alive) { OnDeath(); return; }

            float dt = Time.deltaTime;
            Exec.Update(dt);

            if ((thinking && Cfg.PauseWhileThinking) || rateWaiting) HoldPause(); else ReleasePause();
            if (!thinking && !rateWaiting && Cfg.ForceNormalSpeed && Time.timeScale > 1.01f && PlayerCamera.main != null) PlayerCamera.main.SetTimeScale(0, false);

            if (thinking) { Status = "Thinking... " + (Time.realtimeSinceStartup - thinkStartRT).ToString("0") + "s"; return; }

            if (cur == null && queue.Count > 0) cur = queue.Dequeue();
            if (cur != null)
            {
                string res; bool done;
                try { done = Exec.Step(cur, b, idx, dt, out res); }
                catch (Exception e) { done = true; res = "internal error: " + e.Message; Plugin.Log?.LogError(e); }
                Status = "Acting: " + cur.Describe();
                if (done)
                {
                    if (cur.Text != "__auto")
                    {
                        string line = cur.Describe() + "  =>  " + res;
                        pendingResults.Add(line);
                        AddLog("ACTION " + line);
                    }
                    cur = null;
                }
                return;
            }

            if (Time.realtimeSinceStartup - lastThinkEnd < Cfg.MinThinkInterval) return;

            // follow the human's pointer without spending an LLM call
            if (Cfg.AutoFollowPointer && Pointer.Set && Pointer.Mode == 1 && prevSnap != null && b.conscious && b.totalBleedSpeed < 0.6f
                && pendingHuman.Count == 0 && Time.realtimeSinceStartup - lastThinkEnd < 10f)
            {
                queue.Enqueue(new AiAction { Do = "follow", Target = "PTR", Sec = 1.5f, Text = "__auto" });
                Status = "Following the pointer";
                return;
            }

            float wait; string why; bool hard;
            if (!Limiter.Check(Cfg, out wait, out why, out hard))
            {
                if (hard) { Stop("budget reached (" + why + ")"); return; }
                rateWaiting = Cfg.PauseGameWhileRateLimited;
                Exec.StopMotion();
                Status = "Rate limited: " + why + " (" + wait.ToString("0") + "s)";
                return;
            }
            rateWaiting = false;
            RequestThink(b);
        }

        public void ApplyControls(PlayerCamera pc)
        {
            if (!Active || pc == null) return;
            Body b = pc.body;
            if (b == null || !b.alive) return;
            Exec.Apply(b);
        }

        // ------------------------------------------------------------------ run lifecycle
        void OnNewBody()
        {
            Mem.StartNewRun();
            prevSnap = null; queue.Clear(); cur = null; TurnNo = 0; postmortemDone = false;
            runStartTime = Time.time;
            pendingResults.Clear();
            Exec.ResetControls();
            AddLog("New character / run detected.");
        }

        void OnDeath()
        {
            if (postmortemDone) return;
            postmortemDone = true;
            Active = false; queue.Clear(); cur = null;
            Exec.ResetControls(); ReleasePause();
            Status = "The character died.";
            AddLog("The character died.");
            if (!Cfg.LongMemory) return;
            AddLog("Writing a post-mortem...");

            var turns = Mem.Turns.Skip(Math.Max(0, Mem.Turns.Count - 4)).ToList();
            string journal = Mem.Journal;
            string lastResults = string.Join("\n", pendingResults.ToArray());
            string lastHits = string.Join("; ", DamageLog.Drain().ToArray());
            float survived = Time.time - runStartTime;
            int nTurns = TurnNo;
            var cfg = Cfg;

            Task.Run(() =>
            {
                string cause = "Died after " + (int)survived + "s (no analysis available).";
                var lessons = new List<string>();
                try
                {
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(journal)) sb.AppendLine("JOURNAL:\n" + journal + "\n");
                    sb.AppendLine("LAST TURNS:");
                    foreach (var t in turns) sb.AppendLine(t.UserText + "\nYOU REPLIED: " + t.AssistantText + "\n");
                    if (lastResults.Length > 0) sb.AppendLine("FINAL ACTION RESULTS:\n" + lastResults);
                    if (lastHits.Length > 0) sb.AppendLine("LAST DAMAGE SOURCES: " + lastHits);
                    var msgs = new List<ChatMessage>
                    {
                        new ChatMessage("system", "You analyse why a survival-game character died so the next attempt can do better. Be concrete and brief."),
                        new ChatMessage("user", sb + "\nThe character has just died. Reply with JSON: {\"cause\": \"one or two sentences on what killed it and which decisions led there\", \"lessons\": [\"up to 3 SHORT, GENERAL, reusable rules\"]}")
                    };
                    var schema = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["cause"] = new JObject { ["type"] = "string" },
                            ["lessons"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }
                        },
                        ["required"] = new JArray("cause", "lessons")
                    };
                    var r = OllamaClient.Chat(cfg, msgs, true, schema, 400);
                    var jo = JObject.Parse(OllamaClient.ExtractJson(r.Content));
                    string c = (string)jo["cause"];
                    if (!string.IsNullOrWhiteSpace(c)) cause = c;
                    var arr = jo["lessons"] as JArray;
                    if (arr != null) foreach (var l in arr) lessons.Add(l.ToString());
                }
                catch (Exception e) { Plugin.Log?.LogWarning("Post-mortem failed: " + e.Message); }
                mainQ.Enqueue(() =>
                {
                    Mem.AddRun(new RunRecord { Ended = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Summary = cause, Turns = nTurns, SurvivedSeconds = survived });
                    foreach (var l in lessons) if (Mem.AddLesson(l)) AddLog("LESSON (from death): " + l);
                    AddChat("system", "Post-mortem: " + cause);
                });
            });
        }

        // ------------------------------------------------------------------ the think step
        string PointerText(Body b)
        {
            if (!Cfg.Sense.Pointer || !Pointer.Set) return "";
            Vector2 p = b.transform.position, w = Pointer.World;
            string near = "";
            float best = 2.2f;
            Action<string, Vector2> test = (label, pos) => { float d = Vector2.Distance(pos, w); if (d < best) { best = d; near = label; } };
            for (int i = 0; i < idx.Items.Count; i++) if (idx.Items[i] != null) test("I" + (i + 1), idx.Items[i].transform.position);
            for (int i = 0; i < idx.Creatures.Count; i++) if (idx.Creatures[i] != null) test("E" + (i + 1), idx.Creatures[i].transform.position);
            for (int i = 0; i < idx.Hazards.Count; i++) if (idx.Hazards[i] != null) test("H" + (i + 1), idx.Hazards[i].transform.position);
            for (int i = 0; i < idx.Players.Count; i++) if (idx.Players[i] != null) test("P" + (i + 1), idx.Players[i].transform.position);
            return "HUMAN POINTER (target PTR): dx=" + (w.x - p.x).ToString("+0.0;-0.0;0.0") + " dy=" + (w.y - p.y).ToString("+0.0;-0.0;0.0")
                + " dist=" + Vector2.Distance(p, w).ToString("0.0")
                + (near.Length > 0 ? " - right next to " + near : "")
                + (Pointer.Mode == 1 ? "\n  The human wants you to FOLLOW this pointer (use follow target PTR)." : "\n  The human wants you to look at / pay attention to this spot.") + "\n";
        }

        void RequestThink(Body b)
        {
            if (string.IsNullOrEmpty(Cfg.Model)) { Stop("no model selected"); return; }

            TurnNo++;
            idx = new WorldIndex();
            Observation obs = Senser.Build(b, Cfg, idx, prevSnap);
            prevSnap = obs.Snap;

            var extra = new StringBuilder();
            if (pendingResults.Count > 0)
            {
                extra.AppendLine("RESULTS OF YOUR LAST ACTIONS:");
                foreach (var r in pendingResults) extra.AppendLine("  - " + r);
                pendingResults.Clear();
            }
            if (pendingAlerts.Count > 0)
            {
                extra.AppendLine("GAME MESSAGES (alerts shown on screen):");
                foreach (var a in pendingAlerts) extra.AppendLine("  - " + a);
                pendingAlerts.Clear();
            }
            extra.Append(PointerText(b));
            if (Cfg.ReadChat && Cfg.Sense.Chat && MpChat.Count > 0)
            {
                var recent = MpChat.Skip(Math.Max(0, MpChat.Count - Cfg.ChatLines)).ToList();
                extra.AppendLine("MULTIPLAYER CHAT (* = new; answer players in the 'chat' field; you are called " + Cfg.AiName + "):");
                foreach (var l in recent)
                    extra.AppendLine((l.New ? "  * " : "    ") + "[" + l.Name + (l.FromLocal ? " (operator)" : "") + "]: " + l.Text + (l.Mention && l.New ? "   <-- talking to YOU" : ""));
                foreach (var l in MpChat) l.New = false;
            }
            if (pendingHuman.Count > 0)
            {
                extra.AppendLine("MESSAGE FROM HUMAN (reply in the 'say' field):");
                foreach (var h in pendingHuman) extra.AppendLine("  \"" + h + "\"");
                pendingHuman.Clear();
            }
            if (parseFeedback != null) { extra.AppendLine(parseFeedback); parseFeedback = null; }

            string header = "=== TURN " + TurnNo + " ===\n";
            string compact = header + obs.Compact + extra;
            string full = header + obs.Compact + extra + obs.Map + "Reply with the JSON object now.";
            LastObservation = full;

            int keep = Cfg.ShortMemory ? Mathf.Max(1, Cfg.HistoryTurns) : 0;
            int oldCount = Mem.Turns.Count - keep;
            List<TurnRecord> toSummarize = null;
            if (Cfg.ShortMemory && oldCount >= Mathf.Max(2, Cfg.SummarizeEvery)) toSummarize = Mem.Turns.Take(oldCount).ToList();

            var recentTurns = keep > 0 ? Mem.Turns.Skip(Math.Max(0, Mem.Turns.Count - keep)).ToList() : new List<TurnRecord>();
            string system = Prompts.BuildSystem(Cfg, Mem, toSummarize != null ? JournalMarker : null);
            var msgs = new List<ChatMessage> { new ChatMessage("system", system) };
            foreach (var t in recentTurns) { msgs.Add(new ChatMessage("user", t.UserText)); msgs.Add(new ChatMessage("assistant", t.AssistantText)); }
            msgs.Add(new ChatMessage("user", full));

            string oldJournal = Mem.Journal;
            var cfg = Cfg;
            int myId = ++reqId;
            thinking = true;
            thinkStartRT = Time.realtimeSinceStartup;
            if (Cfg.PauseWhileThinking) HoldPause();
            Status = "Thinking...";

            Task.Run(() =>
            {
                try
                {
                    string newJournal = null;
                    if (toSummarize != null)
                    {
                        try { newJournal = Summarize(cfg, oldJournal, toSummarize); }
                        catch (Exception e) { Plugin.Log?.LogWarning("Summarize failed: " + e.Message); }
                        string j = newJournal ?? oldJournal ?? "";
                        msgs[0] = new ChatMessage("system", system.Replace(JournalMarker, j));
                    }
                    var r = OllamaClient.Chat(cfg, msgs, true);
                    mainQ.Enqueue(() => OnResponse(myId, compact, r.Content, newJournal, toSummarize));
                }
                catch (Exception e) { mainQ.Enqueue(() => OnError(myId, e)); }
            });
        }

        static string Summarize(AiSettings cfg, string journal, List<TurnRecord> turns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CURRENT JOURNAL:\n" + (string.IsNullOrWhiteSpace(journal) ? "(empty)" : journal) + "\n");
            sb.AppendLine("OLDER TURNS TO FOLD IN:");
            foreach (var t in turns) sb.AppendLine(t.UserText + "\nYOU REPLIED: " + t.AssistantText + "\n");
            sb.AppendLine("Write the UPDATED journal in at most 160 words of plain text: where you are and what you explored, what you found and carry, injuries and how they were treated, traps/dangers you met, what worked, what failed, what players said, and your current plan. No JSON.");
            var msgs = new List<ChatMessage>
            {
                new ChatMessage("system", "You maintain the memory journal of an AI playing a survival game. Be factual, compact, and keep information that will matter later."),
                new ChatMessage("user", sb.ToString())
            };
            return OllamaClient.Chat(cfg, msgs, false, null, 450).Content.Trim();
        }

        void OnResponse(int id, string compact, string raw, string newJournal, List<TurnRecord> summarized)
        {
            if (id != reqId || !Active) { thinking = false; ReleasePause(); return; }
            thinking = false;
            lastThinkEnd = Time.realtimeSinceStartup;
            LastThinkSeconds = lastThinkEnd - thinkStartRT;
            ReleasePause();
            LastRaw = raw;

            if (newJournal != null && summarized != null)
            {
                Mem.Journal = newJournal;
                Mem.Turns.RemoveAll(t => summarized.Contains(t));
                AddLog("Journal updated (" + newJournal.Length + " chars).");
            }
            if (Cfg.ShortMemory) Mem.Turns.Add(new TurnRecord { N = TurnNo, UserText = compact, AssistantText = raw });
            FileLog(new { time = DateTime.Now.ToString("o"), turn = TurnNo, seconds = LastThinkSeconds, observation = LastObservation, reply = raw });

            JObject jo;
            try { jo = JObject.Parse(OllamaClient.ExtractJson(raw)); }
            catch
            {
                consecutiveFailures++;
                parseFeedback = "NOTE: your previous reply was not valid JSON. Reply with ONLY the JSON object described in the instructions.";
                AddLog("Reply was not valid JSON (" + consecutiveFailures + "x): " + Senser.Trunc(raw, 120));
                if (consecutiveFailures >= 4) Stop("the model keeps returning invalid JSON (try another model or a lower temperature)");
                return;
            }
            consecutiveFailures = 0;

            string thought = (string)jo["thought"] ?? "";
            string say = (string)jo["say"] ?? "";
            string chat = (string)jo["chat"] ?? "";
            string lesson = (string)jo["lesson"] ?? "";
            LastThought = thought;
            AddLog("THOUGHT " + thought);

            if (!string.IsNullOrWhiteSpace(say))
            {
                AddChat("AI", say);
                if (Cfg.Allow.Speak && lastBody != null && lastBody.talker != null) { try { lastBody.talker.Talk(say); } catch { } }
            }
            if (!string.IsNullOrWhiteSpace(chat)) SendChat(chat);
            if (Cfg.LongMemory && !string.IsNullOrWhiteSpace(lesson) && Mem.AddLesson(lesson)) AddLog("LESSON: " + lesson);

            Exec.ResetAim();
            var arr = jo["actions"] as JArray;
            int n = 0;
            if (arr != null)
                foreach (var tok in arr)
                {
                    var o = tok as JObject;
                    if (o == null) continue;
                    AiAction a = AiAction.FromJson(o);
                    if (a == null) continue;
                    queue.Enqueue(a);
                    if (++n >= Mathf.Max(1, Cfg.MaxActionsPerTurn)) break;
                }
            if (n == 0)
            {
                queue.Enqueue(new AiAction { Do = "wait", Sec = 1f });
                pendingResults.Add("(you returned no valid actions, so the game waited 1 second)");
            }
        }

        void OnError(int id, Exception e)
        {
            if (id != reqId) { thinking = false; ReleasePause(); return; }
            thinking = false;
            ReleasePause();
            LastError = e.Message;
            consecutiveFailures++;
            AddLog("Ollama error (" + consecutiveFailures + "x): " + e.Message);
            if (consecutiveFailures >= 3) { Stop("Ollama error: " + e.Message); return; }
            lastThinkEnd = Time.realtimeSinceStartup + 2f;
        }
    }
}
