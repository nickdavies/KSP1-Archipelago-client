using System.Collections.Generic;

namespace KSPArchipelago.Buffs.Applicators
{
    /// <summary>
    /// Power Generation: more ElectricCharge out of solar panels, RTGs,
    /// alternators and fuel cells.
    /// </summary>
    /// <remarks>
    /// There are TWO distinct output paths in stock and both need covering —
    /// "one pass over resHandler covers everything" is false:
    ///
    /// 1. <c>PartModule.resHandler.outputResources</c> (resHandler L413208,
    ///    outputResources L450445, ModuleResource.rate L450095) — this is how
    ///    solar panels (L424042), ModuleGenerator/RTGs (L424771) and engine
    ///    alternators (L419129) all emit, via UpdateModuleResourceOutputs.
    /// 2. <c>BaseConverter.outputList</c> (L469749), a List&lt;ResourceRatio&gt;
    ///    with a <c>Ratio</c> field (L487368). Stock fuel cells are
    ///    ModuleResourceConverter : BaseConverter and never touch resHandler,
    ///    so path 1 misses them entirely.
    ///
    /// ModuleDeployableSolarPanel.chargeRate (L423237) is NOT the runtime
    /// driver — it is read at exactly one site, OnLoad (L423593), and only
    /// when outputResources is empty, where it seeds path 1. It is scaled
    /// anyway so the two stay consistent if OnLoad ever re-runs, and so the
    /// VAB tooltip reads true.
    ///
    /// ElectricCharge is massless in stock, so unlike a fuel or ablator buff
    /// this adds no mass anywhere and has no downside. It is worth most in the
    /// outer system, where solar output has already fallen off with distance.
    ///
    /// Only ElectricCharge is touched: scaling every output would buff ISRU
    /// ore-to-fuel conversion, which is a different (and much larger) balance
    /// question than "your panels work better".
    /// </remarks>
    public class PowerApplicator : IBuffApplicator
    {
        private const string EC = "ElectricCharge";

        public string Id { get { return "power"; } }

        // Stock rates, keyed "<part>:<module index>:<entry index>". Entry index
        // is the position within that module's output list, so a module with
        // several outputs keeps them distinct.
        private readonly Dictionary<string, double> _rates =
            new Dictionary<string, double>();
        private readonly Dictionary<string, float> _chargeRates =
            new Dictionary<string, float>();

        public void Reset()
        {
            _rates.Clear();
            _chargeRates.Clear();
        }

        public void ApplyToPart(Part part, string partName, BuffTotals totals)
        {
            if (part == null || part.Modules == null) return;
            double mult = 1.0 + totals.Get(BuffType.Power);

            for (int m = 0; m < part.Modules.Count; m++)
            {
                PartModule module = part.Modules[m];
                if (module == null) continue;

                // Path 1: solar panels, RTGs, alternators.
                if (module.resHandler != null && module.resHandler.outputResources != null)
                {
                    List<ModuleResource> outputs = module.resHandler.outputResources;
                    for (int r = 0; r < outputs.Count; r++)
                    {
                        ModuleResource res = outputs[r];
                        if (res == null || res.name != EC) continue;
                        string key = partName + ":" + m + ":" + r;
                        double stock;
                        if (!_rates.TryGetValue(key, out stock))
                        {
                            stock = res.rate;
                            _rates[key] = stock;
                        }
                        res.rate = stock * mult;
                    }
                }

                // Path 2: fuel cells (ModuleResourceConverter : BaseConverter).
                BaseConverter converter = module as BaseConverter;
                if (converter != null && converter.outputList != null)
                {
                    for (int r = 0; r < converter.outputList.Count; r++)
                    {
                        ResourceRatio ratio = converter.outputList[r];
                        if (ratio.ResourceName != EC) continue;
                        string key = partName + ":conv:" + m + ":" + r;
                        double stock;
                        if (!_rates.TryGetValue(key, out stock))
                        {
                            stock = ratio.Ratio;
                            _rates[key] = stock;
                        }
                        // ResourceRatio is a struct — mutate a copy and write
                        // it back, or the assignment is lost.
                        ratio.Ratio = stock * mult;
                        converter.outputList[r] = ratio;
                    }
                }

                // Keep the OnLoad seed consistent with path 1.
                ModuleDeployableSolarPanel panel = module as ModuleDeployableSolarPanel;
                if (panel != null && panel.resourceName == EC)
                {
                    string key = partName + ":panel:" + m;
                    float stock;
                    if (!_chargeRates.TryGetValue(key, out stock))
                    {
                        stock = panel.chargeRate;
                        _chargeRates[key] = stock;
                    }
                    panel.chargeRate = (float)(stock * mult);
                }
            }
        }
    }
}
