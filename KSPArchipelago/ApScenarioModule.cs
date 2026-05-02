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
        }
    }
}
