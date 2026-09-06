using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// Tops up the active vessel's propellant tanks — the effect behind the
    /// "Buff: Mid-Air Refuel" consumable.
    /// </summary>
    /// <remarks>
    /// EXCLUSIONS, and why each one:
    ///
    ///  * Ore — ISRU feedstock. Refilling it makes ISRU a free infinite fuel
    ///    source, which is a far bigger capability change than "top up my
    ///    tanks" and would trivialise every mining mission.
    ///  * ElectricCharge — not a tank. Generation is what the Power buff is
    ///    for, and a full battery is a much weaker reward than free propellant.
    ///  * Ablator — a heat shield reset is a different feature with a different
    ///    risk profile (it rescues a re-entry rather than extending a burn).
    ///  * SolidFuel — stock SRBs cannot be refuelled or even transferred; the
    ///    resource is NO_FLOW by design. Filling one is non-physical in a way
    ///    the other propellants are not.
    ///  * Any resource on a part hosting a BaseConverter (ModuleResourceConverter
    ///    and friends, decompile L477338 / outputList L469749) — that is the
    ///    literal "not ISRU tanks" requirement. A converter's buffers are its
    ///    working stock, not the craft's propellant load.
    ///  * IntakeAir / IntakeAtm — DECISION: excluded. These are transient
    ///    intake buffers refilled by atmospheric flow every physics tick, not
    ///    storage; "filling" one is meaningless and would read as a no-op.
    ///  * EVA Propellant — DECISION: excluded. It lives on kerbals, not the
    ///    vessel, and jetpack fuel already refills on boarding. Including it
    ///    would make the charge appear to do something on an EVA-only
    ///    "vessel", which is a confusing edge case for no gain.
    ///
    /// Everything else that has a maxAmount and is below it gets topped up:
    /// LiquidFuel, Oxidizer, MonoPropellant, XenonGas, and any modded
    /// propellant that behaves like them.
    /// </remarks>
    public static class RefuelAction
    {
        private static readonly HashSet<string> Excluded = new HashSet<string>
        {
            "Ore",
            "ElectricCharge",
            "Ablator",
            "SolidFuel",
            "IntakeAir",
            "IntakeAtm",
            "EVA Propellant",
        };

        /// <summary>
        /// Fill every eligible tank on <paramref name="vessel"/>.
        /// Returns true only if something actually changed — the caller uses
        /// that to decide whether to consume the charge.
        /// </summary>
        public static bool Run(Vessel vessel, out string summary)
        {
            summary = "no vessel";
            // Same guard the trap effects use: a packed or non-active vessel
            // has no live part state to write.
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.parts == null)
                return false;

            int partsTouched = 0;
            int tanksTouched = 0;
            double unitsAdded = 0.0;
            bool sawEligibleTank = false;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (part == null || part.Resources == null) continue;

                // Skip ISRU hosts wholesale — their buffers are working stock.
                if (part.FindModuleImplementing<BaseConverter>() != null) continue;

                bool touchedThisPart = false;
                for (int r = 0; r < part.Resources.Count; r++)
                {
                    PartResource res = part.Resources[r];
                    if (res == null || res.info == null) continue;
                    if (Excluded.Contains(res.resourceName)) continue;
                    if (res.maxAmount <= 0.0) continue;

                    sawEligibleTank = true;
                    double missing = res.maxAmount - res.amount;
                    if (missing <= 1e-6) continue;   // already full

                    res.amount = res.maxAmount;
                    unitsAdded += missing;
                    tanksTouched++;
                    touchedThisPart = true;
                }
                if (touchedThisPart) partsTouched++;
            }

            if (tanksTouched == 0)
            {
                summary = sawEligibleTank ? "tanks already full" : "no refuelable tanks";
                return false;
            }

            summary = $"{tanksTouched} tank{(tanksTouched == 1 ? "" : "s")} on "
                      + $"{partsTouched} part{(partsTouched == 1 ? "" : "s")}, "
                      + $"{unitsAdded:0.#} units";
            Debug.Log($"[KSP-AP] Refuelled {vessel.vesselName}: {summary}");
            return true;
        }
    }
}
