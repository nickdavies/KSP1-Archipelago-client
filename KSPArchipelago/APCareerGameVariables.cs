using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Per-getter scaling override for KSP's facility limits. Subclasses
    /// GameVariables and overrides the virtual getters identified during
    /// the Career-mode spike (`notes/career_spike.md`, H2.body-scaled-limits).
    ///
    /// The override is empirically validated end-to-end: VAB editor UI,
    /// part-count and mass limits, launch pad dimension checks, and
    /// info-panel readouts all dispatch through the virtual getter and
    /// honour our scaling.
    ///
    /// Install by assigning `GameVariables.Instance = subclass`. Restore
    /// the original instance on uninstall — see CareerLimitsManager.
    ///
    /// Defaults to 1.0 (no scaling). Per-body factors are slot_data-driven
    /// in production; for the initial career-branch wiring we hard-code
    /// test factors so we can verify the override is live in-game.
    /// </summary>
    public class APCareerGameVariables : GameVariables
    {
        /// <summary>
        /// Per-getter multipliers. Keyed by the getter method name
        /// (e.g. "GetCraftMassLimit"). Missing keys default to 1.0.
        /// </summary>
        public Dictionary<string, float> bodyFactors = new Dictionary<string, float>();

        private float F(string method)
        {
            float v;
            return bodyFactors.TryGetValue(method, out v) ? v : 1.0f;
        }

        public override float GetCraftMassLimit(float level, bool isVAB)
            => base.GetCraftMassLimit(level, isVAB) * F("GetCraftMassLimit");

        public override int GetPartCountLimit(float level, bool isVAB)
        {
            int stock = base.GetPartCountLimit(level, isVAB);
            // Stock returns int.MaxValue for "unlimited" — don't multiply.
            if (stock == int.MaxValue) return int.MaxValue;
            float scaled = stock * F("GetPartCountLimit");
            if (scaled >= int.MaxValue) return int.MaxValue;
            return (int)scaled;
        }

        public override Vector3 GetCraftSizeLimit(float level, bool isVAB)
            => base.GetCraftSizeLimit(level, isVAB) * F("GetCraftSizeLimit");

        public override double GetDSNRange(float level)
            => base.GetDSNRange(level) * F("GetDSNRange");
    }
}
