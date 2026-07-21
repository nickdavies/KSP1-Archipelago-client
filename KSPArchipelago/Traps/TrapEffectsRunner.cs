using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Traps
{
    /// <summary>
    /// A live timed trap effect. Tick runs at physics rate and returns false
    /// when the effect is finished — implementations must self-validate their
    /// vessel every tick (null / unloaded / packed / no longer active) and do
    /// their own cleanup before returning false. Abort is the external
    /// termination path (scene change, disconnect) and must restore anything
    /// the effect changed.
    /// </summary>
    public interface ITrapEffect
    {
        bool Tick(float fixedDeltaTime);
        void Abort();
    }

    /// <summary>
    /// Ticks live trap effects in FixedUpdate (physics-rate work: forces,
    /// resource drains, heat flux). Added lazily to the mod's persistent
    /// GameObject by TrapManager. A throwing effect is logged and dropped —
    /// never allowed to wedge FixedUpdate.
    /// </summary>
    public class TrapEffectsRunner : MonoBehaviour
    {
        private readonly List<ITrapEffect> _effects = new List<ITrapEffect>();

        /// <summary>True while any effect runs — blocks the next trap fire.</summary>
        public bool HasActive => _effects.Count > 0;

        public void Add(ITrapEffect effect) => _effects.Add(effect);

        private void FixedUpdate()
        {
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                bool alive;
                try
                {
                    alive = _effects[i].Tick(Time.fixedDeltaTime);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KSP-AP] Trap effect threw, dropping: {ex}");
                    alive = false;
                }
                if (!alive) _effects.RemoveAt(i);
            }
        }

        public void AbortAll()
        {
            for (int i = 0; i < _effects.Count; i++)
            {
                try { _effects[i].Abort(); }
                catch (Exception ex)
                {
                    Debug.LogError($"[KSP-AP] Trap effect abort threw: {ex}");
                }
            }
            _effects.Clear();
        }
    }
}
