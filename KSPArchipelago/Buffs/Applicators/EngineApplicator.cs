using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Buffs.Applicators
{
    /// <summary>
    /// Engine Efficiency (Isp) and Engine Thrust, in ONE applicator.
    /// </summary>
    /// <remarks>
    /// These two cannot be separate applicators. Stock computes thrust as
    /// <c>requestedMassFlow * realIsp * g * multIsp</c> where
    /// <c>requestedMassFlow = maxFuelFlow * throttle * flowMultiplier</c>
    /// (Assembly-CSharp ~L368988-369017), so BOTH buffs have to write
    /// maxFuelFlow. Two applicators each writing absolute-from-its-own-snapshot
    /// would be last-writer-wins, and holding both buffs would silently turn
    /// the Isp buff into a thrust buff. One applicator, both percentages.
    ///
    /// Math, with isp fraction i and thrust fraction t:
    ///     atmosphereCurve = stock * (1+i)
    ///     maxFuelFlow     = stock * (1+t)/(1+i)
    /// giving thrust = flow*isp*g = stock_thrust * (1+t)   -- thrust scales by t alone
    ///        deltaV  = isp*g*ln(R) = stock_dv * (1+i)     -- dv scales by i alone
    ///        burn time                        *= (1+i)/(1+t)
    ///
    /// We deliberately write the REAL fields rather than the ready-made
    /// [KSPField] mod hooks multIsp/multFlow (L365864/365866): the stock delta-v
    /// app ignores both of those, reading atmosphereCurve and maxFuelFlow
    /// directly (ispVac = engine.atmosphereCurve.Evaluate(t) L624467;
    /// GetEngineThrust = Lerp(minFuelFlow, maxFuelFlow, throttle) * isp * g
    /// L370542). Using the mults would give the player an invisible buff they
    /// could not plan a craft around.
    ///
    /// maxThrust/minThrust are display-and-derivation only at runtime (thrust
    /// comes from fuel flow), but they seed maxFuelFlow in OnLoad
    /// (L366749) and show in the VAB part tooltip, so they are kept consistent.
    ///
    /// That seeding is self-consistent with what we write, which is why it is
    /// safe to scale all four. OnLoad only re-derives when its ConfigNode
    /// actually carries maxThrust — true for the part.cfg at game load (long
    /// before we connect), false for craft/save nodes, since none of these
    /// fields are persistent. And if it ever did re-run over a buffed prefab it
    /// would compute maxThrust*(1+t) / (isp*(1+i) * g) = stock_flow*(1+t)/(1+i),
    /// which is exactly the value we set. Re-derivation is a no-op, not a fight.
    ///
    /// SRBs (throttleLocked) are excluded from the THRUST half by operator
    /// decision: they cannot be throttled down, so extra thrust makes an ascent
    /// harder to fly, not easier. They still receive the Isp half, which for a
    /// solid means it burns longer at the same thrust — pure upside.
    /// </remarks>
    public class EngineApplicator : IBuffApplicator
    {
        public string Id { get { return "engines"; } }

        private struct EngineStock
        {
            public float MaxThrust;
            public float MinThrust;
            public float MaxFuelFlow;
            public float MinFuelFlow;
            public Keyframe[] IspKeys;   // atmosphereCurve, stock
        }

        // Keyed by "<AvailablePart.name>:<index of this ModuleEngines in the
        // part>". Keying on the part NAME (not the live instance) is what makes
        // this immune to the open question of whether Unity's Instantiate deep-
        // copies or shares a prefab's FloatCurve reference: prefab and instance
        // resolve to the same stock record, and every write is absolute
        // (stock * factor) with no read-modify-write, so applying twice — or
        // applying to a shared object through two paths — converges to the same
        // state instead of compounding.
        private readonly Dictionary<string, EngineStock> _stock =
            new Dictionary<string, EngineStock>();

        public void Reset() { _stock.Clear(); }

        public void ApplyToPart(Part part, string partName, BuffTotals totals)
        {
            if (part == null || part.Modules == null) return;

            float i = totals.Get(BuffType.Isp);
            float t = totals.Get(BuffType.Thrust);

            int index = -1;
            for (int m = 0; m < part.Modules.Count; m++)
            {
                // ModuleEnginesFX derives from ModuleEngines, so this covers both.
                ModuleEngines engine = part.Modules[m] as ModuleEngines;
                if (engine == null) continue;
                index++;

                string key = partName + ":" + index;
                EngineStock stock;
                if (!_stock.TryGetValue(key, out stock))
                {
                    if (engine.atmosphereCurve == null || engine.atmosphereCurve.Curve == null)
                        continue;
                    stock = new EngineStock
                    {
                        MaxThrust = engine.maxThrust,
                        MinThrust = engine.minThrust,
                        MaxFuelFlow = engine.maxFuelFlow,
                        MinFuelFlow = engine.minFuelFlow,
                        IspKeys = engine.atmosphereCurve.Curve.keys,
                    };
                    _stock[key] = stock;
                }

                // Solids keep stock thrust; they still get the Isp half.
                float tEff = engine.throttleLocked ? 0f : t;
                float ispMult = 1f + i;
                float thrustMult = 1f + tEff;
                float flowMult = thrustMult / ispMult;

                engine.maxThrust = stock.MaxThrust * thrustMult;
                engine.minThrust = stock.MinThrust * thrustMult;
                engine.maxFuelFlow = stock.MaxFuelFlow * flowMult;
                engine.minFuelFlow = stock.MinFuelFlow * flowMult;
                engine.atmosphereCurve = ScaleCurve(stock.IspKeys, ispMult);
            }
        }

        /// <summary>
        /// Rebuild a FloatCurve with every value scaled. FloatCurve has no
        /// scale operation, so this reconstructs from the stock keyframes.
        /// Tangents scale by the same factor: d(k*v)/dt == k * dv/dt.
        /// </summary>
        private static FloatCurve ScaleCurve(Keyframe[] stockKeys, float mult)
        {
            FloatCurve curve = new FloatCurve();
            for (int k = 0; k < stockKeys.Length; k++)
            {
                Keyframe f = stockKeys[k];
                curve.Add(f.time, f.value * mult, f.inTangent * mult, f.outTangent * mult);
            }
            return curve;
        }
    }
}
