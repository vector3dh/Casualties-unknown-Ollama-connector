using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace CasualtiesOllama
{
    public class PlayerInfo
    {
        public Body Body;
        public string Name;
        public bool IsLocal;
    }

    public class ChatLine
    {
        public string Name, Text;
        public bool Server, FromLocal, Mention, New = true;
    }

    /// <summary>
    /// Bridge to the "Casualties Together" multiplayer mod (KrokoshaCasualtiesMP.dll).
    /// Everything is done through reflection, so this mod works with or without the multiplayer mod installed.
    /// </summary>
    public static class MP
    {
        static bool ready;
        static float nextTry;
        static Assembly asm;
        static Type tPlayer, tChat, tMsg;
        static FieldInfo fDict, fLocalPlayer, fName, fChatLog, fcName, fcMsg;
        static PropertyInfo pLocal;
        static MethodInfo mSend;
        static readonly HashSet<object> seen = new HashSet<object>();
        static bool firstPoll = true;

        public static bool Present { get { Init(); return ready; } }

        static void Init()
        {
            if (ready || Time.realtimeSinceStartup < nextTry) return;
            nextTry = Time.realtimeSinceStartup + 5f;
            try
            {
                asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "KrokoshaCasualtiesMP");
                if (asm == null) return;
                BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                tPlayer = asm.GetType("KrokoshaCasualtiesMP.NetPlayer");
                tChat = asm.GetType("KrokoshaCasualtiesMP.Chat");
                if (tPlayer != null)
                {
                    fDict = tPlayer.GetField("BodyToPlayerDict", all);
                    fLocalPlayer = tPlayer.GetField("LOCAL_PLAYER", all);
                    fName = tPlayer.GetField("playername", all);
                    pLocal = tPlayer.GetProperty("is_local", all);
                }
                if (tChat != null)
                {
                    fChatLog = tChat.GetField("CHAT_LOG", all);
                    mSend = tChat.GetMethod("SendChatMessage", all);
                    tMsg = tChat.GetNestedType("ChatMsgContainer", all);
                    if (tMsg != null) { fcName = tMsg.GetField("name", all); fcMsg = tMsg.GetField("msg", all); }
                }
                ready = tPlayer != null;
                if (ready) Plugin.Log?.LogInfo("Multiplayer mod detected (players: " + (fDict != null) + ", chat: " + (fChatLog != null && mSend != null) + ")");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("MP bridge init failed: " + e.Message); }
        }

        public static string LocalName()
        {
            try
            {
                if (!Present || fLocalPlayer == null || fName == null) return "";
                object lp = fLocalPlayer.GetValue(null);
                return lp != null ? (fName.GetValue(lp) as string ?? "") : "";
            }
            catch { return ""; }
        }

        /// <summary>All known players (including the local one).</summary>
        public static List<PlayerInfo> Players()
        {
            var list = new List<PlayerInfo>();
            try
            {
                if (!Present || fDict == null) return list;
                var d = fDict.GetValue(null) as IDictionary;
                if (d == null) return list;
                foreach (DictionaryEntry de in d)
                {
                    Body bd = de.Key as Body;
                    if (bd == null) continue;
                    object pl = de.Value;
                    string nm = (pl != null && fName != null) ? (fName.GetValue(pl) as string) : null;
                    bool loc = false;
                    try { if (pl != null && pLocal != null) loc = (bool)pLocal.GetValue(pl, null); } catch { }
                    list.Add(new PlayerInfo { Body = bd, Name = string.IsNullOrEmpty(nm) ? "player" : nm, IsLocal = loc });
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("MP.Players: " + e.Message); }
            return list;
        }

        public static bool ChatAvailable { get { return Present && fChatLog != null && fcName != null && fcMsg != null; } }

        /// <summary>Chat messages that appeared since the last call (the first call only marks the existing history as seen).</summary>
        public static List<ChatLine> PollNew()
        {
            var res = new List<ChatLine>();
            try
            {
                if (!ChatAvailable) return res;
                object q = fChatLog.GetValue(null);
                if (q == null) return res;
                MethodInfo m = q.GetType().GetMethod("JustGiveTheQueue");
                if (m == null) return res;
                IEnumerable en = m.Invoke(q, null) as IEnumerable;
                if (en == null) return res;
                var current = new List<object>();
                foreach (object o in en) current.Add(o);
                string local = LocalName();
                foreach (object o in current)
                {
                    if (seen.Contains(o)) continue;
                    seen.Add(o);
                    if (firstPoll) continue;
                    string nm = fcName.GetValue(o) as string ?? "?";
                    string tx = Senser.Strip(fcMsg.GetValue(o) as string ?? "");
                    if (tx.Length == 0) continue;
                    res.Add(new ChatLine { Name = Senser.Strip(nm), Text = tx, Server = nm.StartsWith("*"), FromLocal = local.Length > 0 && nm == local });
                }
                firstPoll = false;
                seen.RemoveWhere(o => !current.Contains(o));
            }
            catch (Exception e) { Plugin.Log?.LogWarning("MP.PollNew: " + e.Message); }
            return res;
        }

        public static bool Send(string text)
        {
            try
            {
                if (!Present || mSend == null || string.IsNullOrWhiteSpace(text)) return false;
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 150) text = text.Substring(0, 150);
                mSend.Invoke(null, new object[] { text, false });
                return true;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("MP.Send: " + e.Message); return false; }
        }
    }
}
