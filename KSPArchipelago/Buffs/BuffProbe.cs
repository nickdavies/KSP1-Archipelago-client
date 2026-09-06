using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// Logs the real post-apply field values of a few well-known stock parts,
    /// one line each, whenever buffs are active.
    /// </summary>
    /// <remarks>
    /// The "applied to N prefabs (Engine Efficiency +10%)" line proves what the
    /// mod INTENDED. It does not prove a field on a part actually moved — a
    /// dead write (scaling a value nothing reads, as ModuleGimbal.gimbalRange
    /// turned out to be) produces an identical log line. This probe closes that
    /// gap so a buff can be verified from the log alone instead of by reading
    /// tooltips in the VAB.
    ///
    /// The sample is chosen to hit every applicator, including the two output
    /// paths that are easy to get wrong:
    ///   nuclearEngine     Isp + thrust + heat + structural, and has NO gimbal
    ///   Shrimp            throttleLocked: must take Isp but NOT thrust
    ///   liquidEngine3_v2  gimbal (the four directional fields, not gimbalRange)
    ///   advSasModule      reaction wheel torque
    ///   rtg               power via resHandler.outputResources
    ///   FuelCell          power via BaseConverter.outputList — a separate path
    ///
    /// Silent when no buff is held, so an unbuffed run logs nothing extra.
    /// </remarks>
    public static class BuffProbe
    {
        private static readonly string[] Parts =
        {
            "nuclearEngine", "Shrimp", "liquidEngine3_v2",
            "advSasModule", "rtg", "FuelCell",
        };

        /// <summary>
        /// Log one line per probed part. BuffManager only calls this when the
        /// held totals actually moved, so the log shows transitions rather than
        /// the same six lines on every scene change.
        /// </summary>
        public static void LogSample()
        {
            for (int i = 0; i < Parts.Length; i++)
            {
                try
                {
                    // PartLoader registers part names with underscores turned
                    // into dots (it does name.Replace('_', '.') at load), so a
                    // raw cfg name like "liquidEngine3_v2" never resolves.
                    AvailablePart ap =
                        PartLoader.getPartInfoByName(Parts[i].Replace('_', '.'));
                    if (ap == null || ap.partPrefab == null)
                    {
                        Debug.Log($"[KSP-AP] BuffProbe {Parts[i]} ABSENT (part not installed)");
                        continue;
                    }
                    Debug.Log($"[KSP-AP] BuffProbe {Parts[i]} {Describe(ap.partPrefab)}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KSP-AP] BuffProbe {Parts[i]} failed: {e.Message}");
                }
            }
        }

        private static string Describe(Part part)
        {
            var sb = new StringBuilder();
            sb.Append($"crashTol={part.crashTolerance:0.###} maxTemp={part.maxTemp:0.#}");

            if (part.Modules == null) return sb.ToString();
            for (int m = 0; m < part.Modules.Count; m++)
            {
                PartModule module = part.Modules[m];
                if (module == null) continue;

                ModuleEngines engine = module as ModuleEngines;
                if (engine != null)
                {
                    float isp = engine.atmosphereCurve != null
                        ? engine.atmosphereCurve.Evaluate(0f) : float.NaN;
                    sb.Append($" vacIsp={isp:0.##} maxThrust={engine.maxThrust:0.##}")
                      .Append($" maxFuelFlow={engine.maxFuelFlow:0.#####}")
                      .Append($" throttleLocked={engine.throttleLocked}");
                    continue;
                }

                ModuleGimbal gimbal = module as ModuleGimbal;
                if (gimbal != null)
                {
                    sb.Append($" gimbalRange={gimbal.gimbalRange:0.###}")
                      .Append($" gimbalXP={gimbal.gimbalRangeXP:0.###}");
                    continue;
                }

                ModuleReactionWheel wheel = module as ModuleReactionWheel;
                if (wheel != null)
                {
                    sb.Append($" pitchTorque={wheel.PitchTorque:0.###}");
                    continue;
                }

                // Power path 1: solar panels, RTGs, alternators.
                if (module.resHandler != null && module.resHandler.outputResources != null)
                {
                    List<ModuleResource> outputs = module.resHandler.outputResources;
                    for (int r = 0; r < outputs.Count; r++)
                        if (outputs[r] != null && outputs[r].name == "ElectricCharge")
                            sb.Append($" ecRate={outputs[r].rate:0.####}");
                }

                // Power path 2: fuel cells and other converters.
                BaseConverter converter = module as BaseConverter;
                if (converter != null && converter.outputList != null)
                {
                    for (int r = 0; r < converter.outputList.Count; r++)
                        if (converter.outputList[r].ResourceName == "ElectricCharge")
                            sb.Append($" ecRatio={converter.outputList[r].Ratio:0.####}");
                }
            }
            return sb.ToString();
        }
    }
}
