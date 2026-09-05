using System.Collections.Generic;

namespace KSPArchipelago.Buffs.Applicators
{
    /// <summary>
    /// Heat Tolerance and Structural Integrity — both are plain fields on
    /// Part itself, so one pass covers them.
    /// </summary>
    /// <remarks>
    /// Fields (Assembly-CSharp line refs):
    ///   maxTemp        L320293  internal failure temperature
    ///   skinMaxTemp    L320321  skin failure temperature
    ///   crashTolerance L320246  impact speed a part survives (stock default 9 m/s)
    ///   breakingForce  L320242  joint strength
    ///   breakingTorque L320244  joint strength
    ///   gTolerance     L320248  sustained-g failure threshold
    ///   maxPressure    L320295  crush pressure, kPa (stock default 4000)
    ///
    /// skinMaxTemp defaults to -1.0, which is a SENTINEL meaning "derive from
    /// maxTemp", not a temperature. Scaling it would turn -1 into -1.05 and
    /// still read as the sentinel — harmless today, but it would silently
    /// become a real (negative, instantly-failing) temperature if stock ever
    /// changed the test to `&lt; 0`. Left alone when negative.
    ///
    /// Both buffs are purely protective: they can only raise a failure
    /// threshold, never lower one, so neither can make a situation harder.
    /// </remarks>
    public class PartStatApplicator : IBuffApplicator
    {
        public string Id { get { return "part-stats"; } }

        private struct PartStock
        {
            public double MaxTemp;
            public double SkinMaxTemp;
            public float CrashTolerance;
            public float BreakingForce;
            public float BreakingTorque;
            public double GTolerance;
            public double MaxPressure;
        }

        private readonly Dictionary<string, PartStock> _stock =
            new Dictionary<string, PartStock>();

        public void Reset() { _stock.Clear(); }

        public void ApplyToPart(Part part, string partName, BuffTotals totals)
        {
            if (part == null) return;

            PartStock stock;
            if (!_stock.TryGetValue(partName, out stock))
            {
                stock = new PartStock
                {
                    MaxTemp = part.maxTemp,
                    SkinMaxTemp = part.skinMaxTemp,
                    CrashTolerance = part.crashTolerance,
                    BreakingForce = part.breakingForce,
                    BreakingTorque = part.breakingTorque,
                    GTolerance = part.gTolerance,
                    MaxPressure = part.maxPressure,
                };
                _stock[partName] = stock;
            }

            double heat = 1.0 + totals.Get(BuffType.HeatTolerance);
            float structural = totals.Mult(BuffType.Structural);

            part.maxTemp = stock.MaxTemp * heat;
            // Negative == "derive from maxTemp" sentinel; leave it be.
            if (stock.SkinMaxTemp > 0.0)
                part.skinMaxTemp = stock.SkinMaxTemp * heat;

            part.crashTolerance = stock.CrashTolerance * structural;
            part.breakingForce = stock.BreakingForce * structural;
            part.breakingTorque = stock.BreakingTorque * structural;
            part.gTolerance = stock.GTolerance * structural;
            part.maxPressure = stock.MaxPressure * structural;
        }
    }
}
