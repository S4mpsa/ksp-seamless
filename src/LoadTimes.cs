using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Seamless
{
    /// <summary>
    /// Load times measured by past transitions, per kind of transition (setting
    /// <c>dynamicTiming</c>), kept in GameData/Seamless/PluginData/loadtimes.cfg. Two numbers per
    /// load: how long the preload took to read the new scene (the part that runs behind the camera
    /// move) and how long the card then stayed up after the switch (the part no camera move can
    /// hide: the old scene is gone by then). The last few of each are kept; the expected value is
    /// the largest of them, so a quick load once does not take the card away from a slow one.
    /// </summary>
    internal static class LoadTimes
    {
        private const int Keep = 3;
        private static Dictionary<string, Entry> data;

        private sealed class Entry
        {
            public readonly List<float> Read = new List<float>();
            public readonly List<float> After = new List<float>();
        }

        private static string FilePath =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData/Seamless/PluginData/loadtimes.cfg");

        /// <summary>The expected read and after-the-switch times, or false with no record yet.</summary>
        internal static bool Expected(string key, out float read, out float after)
        {
            read = after = 0f;
            Entry e;
            if (!Data().TryGetValue(key, out e) || e.Read.Count == 0 || e.After.Count == 0) return false;
            read = e.Read.Max();
            after = e.After.Max();
            return true;
        }

        internal static string Describe(string key)
        {
            Entry e;
            if (!Data().TryGetValue(key, out e)) return "none";
            return "read " + Join(e.Read) + " s, after the switch " + Join(e.After) + " s";
        }

        internal static void Record(string key, float read, float after)
        {
            Entry e;
            if (!Data().TryGetValue(key, out e)) data[key] = e = new Entry();
            Push(e.Read, Math.Max(0f, read));
            Push(e.After, Math.Max(0f, after));
            Save();
        }

        private static void Push(List<float> l, float v)
        {
            l.Add(v);
            while (l.Count > Keep) l.RemoveAt(0);
        }

        private static string Join(List<float> l)
        {
            return string.Join(", ", l.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)).ToArray());
        }

        private static Dictionary<string, Entry> Data()
        {
            if (data != null) return data;
            data = new Dictionary<string, Entry>();
            try
            {
                if (!File.Exists(FilePath)) return data;
                ConfigNode root = ConfigNode.Load(FilePath);
                ConfigNode top = root != null ? root.GetNode("LOADTIMES") : null;
                if (top == null) return data;
                foreach (ConfigNode n in top.GetNodes("TRANSITION"))
                {
                    string name = n.GetValue("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var e = new Entry();
                    Parse(n.GetValue("read"), e.Read);
                    Parse(n.GetValue("afterSwitch"), e.After);
                    data[name] = e;
                }
            }
            catch (Exception ex) { Log.Warn("loadtimes.cfg unreadable, starting over: " + ex.Message); }
            return data;
        }

        private static void Parse(string s, List<float> into)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (string p in s.Split(','))
                if (float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= 0f && v < 3600f)
                    Push(into, v);
        }

        private static void Save()
        {
            try
            {
                var top = new ConfigNode("LOADTIMES");
                foreach (KeyValuePair<string, Entry> kv in data.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    ConfigNode n = top.AddNode("TRANSITION");
                    n.AddValue("name", kv.Key);
                    n.AddValue("read", Join(kv.Value.Read));
                    n.AddValue("afterSwitch", Join(kv.Value.After));
                }
                var root = new ConfigNode();
                root.AddNode(top);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                root.Save(FilePath);
            }
            catch (Exception ex) { Log.Warn("loadtimes.cfg not saved: " + ex.Message); }
        }
    }
}
