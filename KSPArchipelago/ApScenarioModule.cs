using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Persists AP-related state in the KSP .sfs save file so it reverts
    /// correctly on save/load. Tracks cumulative science awarded and which
    /// item indices have been processed (to avoid duplicate awards on
    /// reconnect and correctly re-award on save revert).
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class ApScenarioModule : ScenarioModule
    {
        public static ApScenarioModule Instance { get; private set; }

        public float TotalApScienceAwarded;
        public HashSet<int> AwardedItemIndices = new HashSet<int>();
        public HashSet<string> PendingLocationNames = new HashSet<string>();

        public override void OnAwake()
        {
            Instance = this;
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("apScience", TotalApScienceAwarded);
            node.AddValue("awardedItems", string.Join(",", AwardedItemIndices));
            if (PendingLocationNames.Count > 0)
                node.AddValue("pendingLocations", string.Join("|", PendingLocationNames));

            // Career-mode facility AP-granted levels. Per-facility key=value
            // pairs joined by '|' so a save with no Career-mode state stays
            // compatible (the field is omitted if empty).
            if (CareerUpgradesManager.Instance != null)
            {
                var snap = CareerUpgradesManager.Instance.SnapshotBaselineLevels();
                var pairs = new List<string>(snap.Count);
                foreach (var kv in snap)
                    if (kv.Value > 0)
                        pairs.Add($"{kv.Key}={kv.Value}");
                if (pairs.Count > 0)
                    node.AddValue("apGrantedLevels", string.Join("|", pairs.ToArray()));
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            float.TryParse(node.GetValue("apScience"), out TotalApScienceAwarded);
            AwardedItemIndices.Clear();
            string raw = node.GetValue("awardedItems");
            if (!string.IsNullOrEmpty(raw))
                foreach (string s in raw.Split(','))
                    if (int.TryParse(s.Trim(), out int idx))
                        AwardedItemIndices.Add(idx);

            // Merge save-file names into the existing in-memory set rather than
            // replacing it. Replacing would lose locations checked while offline
            // if the player reverts a flight before reconnecting — the in-memory
            // names would vanish and never be sent. Merging means a revert only
            // adds back save-file names; anything queued since the last save is kept.
            string pendingRaw = node.GetValue("pendingLocations");
            if (!string.IsNullOrEmpty(pendingRaw))
                foreach (string s in pendingRaw.Split('|'))
                    if (!string.IsNullOrEmpty(s))
                        PendingLocationNames.Add(s);

            // Career-mode facility AP-granted levels. Hydrate
            // CareerUpgradesManager state and apply SetLevel on the live
            // facility instances. CareerUpgradesManager.SetApGrantedLevel
            // both updates the tracker and calls SetLevel on the matching
            // UpgradeableFacility, so the in-game state matches the save.
            string apGrantedRaw = node.GetValue("apGrantedLevels");
            if (!string.IsNullOrEmpty(apGrantedRaw)
                && CareerUpgradesManager.Instance != null)
            {
                foreach (string pair in apGrantedRaw.Split('|'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string facilityId = pair.Substring(0, eq);
                    if (!int.TryParse(pair.Substring(eq + 1), out int level)) continue;
                    CareerUpgradesManager.Instance.SetApGrantedLevel(facilityId, level);
                }
                // Restored AP-granted levels are authoritative — open the
                // POST-revert gate so it enforces them even if the player
                // doesn't reconnect this session (no career-directive apply).
                CareerUpgradesManager.Instance.MarkBaselineApplied();
            }
        }
    }
}
