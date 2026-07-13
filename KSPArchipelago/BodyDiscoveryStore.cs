using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Persists fly-there (non-item) body reveals in the AP server's per-slot
    /// DataStorage (Scope.Slot), so they survive a KSP restart / fresh save — the
    /// server keeps them in its own save file. Item reveals already persist via AP
    /// received-items; this covers only bodies the player revealed by flying into a
    /// hidden body's SOI, which are not otherwise recoverable.
    /// </summary>
    public static class BodyDiscoveryStore
    {
        private const string Key = "ksp_ap_revealed_bodies";

        /// <summary>
        /// Read the persisted fly-there reveal set WITHOUT blocking. Kicks off an
        /// async server retrieve; <paramref name="onLoaded"/> is invoked (on a
        /// thread-pool thread) with the result — an empty list on any failure or an
        /// unset key. The callback must marshal back to the main thread itself.
        /// </summary>
        public static void LoadAsync(ArchipelagoSession session, Action<List<string>> onLoaded)
        {
            if (session == null) { onLoaded(new List<string>()); return; }
            try
            {
                session.DataStorage[Scope.Slot, Key].GetAsync().ContinueWith(task =>
                {
                    var result = new List<string>();
                    try
                    {
                        if (task.Result is JArray arr)
                            foreach (var e in arr) result.Add((string)e);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[KSP-AP] BodyDiscoveryStore parse failed: {e.Message}");
                    }
                    onLoaded(result);
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KSP-AP] BodyDiscoveryStore.LoadAsync failed: {e.Message}");
                onLoaded(new List<string>());
            }
        }

        /// <summary>
        /// Overwrite the persisted set with the full current fly-there reveal set.
        /// The indexer setter enqueues a Set packet, so this is non-blocking and
        /// safe to call from the main thread.
        /// </summary>
        public static void Save(ArchipelagoSession session, IEnumerable<string> bodyNames)
        {
            if (session == null) return;
            try
            {
                session.DataStorage[Scope.Slot, Key] = JToken.FromObject(new List<string>(bodyNames));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KSP-AP] BodyDiscoveryStore.Save failed: {e.Message}");
            }
        }
    }
}
