using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CasualtiesOllama
{
    public class Lesson
    {
        public string Text;
        public string Created;
        public int Importance = 1;
    }

    public class RunRecord
    {
        public string Ended;
        public string Summary;
        public int Turns;
        public float SurvivedSeconds;
    }

    public class TurnRecord
    {
        public int N;
        public string UserText;
        public string AssistantText;
    }

    /// <summary>
    /// One memory profile = one folder: lessons.json (long-term), runs.json (previous lives), notes.json (what the AI learned about items).
    /// Turns + Journal are short/medium-term and only live for the current life.
    /// </summary>
    public class MemoryStore
    {
        public readonly string Root, Profile, Dir;
        readonly string lessonsPath, runsPath, notesPath;

        public List<Lesson> Lessons = new List<Lesson>();
        public List<RunRecord> Runs = new List<RunRecord>();
        public Dictionary<string, string> Notes = new Dictionary<string, string>();   // item id -> short description
        public List<TurnRecord> Turns = new List<TurnRecord>();
        public string Journal = "";

        public static string Safe(string n)
        {
            if (string.IsNullOrWhiteSpace(n)) return "default";
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Trim();
        }

        public static string ProfilesDir(string root) { string d = Path.Combine(root, "memory"); Directory.CreateDirectory(d); return d; }

        public static List<string> ListProfiles(string root)
        {
            try { return Directory.GetDirectories(ProfilesDir(root)).Select(d => Path.GetFileName(d)).OrderBy(x => x).ToList(); }
            catch { return new List<string>(); }
        }

        public static void DeleteProfile(string root, string name)
        {
            try { string d = Path.Combine(ProfilesDir(root), Safe(name)); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        public MemoryStore(string root, string profile)
        {
            Root = root;
            Profile = Safe(profile);
            Dir = Path.Combine(ProfilesDir(root), Profile);
            Directory.CreateDirectory(Dir);
            lessonsPath = Path.Combine(Dir, "lessons.json");
            runsPath = Path.Combine(Dir, "runs.json");
            notesPath = Path.Combine(Dir, "notes.json");
            try { if (File.Exists(lessonsPath)) Lessons = JsonConvert.DeserializeObject<List<Lesson>>(File.ReadAllText(lessonsPath)) ?? new List<Lesson>(); } catch (Exception e) { Plugin.Log?.LogWarning("lessons.json: " + e.Message); }
            try { if (File.Exists(runsPath)) Runs = JsonConvert.DeserializeObject<List<RunRecord>>(File.ReadAllText(runsPath)) ?? new List<RunRecord>(); } catch (Exception e) { Plugin.Log?.LogWarning("runs.json: " + e.Message); }
            try { if (File.Exists(notesPath)) Notes = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(notesPath)) ?? new Dictionary<string, string>(); } catch (Exception e) { Plugin.Log?.LogWarning("notes.json: " + e.Message); }
            LoadState();
        }

        public Dictionary<string, List<string>> PlayerNotes = new Dictionary<string, List<string>>();   // player name -> long-term notes

        class SavedState { public string Journal; public List<TurnRecord> Turns; }

        string StatePath { get { return Path.Combine(Dir, "state.json"); } }
        string PlayersPath { get { return Path.Combine(Dir, "players.json"); } }

        void LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var st = JsonConvert.DeserializeObject<SavedState>(File.ReadAllText(StatePath));
                    if (st != null) { Journal = st.Journal ?? ""; Turns = st.Turns ?? new List<TurnRecord>(); }
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("state.json: " + e.Message); }
            try { if (File.Exists(PlayersPath)) PlayerNotes = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(PlayersPath)) ?? new Dictionary<string, List<string>>(); }
            catch (Exception e) { Plugin.Log?.LogWarning("players.json: " + e.Message); }
        }

        /// <summary>Saves the short-term memory (journal + recent turns) into this memory profile.</summary>
        public void SaveState()
        {
            try
            {
                int keep = Math.Max(2, Plugin.Instance != null ? Plugin.Instance.Cfg.HistoryTurns + 2 : 8);
                var turns = Turns.Skip(Math.Max(0, Turns.Count - keep)).ToList();
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(new SavedState { Journal = Journal, Turns = turns }, Formatting.Indented));
            }
            catch { }
        }

        public List<string> NotesFor(string player)
        {
            List<string> l;
            return (player != null && PlayerNotes.TryGetValue(player, out l)) ? l : new List<string>();
        }

        public bool AddPlayerNote(string player, string note)
        {
            if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(note)) return false;
            player = player.Trim(); note = note.Trim();
            if (note.Length > 200) note = note.Substring(0, 200);
            List<string> l;
            if (!PlayerNotes.TryGetValue(player, out l)) { l = new List<string>(); PlayerNotes[player] = l; }
            string n = Norm(note);
            foreach (var x in l) { string xn = Norm(x); if (xn == n || (n.Length > 20 && (xn.Contains(n) || n.Contains(xn)))) return false; }
            l.Add(note);
            while (l.Count > 10) l.RemoveAt(0);
            SavePlayers();
            return true;
        }

        public void RemovePlayerNote(string player, int i)
        {
            List<string> l;
            if (PlayerNotes.TryGetValue(player, out l) && i >= 0 && i < l.Count) { l.RemoveAt(i); if (l.Count == 0) PlayerNotes.Remove(player); SavePlayers(); }
        }

        public void SavePlayers() { try { File.WriteAllText(PlayersPath, JsonConvert.SerializeObject(PlayerNotes, Formatting.Indented)); } catch { } }

        /// <summary>Clears the journal and recent turns. A normal 'new life' keeps them when KeepShortMemory is on; force = true always clears.</summary>
        public void StartNewRun(bool force = false)
        {
            if (!force && Plugin.Instance != null && Plugin.Instance.Cfg.KeepShortMemory) return;
            Turns.Clear(); Journal = "";
            SaveState();
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            return new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        }

        public bool AddLesson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length > 280) text = text.Substring(0, 280);
            string n = Norm(text);
            foreach (var l in Lessons)
            {
                string ln = Norm(l.Text);
                if (ln == n || (n.Length > 25 && (ln.Contains(n) || n.Contains(ln)))) { l.Importance++; SaveLessons(); return false; }
            }
            Lessons.Add(new Lesson { Text = text, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Importance = 1 });
            while (Lessons.Count > 60)
            {
                var victim = Lessons.OrderBy(x => x.Importance).ThenBy(x => x.Created).First();
                Lessons.Remove(victim);
            }
            SaveLessons();
            return true;
        }

        public void RemoveLesson(int i) { if (i >= 0 && i < Lessons.Count) { Lessons.RemoveAt(i); SaveLessons(); } }
        public void ClearLessons() { Lessons.Clear(); SaveLessons(); }
        public List<Lesson> TopLessons(int n) { return Lessons.OrderByDescending(l => l.Importance).ThenByDescending(l => l.Created).Take(n).ToList(); }

        public void AddRun(RunRecord r)
        {
            Runs.Add(r);
            while (Runs.Count > 30) Runs.RemoveAt(0);
            try { File.WriteAllText(runsPath, JsonConvert.SerializeObject(Runs, Formatting.Indented)); } catch { }
        }

        public void SetNote(string itemId, string note)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            Notes[itemId] = note;
            SaveNotes();
        }

        public void RemoveNote(string itemId) { if (Notes.Remove(itemId)) SaveNotes(); }
        public void SaveLessons() { try { File.WriteAllText(lessonsPath, JsonConvert.SerializeObject(Lessons, Formatting.Indented)); } catch { } }
        public void SaveNotes() { try { File.WriteAllText(notesPath, JsonConvert.SerializeObject(Notes, Formatting.Indented)); } catch { } }
    }
}
