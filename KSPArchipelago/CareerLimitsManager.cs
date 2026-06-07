using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Installs APCareerGameVariables as GameVariables.Instance when the
    /// current save is Career mode; restores the original instance otherwise.
    ///
    /// Lifecycle:
    ///   - Loads as Instantly+DontDestroyOnLoad like the main mod.
    ///   - Subscribes to onGameStateLoad + onGameSceneLoadRequested.
    ///   - On each transition: if Career → install (idempotent), otherwise
    ///     → restore (idempotent).
    ///
    /// Test factors are hard-coded for now (see CareerProbe validation —
    /// 0.5x mass / 0.5x parts confirmed enforced at editor + pad). Once the
    /// AP-side generation ships, the values come from slot_data via a
    /// `SetBodyFactors(Dictionary)` call from KSPArchipelagoMod.HandleConnect.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class CareerLimitsManager : MonoBehaviour
    {
        public static CareerLimitsManager Instance { get; private set; }

        private GameVariables _originalGameVariables;
        private APCareerGameVariables _override;
        private GameObject _overrideHost;
        private bool _installed;

        // Hard-coded test factors. Replaced by slot_data when generation
        // side ships. 1.0 = stock; <1.0 tighter limits; >1.0 more generous.
        // Verified with these values in CareerProbe — VAB editor display +
        // pre-launch enforcement at Kerbin both react correctly.
        private static readonly Dictionary<string, float> _testFactors = new Dictionary<string, float>
        {
            { "GetCraftMassLimit",   1.0f },
            { "GetPartCountLimit",   1.0f },
            { "GetCraftSizeLimit",   1.0f },
            { "GetDSNRange",         1.0f },
        };

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoad);
        }

        void OnDestroy()
        {
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoad);
            Uninstall();
            if (Instance == this) Instance = null;
        }

        private void OnGameStateLoad(ConfigNode _) => Sync();
        private void OnSceneLoad(GameScenes _) => Sync();

        private void Sync()
        {
            bool careerMode = HighLogic.CurrentGame != null
                              && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            if (careerMode) Install();
            else Uninstall();
        }

        /// <summary>
        /// Replace public bodyFactors. Called from KSPArchipelagoMod once
        /// slot_data has been parsed. Re-applies factors on the live
        /// override instance if installed.
        /// </summary>
        public void SetBodyFactors(IDictionary<string, float> factors)
        {
            if (factors == null) return;
            // Update the testFactors snapshot so subsequent installs use
            // the latest values, AND apply to the live instance if any.
            foreach (var kv in factors) _testFactors[kv.Key] = kv.Value;
            if (_override != null)
            {
                _override.bodyFactors.Clear();
                foreach (var kv in _testFactors) _override.bodyFactors[kv.Key] = kv.Value;
                Debug.Log($"[KSP-AP] Career limits factors updated: {Stringify(_testFactors)}");
            }
        }

        private void Install()
        {
            if (_installed) return;
            try
            {
                _originalGameVariables = GameVariables.Instance;
                _overrideHost = new GameObject("APCareerGameVariablesHost");
                DontDestroyOnLoad(_overrideHost);
                _override = _overrideHost.AddComponent<APCareerGameVariables>();

                // APCareerGameVariables overrides only a few getters but inherits
                // every GameVariables DATA field — and a freshly AddComponent'd
                // instance has them all null. Reputation math, for one, evaluates
                // GameVariables.Instance.reputationAddition/Subtraction (curves);
                // a null curve NREs in ModifyReputationDelta on every rep change
                // (ours and KSP's own milestone awards). Copy all base fields from
                // the original so the override is a faithful stand-in.
                if (_originalGameVariables != null)
                {
                    foreach (var f in typeof(GameVariables).GetFields(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance))
                    {
                        try { f.SetValue(_override, f.GetValue(_originalGameVariables)); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[KSP-AP] GameVariables field copy "
                                           + $"'{f.Name}' failed: {ex.Message}");
                        }
                    }
                }

                foreach (var kv in _testFactors) _override.bodyFactors[kv.Key] = kv.Value;
                GameVariables.Instance = _override;
                _installed = true;
                Debug.Log($"[KSP-AP] APCareerGameVariables installed (factors: {Stringify(_testFactors)})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] APCareerGameVariables install failed: {ex}");
                _installed = false;
            }
        }

        private void Uninstall()
        {
            if (!_installed) return;
            try
            {
                GameVariables.Instance = _originalGameVariables;
                if (_overrideHost != null) Destroy(_overrideHost);
                _overrideHost = null;
                _override = null;
                _originalGameVariables = null;
                _installed = false;
                Debug.Log("[KSP-AP] APCareerGameVariables uninstalled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] APCareerGameVariables uninstall failed: {ex}");
            }
        }

        private static string Stringify(Dictionary<string, float> d)
        {
            var parts = new List<string>(d.Count);
            foreach (var kv in d) parts.Add($"{kv.Key}={kv.Value}");
            return string.Join(", ", parts.ToArray());
        }
    }
}
