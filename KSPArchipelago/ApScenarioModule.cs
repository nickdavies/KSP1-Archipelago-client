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

        public override void OnAwake()
        {
            Instance = this;
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("apScience", TotalApScienceAwarded);
            node.AddValue("awardedItems", string.Join(",", AwardedItemIndices));
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
        }
    }
}
