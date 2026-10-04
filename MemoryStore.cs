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
        }

        public void StartNewRun() { Turns.Clear(); Journal = ""; }

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
