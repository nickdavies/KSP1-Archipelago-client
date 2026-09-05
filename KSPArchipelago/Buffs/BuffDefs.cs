using System.Collections.Generic;

namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// The buff categories the server can send. Mirrors BuffType in
    /// worlds/ksp1/buffs.py — the AP item NAME is the entire wire signal,
    /// so this enum plus <see cref="BuffDefs.ByName"/> is the whole protocol.
    /// </summary>
    public enum BuffType
    {
        Isp,
        Thrust,
        HeatTolerance,
        Structural,
        Control,
        Power,
    }

    /// <summary>
    /// Resolved per-type buff totals, as fractions (0.14f == +14%).
    /// Passed to every applicator so a single applicator can consume more
    /// than one type — <see cref="Applicators.EngineApplicator"/> must,
    /// because Isp and Thrust both write maxFuelFlow.
    /// </summary>
    public struct BuffTotals
    {
        private readonly Dictionary<BuffType, float> _fractions;

        public BuffTotals(Dictionary<BuffType, float> fractions)
        {
            _fractions = fractions;
        }

        /// <summary>Fraction for a type; 0f when none held.</summary>
        public float Get(BuffType type)
        {
            if (_fractions == null) return 0f;
            float v;
            return _fractions.TryGetValue(type, out v) ? v : 0f;
        }

        /// <summary>Multiplier for a type; 1f when none held.</summary>
        public float Mult(BuffType type)
        {
            return 1f + Get(type);
        }

        public bool Any
        {
            get
            {
                if (_fractions == null) return false;
                foreach (var kvp in _fractions)
                    if (kvp.Value != 0f) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// The 18 buff item names and what each is worth.
    /// </summary>
    /// <remarks>
    /// This roster is duplicated in three places that MUST stay in sync:
    /// this table, the BuffTypes option docstring in worlds/ksp1/options.py,
    /// and the Buffs section of docs/en_Kerbal Space Program 1.md.
    ///
    /// Tiers stack ADDITIVELY: three "I" copies is +3%, not +3.03%. Names and
    /// magnitudes are frozen at release alongside the server-side ids
    /// (worlds/ksp1/buffs.py, offsets 12000-12999).
    /// </remarks>
    public static class BuffDefs
    {
        private const float Small = 0.01f;   // "I"
        private const float Medium = 0.03f;  // "II"
        private const float Large = 0.05f;   // "III"

        // Structural runs a deliberately steeper ladder than everything else.
        // The default 1/3/5% is near-worthless here: the fields it scales start
        // small in absolute terms, so a top-tier copy moved an LV-N's impact
        // tolerance 12.0 -> 12.6 m/s, which is not worth a check.
        //
        // Percentage (rather than a flat "+N m/s") is deliberate too, because
        // crashTolerance is only one of the five fields in this buff —
        // breakingForce, breakingTorque, gTolerance and maxPressure are all
        // different units, and a flat bonus would make one item grant a
        // mixed-unit effect. Steepening the percentage keeps all five coherent.
        //
        // Known trade-off, accepted: percentage favours parts that already
        // survive a lot, so a landing leg gains more absolute m/s than the weak
        // part that actually fails first.
        private const float StructuralSmall = 0.05f;
        private const float StructuralMedium = 0.15f;
        private const float StructuralLarge = 0.25f;

        /// <summary>Item name -> (category, fraction a single copy grants).</summary>
        public static readonly Dictionary<string, KeyValuePair<BuffType, float>> ByName =
            new Dictionary<string, KeyValuePair<BuffType, float>>
        {
            { "Buff: Engine Efficiency I",      Def(BuffType.Isp, Small) },
            { "Buff: Engine Efficiency II",     Def(BuffType.Isp, Medium) },
            { "Buff: Engine Efficiency III",    Def(BuffType.Isp, Large) },
            { "Buff: Engine Thrust I",          Def(BuffType.Thrust, Small) },
            { "Buff: Engine Thrust II",         Def(BuffType.Thrust, Medium) },
            { "Buff: Engine Thrust III",        Def(BuffType.Thrust, Large) },
            { "Buff: Heat Tolerance I",         Def(BuffType.HeatTolerance, Small) },
            { "Buff: Heat Tolerance II",        Def(BuffType.HeatTolerance, Medium) },
            { "Buff: Heat Tolerance III",       Def(BuffType.HeatTolerance, Large) },
            { "Buff: Structural Integrity I",   Def(BuffType.Structural, StructuralSmall) },
            { "Buff: Structural Integrity II",  Def(BuffType.Structural, StructuralMedium) },
            { "Buff: Structural Integrity III", Def(BuffType.Structural, StructuralLarge) },
            { "Buff: Control Authority I",      Def(BuffType.Control, Small) },
            { "Buff: Control Authority II",     Def(BuffType.Control, Medium) },
            { "Buff: Control Authority III",    Def(BuffType.Control, Large) },
            { "Buff: Power Generation I",       Def(BuffType.Power, Small) },
            { "Buff: Power Generation II",      Def(BuffType.Power, Medium) },
            { "Buff: Power Generation III",     Def(BuffType.Power, Large) },
        };

        private static KeyValuePair<BuffType, float> Def(BuffType t, float f)
        {
            return new KeyValuePair<BuffType, float>(t, f);
        }

        /// <summary>Player-facing label for the buff totals readout.</summary>
        public static string DisplayName(BuffType type)
        {
            switch (type)
            {
                case BuffType.Isp:           return "Engine Efficiency";
                case BuffType.Thrust:        return "Engine Thrust";
                case BuffType.HeatTolerance: return "Heat Tolerance";
                case BuffType.Structural:    return "Structural Integrity";
                case BuffType.Control:       return "Control Authority";
                case BuffType.Power:         return "Power Generation";
                default:                     return type.ToString();
            }
        }
    }
}
