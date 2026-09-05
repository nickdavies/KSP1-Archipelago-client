using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// Regenerates the cached VAB tooltip text for prefabs we have buffed.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS: the part tooltip is two different things stitched
    /// together, and only one of them is live.
    ///
    ///  * The stats block (mass, impact tolerance, max temp, cost) is read
    ///    from the prefab's fields every time the tooltip is drawn, so buffs
    ///    to Part.crashTolerance / maxTemp show up immediately.
    ///  * The per-module block (gimbal range, generator output, engine Isp
    ///    curves, ...) is a STRING baked once at load. PartLoader builds it at
    ///    L224581-224629: for each module it takes GetModuleTitle()/GetInfo()
    ///    off IModuleInfo (or PrintModuleName/drawStats otherwise) and stores
    ///    the result in AvailablePart.moduleInfos[i].info (L231242).
    ///
    /// Nothing ever regenerates that string, so a runtime field write is
    /// invisible there — the tooltip keeps reporting "Gimbal Range: 4.0°" or
    /// an RTG's stock output forever, even though flight behaviour has
    /// genuinely changed. That is the same failure the whole design avoided by
    /// writing real engine fields instead of multIsp/multFlow: a buff the
    /// player cannot see is a buff they cannot plan around.
    ///
    /// Only module types the applicators actually touch are refreshed —
    /// GetInfo() builds and localizes strings, and doing it for every module
    /// on all ~737 prefabs would be needless work on every apply.
    /// </remarks>
    public static class TooltipRefresher
    {
        /// <summary>Regenerate cached module info for one buffed prefab.</summary>
        public static void Refresh(AvailablePart availablePart, Part prefab)
        {
            if (availablePart == null || prefab == null) return;
            if (availablePart.moduleInfos == null || prefab.Modules == null) return;

            // Which cached entries have we already rewritten? Guards against a
            // part carrying two modules with the same display title.
            var claimed = new HashSet<int>();

            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module == null || !Touches(module)) continue;

                string title;
                string info;
                try
                {
                    IModuleInfo asInfo = module as IModuleInfo;
                    if (asInfo != null)
                    {
                        title = asInfo.GetModuleTitle();
                        info = asInfo.GetInfo();
                    }
                    else
                    {
                        title = KSPUtil.PrintModuleName(module.ClassName);
                        info = module.GetInfo();
                    }
                }
                catch (Exception e)
                {
                    // A module that throws building its own description is not
                    // worth failing the buff over — the field write already
                    // landed, only the label is stale.
                    Debug.LogWarning($"[KSP-AP] Buffs: GetInfo() threw for "
                                     + $"{availablePart.name}/{module.ClassName}: {e.Message}");
                    continue;
                }

                if (string.IsNullOrEmpty(info)) continue;
                info = info.Trim();

                for (int i = 0; i < availablePart.moduleInfos.Count; i++)
                {
                    if (claimed.Contains(i)) continue;
                    if (availablePart.moduleInfos[i].moduleName != title) continue;
                    availablePart.moduleInfos[i].info = info;
                    claimed.Add(i);
                    break;
                }
            }
        }

        /// <summary>
        /// True for module types some applicator writes to. Keep in step with
        /// the applicators — a type added there and missed here still works,
        /// it just shows a stale tooltip.
        /// </summary>
        private static bool Touches(PartModule module)
        {
            return module is ModuleEngines            // Isp / thrust
                || module is ModuleGimbal             // control
                || module is ModuleReactionWheel      // control
                || module is ModuleDeployableSolarPanel  // power
                || module is ModuleGenerator          // power (RTG)
                || module is BaseConverter;           // power (fuel cell)
        }
    }
}
