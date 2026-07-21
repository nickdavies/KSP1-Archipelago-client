using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KSPArchipelago.Traps
{
    /// <summary>
    /// Fired-trap record (fire-once-ever). The AUTHORITATIVE copy lives in
    /// the AP server's Scope.Slot DataStorage ("FiredTraps", pushed by
    /// KSPArchipelagoMod.PushFiredTrap and merged in via MergeServer on
    /// connect) — it survives save reverts, client reinstalls, and the mod
    /// deploy wiping PluginData. This class also keeps a local ConfigNode
    /// file per (save folder, multiworld seed, slot) under
    /// GameData/KSPArchipelago/PluginData as a secondary cache: it covers a
    /// fire whose server write was lost to a disconnect. IsLoaded (and so
    /// any trap firing) requires BOTH the file load and the server merge.
    /// A missing or corrupt file is treated as empty — worst case a trap
    /// fires one extra time, never a crash.
    /// </summary>
    public static class FiredTrapStore
    {
        private static HashSet<int> _fired;
        private static string _path;
        private static bool _serverSynced;

        public static bool IsLoaded => _fired != null && _serverSynced;

        public static void Load(string saveFolder, string seed, string slot)
        {
            _serverSynced = false;
            string dir = Path.Combine(KSPUtil.ApplicationRootPath,
                "GameData/KSPArchipelago/PluginData");
            string file = Sanitize($"traps-{saveFolder}-{seed}-{slot}") + ".cfg";
            _path = Path.Combine(dir, file);
            _fired = new HashSet<int>();
            try
            {
                if (File.Exists(_path))
                {
                    ConfigNode node = ConfigNode.Load(_path);
                    string raw = node?.GetValue("firedTraps");
                    if (!string.IsNullOrEmpty(raw))
                        foreach (string s in raw.Split(','))
                            if (int.TryParse(s.Trim(), out int idx))
                                _fired.Add(idx);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[KSP-AP] FiredTrapStore load failed ({_path}): {ex.Message} — starting empty");
            }
            Debug.Log($"[KSP-AP] FiredTrapStore: {_fired.Count} fired traps on record ({file})");
        }

        public static bool Contains(int itemIndex)
            => _fired != null && _fired.Contains(itemIndex);

        /// <summary>Union the server-side fired set in (main thread, after
        /// Load). Flushes the union back to the local file so the cache
        /// heals itself after a PluginData wipe. Returns the indexes the
        /// LOCAL file had that the server lacks — the caller pushes them up
        /// so the server copy heals too (covers fires recorded before the
        /// server store existed, or whose write died with a disconnect).</summary>
        public static int[] MergeServer(IEnumerable<int> serverIndices)
        {
            if (_fired == null) return new int[0];   // no Load yet — stale callback
            var server = new HashSet<int>(serverIndices);
            int[] localOnly = _fired.Where(i => !server.Contains(i)).ToArray();
            int before = _fired.Count;
            foreach (int idx in server)
                _fired.Add(idx);
            if (_fired.Count > before) Flush();
            _serverSynced = true;
            Debug.Log($"[KSP-AP] FiredTrapStore: server merge, {_fired.Count} fired traps total"
                + (localOnly.Length > 0 ? $", backfilling {localOnly.Length} to server" : ""));
            return localOnly;
        }

        public static void Record(int itemIndex)
        {
            if (_fired == null || !_fired.Add(itemIndex)) return;
            Flush();
        }

        private static void Flush()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var node = new ConfigNode();
                node.AddValue("firedTraps",
                    string.Join(",", _fired.Select(i => i.ToString()).ToArray()));
                node.Save(_path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] FiredTrapStore flush failed ({_path}): {ex.Message}");
            }
        }

        private static string Sanitize(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ' ')
                    chars[i] = '_';
            return new string(chars);
        }
    }
}
