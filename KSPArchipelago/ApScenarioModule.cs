using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Persists AP-related state in the KSP .sfs save file so it reverts
    /// correctly on save/load. Currently tracks cumulative science awarded
    /// by Archipelago to prevent duplicate awards on reconnect.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class ApScenarioModule : ScenarioModule
    {
        public static ApScenarioModule Instance { get; private set; }

        public float TotalApScienceAwarded;

        public override void OnAwake()
        {
            Instance = this;
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("apScience", TotalApScienceAwarded);
        }

        public override void OnLoad(ConfigNode node)
        {
            float.TryParse(node.GetValue("apScience"), out TotalApScienceAwarded);
        }
    }
}
