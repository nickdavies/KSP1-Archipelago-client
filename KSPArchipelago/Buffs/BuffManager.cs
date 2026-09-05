using System;
using System.Collections.Generic;
using UnityEngine;

using KSPArchipelago.Buffs.Applicators;

namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// Owns the permanent-buff subsystem: how many of each buff the player
    /// holds, and pushing the resulting totals into part prefabs and live
    /// vessels.
    /// </summary>
    /// <remarks>
    /// PERSISTENCE: none, deliberately. A buff level is exactly the count of
    /// that item in the server's AllItemsReceived, so it is derived state, not
    /// saved state — the same model as _progressiveCounts / Progressive Launch
    /// Pad. That makes it idempotent on reconnect and correct across save
    /// reverts (a permanent buff SHOULD survive a revert; the item was really
    /// received). Counts are zeroed by KSPArchipelagoMod.ResetProgressiveState
    /// before ProcessAllItems re-walks the item list, which is what stops the
    /// double-count bug documented there.
    ///
    /// PREFABS ARE PROCESS-GLOBAL. Mutating a prefab is not save state and does
    /// not travel with the save file, so it leaks across save switches unless
    /// it is undone — hence Restore() on disconnect, mirroring
    /// ScienceScaling.Reset().
    ///
    /// UNKNOWN NAMES: a newer server can send a buff this build has never heard
    /// of. Those are ignored (with one log line) rather than crashing or
    /// fizzling, matching TrapManager's KnownTrapNames-vs-Registry split.
    /// </remarks>
    public static class BuffManager
    {
        private static readonly List<IBuffApplicator> _applicators =
            new List<IBuffApplicator>
        {
            new EngineApplicator(),
            new PartStatApplicator(),
            new ControlApplicator(),
            new PowerApplicator(),
        };

        /// <summary>Copies held, per buff item name.</summary>
        private static readonly Dictionary<string, int> _counts =
            new Dictionary<string, int>();

        private static bool _applied;

        /// <summary>Set when counts change; drained on the main thread.</summary>
        public static bool NeedsApply { get; private set; }

        public static bool IsBuffItem(string itemName)
        {
            return itemName != null && BuffDefs.ByName.ContainsKey(itemName);
        }

        /// <summary>
        /// Record one received copy. Safe to call from GiveItem's EDITOR replay
        /// path: this only increments a counter that ResetProgressiveState
        /// zeroes before any full re-walk, and the apply is absolute rather
        /// than incremental, so a replay cannot compound.
        /// </summary>
        public static void NoteReceived(string itemName)
        {
            if (!IsBuffItem(itemName))
            {
                if (itemName != null && itemName.StartsWith("Buff: "))
                    Debug.Log($"[KSP-AP] Buffs: unknown buff '{itemName}' ignored — update the client mod");
                return;
            }
            int c;
            _counts.TryGetValue(itemName, out c);
            _counts[itemName] = c + 1;
            NeedsApply = true;
        }

        /// <summary>Zero all counts. Called from ResetProgressiveState.</summary>
        public static void ResetCounts()
        {
            _counts.Clear();
            NeedsApply = true;
        }

        /// <summary>Current per-type totals as fractions (0.14f == +14%).</summary>
        public static BuffTotals Totals()
        {
            var fractions = new Dictionary<BuffType, float>();
            foreach (var kvp in _counts)
            {
                KeyValuePair<BuffType, float> def;
                if (!BuffDefs.ByName.TryGetValue(kvp.Key, out def)) continue;
                float existing;
                fractions.TryGetValue(def.Key, out existing);
                // Additive by design: three +1% copies is +3%, not +3.03%.
                fractions[def.Key] = existing + def.Value * kvp.Value;
            }
            return new BuffTotals(fractions);
        }

        /// <summary>Per-type totals for the UI readout, in percent.</summary>
        public static List<KeyValuePair<string, float>> DisplayTotals()
        {
            BuffTotals totals = Totals();
            var rows = new List<KeyValuePair<string, float>>();
            foreach (BuffType type in Enum.GetValues(typeof(BuffType)))
            {
                float pct = totals.Get(type) * 100f;
                if (pct != 0f)
                    rows.Add(new KeyValuePair<string, float>(BuffDefs.DisplayName(type), pct));
            }
            return rows;
        }

        /// <summary>
        /// Push current totals into every part prefab, then into every loaded
        /// vessel. Main thread only. Idempotent — every write is absolute.
        /// </summary>
        public static void Apply()
        {
            NeedsApply = false;
            if (PartLoader.LoadedPartsList == null) return;

            BuffTotals totals = Totals();
            int prefabs = 0;
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                if (ap == null || ap.partPrefab == null) continue;
                ApplyToPart(ap.partPrefab, ap.name, totals);
                // Cached VAB tooltip strings don't track field writes — see
                // TooltipRefresher. Without this the player sees stock gimbal
                // range and generator output forever.
                TooltipRefresher.Refresh(ap, ap.partPrefab);
                prefabs++;
            }

            int liveParts = 0;
            if (FlightGlobals.Vessels != null)
            {
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v == null || !v.loaded || v.parts == null) continue;
                    liveParts += ApplyToVessel(v, totals);
                }
            }

            _applied = true;
            Debug.Log($"[KSP-AP] Buffs: applied to {prefabs} prefabs, {liveParts} live parts "
                      + $"({DescribeTotals()})");
        }

        /// <summary>
        /// Re-apply to one vessel. Called on onVesselGoOffRails: a vessel that
        /// was packed when Apply() last ran has parts carrying stock values.
        /// </summary>
        public static int ApplyToVessel(Vessel vessel, BuffTotals totals)
        {
            if (vessel == null || vessel.parts == null) return 0;
            int n = 0;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.partInfo == null) continue;
                ApplyToPart(p, p.partInfo.name, totals);
                n++;
            }
            return n;
        }

        public static void OnVesselGoOffRails(Vessel vessel)
        {
            if (!_applied) return;
            ApplyToVessel(vessel, Totals());
        }

        private static void ApplyToPart(Part part, string partName, BuffTotals totals)
        {
            for (int i = 0; i < _applicators.Count; i++)
            {
                try
                {
                    _applicators[i].ApplyToPart(part, partName, totals);
                }
                catch (Exception e)
                {
                    // One bad part must not take down the whole sweep.
                    Debug.LogError($"[KSP-AP] Buffs: applicator '{_applicators[i].Id}' "
                                   + $"threw on '{partName}': {e}");
                }
            }
        }

        /// <summary>
        /// Restore stock values everywhere and drop all state. Called on
        /// disconnect — prefab mutation is process-global and would otherwise
        /// bleed into the next save the player opens.
        /// </summary>
        public static void Restore()
        {
            if (_applied)
            {
                // An empty count set makes every applicator write stock * 1.0,
                // which IS the restore — no separate restore path to keep in
                // sync with the apply path.
                _counts.Clear();
                Apply();
                Debug.Log("[KSP-AP] Buffs: restored stock values");
            }
            _counts.Clear();
            for (int i = 0; i < _applicators.Count; i++)
                _applicators[i].Reset();
            _applied = false;
            NeedsApply = false;
        }

        private static string DescribeTotals()
        {
            var rows = DisplayTotals();
            if (rows.Count == 0) return "no buffs held";
            var parts = new List<string>();
            foreach (var row in rows)
                parts.Add($"{row.Key} +{row.Value:0.##}%");
            return string.Join(", ", parts.ToArray());
        }
    }
}
