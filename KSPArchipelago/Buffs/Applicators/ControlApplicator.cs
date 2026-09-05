using System.Collections.Generic;

namespace KSPArchipelago.Buffs.Applicators
{
    /// <summary>
    /// Control Authority: reaction wheel torque and engine gimbal range.
    /// </summary>
    /// <remarks>
    /// ModuleReactionWheel.PitchTorque/YawTorque/RollTorque are read directly
    /// by the torque provider, so scaling them is enough.
    ///
    /// ModuleGimbal is a trap. <c>gimbalRange</c> (L371230) looks like the
    /// knob but is DEAD at runtime: it is read in exactly one place,
    /// ModuleGimbal.OnLoad (L371894-371947), where it seeds the four
    /// directional fields gimbalRangeXP/YP/XN/YN (L371233-371242, each
    /// defaulting to the -1 "copy from gimbalRange" sentinel). The actual
    /// deflection math reads ONLY the directional fields (L371701, L371705,
    /// L371721, L371725). OnLoad has already run by the time we connect, so
    /// scaling gimbalRange alone would be a silent no-op. All five are scaled.
    ///
    /// A directional field that is still negative is an unresolved sentinel,
    /// not an angle — left alone, exactly as skinMaxTemp is in
    /// PartStatApplicator. If OnLoad runs later it will derive it from the
    /// gimbalRange we did scale, so the buff lands either way.
    /// </remarks>
    public class ControlApplicator : IBuffApplicator
    {
        public string Id { get { return "control"; } }

        private struct WheelStock
        {
            public float Pitch;
            public float Yaw;
            public float Roll;
        }

        private struct GimbalStock
        {
            public float Range;
            public float XP;
            public float YP;
            public float XN;
            public float YN;
        }

        private readonly Dictionary<string, WheelStock> _wheels =
            new Dictionary<string, WheelStock>();
        private readonly Dictionary<string, GimbalStock> _gimbals =
            new Dictionary<string, GimbalStock>();

        public void Reset()
        {
            _wheels.Clear();
            _gimbals.Clear();
        }

        public void ApplyToPart(Part part, string partName, BuffTotals totals)
        {
            if (part == null || part.Modules == null) return;
            float mult = totals.Mult(BuffType.Control);

            int wheelIndex = -1;
            int gimbalIndex = -1;
            for (int m = 0; m < part.Modules.Count; m++)
            {
                ModuleReactionWheel wheel = part.Modules[m] as ModuleReactionWheel;
                if (wheel != null)
                {
                    wheelIndex++;
                    string key = partName + ":" + wheelIndex;
                    WheelStock stock;
                    if (!_wheels.TryGetValue(key, out stock))
                    {
                        stock = new WheelStock
                        {
                            Pitch = wheel.PitchTorque,
                            Yaw = wheel.YawTorque,
                            Roll = wheel.RollTorque,
                        };
                        _wheels[key] = stock;
                    }
                    wheel.PitchTorque = stock.Pitch * mult;
                    wheel.YawTorque = stock.Yaw * mult;
                    wheel.RollTorque = stock.Roll * mult;
                    continue;
                }

                ModuleGimbal gimbal = part.Modules[m] as ModuleGimbal;
                if (gimbal == null) continue;
                gimbalIndex++;
                string gkey = partName + ":" + gimbalIndex;
                GimbalStock g;
                if (!_gimbals.TryGetValue(gkey, out g))
                {
                    g = new GimbalStock
                    {
                        Range = gimbal.gimbalRange,
                        XP = gimbal.gimbalRangeXP,
                        YP = gimbal.gimbalRangeYP,
                        XN = gimbal.gimbalRangeXN,
                        YN = gimbal.gimbalRangeYN,
                    };
                    _gimbals[gkey] = g;
                }
                gimbal.gimbalRange = g.Range * mult;
                if (g.XP >= 0f) gimbal.gimbalRangeXP = g.XP * mult;
                if (g.YP >= 0f) gimbal.gimbalRangeYP = g.YP * mult;
                if (g.XN >= 0f) gimbal.gimbalRangeXN = g.XN * mult;
                if (g.YN >= 0f) gimbal.gimbalRangeYN = g.YN * mult;
            }
        }
    }
}
