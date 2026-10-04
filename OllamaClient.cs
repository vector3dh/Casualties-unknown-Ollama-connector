using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CasualtiesOllama
{
    public class ChatMessage
    {
        public string Role, Content;
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    public class ChatResult
    {
        public string Content = "";
        public int PromptTokens, EvalTokens;
    }

    /// <summary>Counts LLM calls and tokens so a cloud budget can be enforced. Thread-safe.</summary>
    public class RateLimiter
    {
        readonly object L = new object();
        readonly List<DateTime> calls = new List<DateTime>();
        readonly List<KeyValuePair<DateTime, int>> tokens = new List<KeyValuePair<DateTime, int>>();
        DateTime lastCall = DateTime.MinValue;
        public long TotalCalls, TotalTokens;

        public void Record(int tok)
        {
            lock (L)
            {
                DateTime now = DateTime.UtcNow;
                calls.Add(now); tokens.Add(new KeyValuePair<DateTime, int>(now, tok));
                lastCall = now; TotalCalls++; TotalTokens += tok;
                Prune(now);
            }
        }

        void Prune(DateTime now)
        {
            DateTime cutoff = now.AddHours(-1);
            calls.RemoveAll(c => c < cutoff);
            tokens.RemoveAll(t => t.Key < cutoff);
        }

        public int CallsLastMinute() { lock (L) { DateTime c = DateTime.UtcNow.AddMinutes(-1); return calls.Count(x => x >= c); } }
        public int CallsLastHour() { lock (L) { Prune(DateTime.UtcNow); return calls.Count; } }
        public int TokensLastHour() { lock (L) { Prune(DateTime.UtcNow); return tokens.Sum(t => t.Value); } }

        /// <summary>Returns true if a call may be made now. Otherwise wait = seconds to wait, why = reason, hard = budget exhausted.</summary>
        public bool Check(AiSettings c, out float wait, out string why, out bool hard)
        {
            wait = 0f; why = ""; hard = false;
            lock (L)
            {
                DateTime now = DateTime.UtcNow;
                Prune(now);
                if (c.MinSecondsBetweenCalls > 0f && lastCall != DateTime.MinValue)
                {
                    float w = c.MinSecondsBetweenCalls - (float)(now - lastCall).TotalSeconds;
                    if (w > wait) { wait = w; why = "minimum delay between calls"; }
                }
                if (c.MaxCallsPerMinute > 0)
                {
                    var recent = calls.Where(x => x >= now.AddMinutes(-1)).OrderBy(x => x).ToList();
                    if (recent.Count >= c.MaxCallsPerMinute)
                    {
                        float w = 60f - (float)(now - recent[recent.Count - c.MaxCallsPerMinute]).TotalSeconds;
                        if (w > wait) { wait = w; why = "calls per minute limit"; }
                    }
                }
                if (c.MaxCallsPerHour > 0 && calls.Count >= c.MaxCallsPerHour)
                {
                    var sorted = calls.OrderBy(x => x).ToList();
                    float w = 3600f - (float)(now - sorted[sorted.Count - c.MaxCallsPerHour]).TotalSeconds;
                    if (w > wait) { wait = w; why = "calls per hour limit"; if (c.StopWhenBudgetHit) hard = true; }
                }
                if (c.MaxTokensPerHour > 0 && tokens.Sum(t => t.Value) >= c.MaxTokensPerHour)
                {
                    var first = tokens.OrderBy(t => t.Key).First();
                    float w = 3600f - (float)(now - first.Key).TotalSeconds;
                    if (w > wait) { wait = w; why = "tokens per hour budget"; if (c.StopWhenBudgetHit) hard = true; }
                }
            }
            return wait <= 0f;
        }
    }

    /// <summary>Minimal blocking Ollama client. ALWAYS call from a background thread.</summary>
    public static class OllamaClient
    {
        static HttpWebRequest Make(AiSettings cfg, string url, string method, int timeoutSec)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) req.Headers["Authorization"] = "Bearer " + cfg.ApiKey.Trim();
            return req;
        }

        static string Read(HttpWebRequest req)
        {
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                string body = "";
                try { if (ex.Response != null) using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); } catch { }
                throw new Exception(ex.Message + (body.Length > 0 ? " | " + body : ""));
            }
        }

        static string Base(AiSettings cfg) { return cfg.OllamaUrl.TrimEnd('/'); }

        public static List<string> ListModels(AiSettings cfg)
        {
            var req = Make(cfg, Base(cfg) + "/api/tags", "GET", 10);
            var jo = JObject.Parse(Read(req));
            var list = new List<string>();
            var arr = jo["models"] as JArray;
            if (arr != null) foreach (var m in arr) { string n = (string)m["name"]; if (!string.IsNullOrEmpty(n)) list.Add(n); }
            list.Sort();
            return list;
        }

        public static JObject ActionSchema()
        {
            string[] verbs = { "move", "walk_to", "follow", "jump", "leap", "walljump", "climb", "crouch", "stand", "wait", "aim", "attack",
                               "use", "apply", "grab", "wear", "drop", "throw", "swap_hands", "swap_slots", "inspect", "craft", "relocate", "pull_shrapnel", "say" };
            var doEnum = new JArray(); foreach (var v in verbs) doEnum.Add(v);
            var props = new JObject
            {
                ["do"] = new JObject { ["type"] = "string", ["enum"] = doEnum },
                ["dir"] = new JObject { ["type"] = "string", ["enum"] = new JArray("left", "right", "up", "down", "none") },
                ["sec"] = new JObject { ["type"] = "number" },
                ["slot"] = new JObject { ["type"] = "integer" },
                ["slot2"] = new JObject { ["type"] = "integer" },
                ["target"] = new JObject { ["type"] = "string" },
                ["limb"] = new JObject { ["type"] = "string" },
                ["dx"] = new JObject { ["type"] = "number" },
                ["dy"] = new JObject { ["type"] = "number" },
                ["times"] = new JObject { ["type"] = "integer" },
                ["text"] = new JObject { ["type"] = "string" }
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["thought"] = new JObject { ["type"] = "string" },
                    ["actions"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray("do") } },
                    ["say"] = new JObject { ["type"] = "string" },
                    ["chat"] = new JObject { ["type"] = "string" },
                    ["lesson"] = new JObject { ["type"] = "string" }
                },
                ["required"] = new JArray("thought", "actions")
            };
        }

        public static ChatResult Chat(AiSettings cfg, List<ChatMessage> messages, bool useSchema, JObject customSchema = null, int numPredict = 0)
        {
            var msgs = new JArray();
            foreach (var m in messages) msgs.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
            var body = new JObject
            {
                ["model"] = cfg.Model,
                ["messages"] = msgs,
                ["stream"] = false,
                ["keep_alive"] = cfg.KeepAlive,
                ["options"] = new JObject
                {
                    ["temperature"] = cfg.Temperature,
                    ["num_ctx"] = cfg.NumCtx,
                    ["num_predict"] = numPredict > 0 ? numPredict : cfg.NumPredict
                }
            };
            if (useSchema) body["format"] = customSchema ?? ActionSchema();
            if (cfg.Think == "off") body["think"] = false; else if (cfg.Think == "on") body["think"] = true;

            string payload = body.ToString(Newtonsoft.Json.Formatting.None);
            var req = Make(cfg, Base(cfg) + "/api/chat", "POST", cfg.TimeoutSec);
            req.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

            var res = new ChatResult();
            try
            {
                var jo = JObject.Parse(Read(req));
                string content = (string)jo["message"]?["content"] ?? "";
                res.Content = Regex.Replace(content, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
                res.PromptTokens = (int?)jo["prompt_eval_count"] ?? 0;
                res.EvalTokens = (int?)jo["eval_count"] ?? 0;
                if (res.PromptTokens + res.EvalTokens == 0) res.PromptTokens = (payload.Length + content.Length) / 4;
            }
            finally
            {
                // count the call even when it failed: cloud services usually bill attempts too
                Plugin.Instance?.Limiter?.Record(res.PromptTokens + res.EvalTokens);
            }
            return res;
        }

        public static string ExtractJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int start = s.IndexOf('{');
            if (start < 0) return s;
            int depth = 0; bool inStr = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return s.Substring(start, i - start + 1); }
            }
            return s.Substring(start);
        }
    }
}
