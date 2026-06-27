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
        /// When true, GetActiveContractsLimit returns an effectively unlimited
        /// cap so AP can offer/accept as many contracts as the seed needs.
        /// Set by CareerHackManager from the `career` slot_data block. Static
        /// because CareerLimitsManager creates a fresh override instance on
        /// each install, and the directive must survive re-installs.
        /// </summary>
        public static bool UnlimitedContracts = false;

        /// <summary>
        /// Absolute launch-pad craft-mass cap (tonnes) from the Progressive
        /// Launch Pad system: the server-provided cap thresholds indexed by the
        /// count of collected "Progressive Launch Pad" items
        /// (KSPArchipelagoMod.CurrentLaunchPadMassCap). When &gt;= 0 it REPLACES
        /// the stock VAB/pad mass limit, so KSP's own editor readout, the stock
        /// pre-launch check, and KK's MaxCraftMass (Materialiser.ComputeMaxCraftMass
        /// reads through this getter) all reflect the AP cap. &lt; 0 means no
        /// override (option disabled or top tier reached) → stock × factor.
        ///
        /// This is the "write the limit into the building" half: the cap is
        /// pushed in here, not read back out of the facility level (which the
        /// career hack maxes). Static so it survives CareerLimitsManager
        /// re-installs (like UnlimitedContracts).
        /// </summary>
        public static float LaunchPadMassCapTonnes = -1f;

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
        {
            // Launch pad (VAB) craft-mass cap is driven by the Progressive
            // Launch Pad item count, not the facility level. When set, it is an
            // absolute override of the stock limit. Runway/SPH (isVAB == false)
            // is unaffected — the progressive cap gates the pad only.
            if (isVAB && LaunchPadMassCapTonnes >= 0f)
                return LaunchPadMassCapTonnes;
            return base.GetCraftMassLimit(level, isVAB) * F("GetCraftMassLimit");
        }

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

        public override int GetActiveContractsLimit(float mCtrlNormLevel)
            => UnlimitedContracts ? 1000 : base.GetActiveContractsLimit(mCtrlNormLevel);
    }
}
