using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// A once-ever set of received-item indices: "this copy has already been
    /// used up and must never count again". Two instances exist —
    /// <see cref="FiredTraps"/> (a trap that already struck) and
    /// <see cref="SpentCharges"/> (a consumable buff the player already
    /// spent). Both key off the SAME index space (the position in
    /// AllItemsReceived), they just answer different questions about it.
    /// </summary>
    /// <remarks>
    /// The AUTHORITATIVE copy of each set lives in the AP server's Scope.Slot
    /// DataStorage under <see cref="ServerKey"/> (appended by
    /// KSPArchipelagoMod.PushStoreIndices, merged in via MergeServer on
    /// connect) — it survives save reverts, client reinstalls, and the mod
    /// deploy wiping PluginData. Each instance also keeps a local ConfigNode
    /// file per (save folder, multiworld seed, slot) under
    /// GameData/KSPArchipelago/PluginData as a secondary cache: it covers a
    /// use whose server write was lost to a disconnect. IsLoaded (and so any
    /// consumption at all) requires BOTH the file load and the server merge.
    /// A missing or corrupt file is treated as empty — worst case an item is
    /// consumed one extra time, never a crash.
    ///
    /// Surviving reverts is the whole point and it cuts both ways: a suffered
    /// trap stays suffered, and a spent charge stays spent. Reloading to
    /// before the spend does not refund it.
    ///
    /// The file name, the ConfigNode value key and the DataStorage key are all
    /// per-instance so the trap record keeps reading and writing exactly the
    /// files and keys it always did.
    /// </remarks>
    public sealed class ReceivedIndexStore
    {
        /// <summary>Traps that have already fired. Never re-fire.</summary>
        public static readonly ReceivedIndexStore FiredTraps =
            new ReceivedIndexStore("FiredTraps", "traps", "firedTraps");

        /// <summary>Consumable-buff copies the player has already spent.
        /// Never re-grant.</summary>
        public static readonly ReceivedIndexStore SpentCharges =
            new ReceivedIndexStore("SpentCharges", "charges", "spentCharges");

        /// <summary>Every store, for the connect / drain loops that have to
        /// treat them uniformly.</summary>
        public static readonly ReceivedIndexStore[] All = { FiredTraps, SpentCharges };

        /// <summary>Scope.Slot DataStorage key holding the authoritative set.</summary>
        public string ServerKey { get; private set; }

        private readonly string _filePrefix;
        private readonly string _valueKey;

        private HashSet<int> _indices;
        private string _path;
        private bool _serverSynced;

        // Written by the DataStorage GetAsync callback (websocket thread),
        // drained on the main thread by TakePendingServerIndices().
        private volatile int[] _pendingServer;

        private ReceivedIndexStore(string serverKey, string filePrefix, string valueKey)
        {
            ServerKey = serverKey;
            _filePrefix = filePrefix;
            _valueKey = valueKey;
        }

        public bool IsLoaded => _indices != null && _serverSynced;

        public void Load(string saveFolder, string seed, string slot)
        {
            _serverSynced = false;
            _pendingServer = null;   // a prior session's fetch must not land here
            string dir = Path.Combine(KSPUtil.ApplicationRootPath,
                "GameData/KSPArchipelago/PluginData");
            string file = Sanitize($"{_filePrefix}-{saveFolder}-{seed}-{slot}") + ".cfg";
            _path = Path.Combine(dir, file);
            _indices = new HashSet<int>();
            try
            {
                if (File.Exists(_path))
                {
                    ConfigNode node = ConfigNode.Load(_path);
                    string raw = node?.GetValue(_valueKey);
                    if (!string.IsNullOrEmpty(raw))
                        foreach (string s in raw.Split(','))
                            if (int.TryParse(s.Trim(), out int idx))
                                _indices.Add(idx);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[KSP-AP] {ServerKey} store load failed ({_path}): {ex.Message} — starting empty");
            }
            Debug.Log($"[KSP-AP] {ServerKey} store: {_indices.Count} indexes on record ({file})");
        }

        public bool Contains(int itemIndex)
            => _indices != null && _indices.Contains(itemIndex);

        /// <summary>Publish the server-side set (worker thread).</summary>
        public void PublishServerIndices(int[] indices) => _pendingServer = indices;

        /// <summary>Take the published server-side set, or null if none is
        /// waiting. Main thread only.</summary>
        public int[] TakePendingServerIndices()
        {
            int[] pending = _pendingServer;
            if (pending != null) _pendingServer = null;
            return pending;
        }

        /// <summary>Union the server-side set in (main thread, after Load).
        /// Flushes the union back to the local file so the cache heals itself
        /// after a PluginData wipe. Returns the indexes the LOCAL file had
        /// that the server lacks — the caller pushes them up so the server
        /// copy heals too (covers uses recorded before the server store
        /// existed, or whose write died with a disconnect).</summary>
        public int[] MergeServer(IEnumerable<int> serverIndices)
        {
            if (_indices == null) return new int[0];   // no Load yet — stale callback
            var server = new HashSet<int>(serverIndices);
            int[] localOnly = _indices.Where(i => !server.Contains(i)).ToArray();
            int before = _indices.Count;
            foreach (int idx in server)
                _indices.Add(idx);
            if (_indices.Count > before) Flush();
            _serverSynced = true;
            Debug.Log($"[KSP-AP] {ServerKey} store: server merge, {_indices.Count} indexes total"
                + (localOnly.Length > 0 ? $", backfilling {localOnly.Length} to server" : ""));
            return localOnly;
        }

        public void Record(int itemIndex)
        {
            if (_indices == null || !_indices.Add(itemIndex)) return;
            Flush();
        }

        private void Flush()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var node = new ConfigNode();
                node.AddValue(_valueKey,
                    string.Join(",", _indices.Select(i => i.ToString()).ToArray()));
                node.Save(_path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] {ServerKey} store flush failed ({_path}): {ex.Message}");
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
