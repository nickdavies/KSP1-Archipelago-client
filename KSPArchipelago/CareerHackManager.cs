using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Server→client career directives parsed from slot_data["career"].
    /// Always emitted by the generator; the client executes them verbatim.
    /// Each flag is honoured independently (piecemeal lowering is a future
    /// generator concern — the client just actuates whatever it's told).
    /// </summary>
    /// <summary>
    /// One entry of the server's <c>facility_item_map</c>: the facilities a
    /// "Progressive &lt;X&gt;" item drives, and the item-count thresholds at which
    /// the facility level steps up. Facility level = number of thresholds that are
    /// &lt;= the count of that item received (then floored at the directive baseline
    /// and clamped to MaxLevel by <see cref="CareerUpgradesManager"/>). Increment
    /// buildings use [1, 2]; the R&amp;D facility rides a non-linear schedule ([2, 5]).
    /// </summary>
    public sealed class FacilityItemSpec
    {
        public readonly List<string> Facilities;
        public readonly List<int> Thresholds;
        public FacilityItemSpec(List<string> facilities, List<int> thresholds)
        {
            Facilities = facilities ?? new List<string>();
            Thresholds = thresholds ?? new List<int>();
        }
    }

    public sealed class CareerDirectives
    {
        // facility id (e.g. "SpaceCenter/LaunchPad") → directive level (0..2).
        public readonly Dictionary<string, int> BuildingLevels
            = new Dictionary<string, int>(StringComparer.Ordinal);
        // Progressive item name → the facilities it drives + count thresholds.
        // Replaces the old hardcoded client item-name→facility map: the client
        // actuates whatever the generator sends, so adding a building is a
        // server-only change.
        public readonly Dictionary<string, FacilityItemSpec> FacilityItemMap
            = new Dictionary<string, FacilityItemSpec>(StringComparer.Ordinal);
        public bool InfiniteFunds;
        public bool InfiniteReputation;
        public bool UnlimitedContracts;

        public static CareerDirectives Parse(JObject obj)
        {
            if (obj == null) throw new FormatException("career block is null");
            var d = new CareerDirectives();
            if (obj["building_levels"] is JObject levels)
                foreach (var kv in levels)
                    d.BuildingLevels[kv.Key] = (int)kv.Value;
            if (obj["facility_item_map"] is JObject fmap)
                foreach (var kv in fmap)
                {
                    if (!(kv.Value is JObject spec)) continue;
                    var facilities = new List<string>();
                    if (spec["facilities"] is JArray fa)
                        foreach (var f in fa) facilities.Add((string)f);
                    var thresholds = new List<int>();
                    if (spec["thresholds"] is JArray ta)
                        foreach (var t in ta) thresholds.Add((int)t);
                    d.FacilityItemMap[kv.Key] = new FacilityItemSpec(facilities, thresholds);
                }
            d.InfiniteFunds      = (bool?)obj["infinite_funds"] ?? false;
            d.InfiniteReputation = (bool?)obj["infinite_reputation"] ?? false;
            d.UnlimitedContracts = (bool?)obj["unlimited_contracts"] ?? false;
            return d;
        }
    }

    /// <summary>
    /// Actuates the <see cref="CareerDirectives"/> so the career economy never
    /// gates progression — only AP does. It reuses the experimental career
    /// machinery rather than reinventing it:
    ///   - Building levels → <see cref="CareerUpgradesManager.SetApGrantedLevel"/>,
    ///     whose button-intercept + OnKSCFacilityUpgraded revert already pins
    ///     facilities at the granted level.
    ///   - Funds/Reputation → set to a large value and re-asserted whenever
    ///     they change (so spending never bites).
    ///   - Unlimited contracts → <see cref="APCareerGameVariables.UnlimitedContracts"/>,
    ///     read by the installed GameVariables override's GetActiveContractsLimit.
    ///
    /// Apply() must run on the main thread (it touches KSP singletons);
    /// KSPArchipelagoMod parses the directives on the connect worker thread and
    /// drains them into Apply() from Update().
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class CareerHackManager : MonoBehaviour
    {
        public static CareerHackManager Instance { get; private set; }

        private const double FundsTarget = 1_000_000_000.0; // 1e9; re-asserted on spend
        private const float ReputationTarget = 1000f;        // KSP rep caps at ~1000

        private CareerDirectives _directives;
        private bool _eventsHooked;

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelLoaded);
        }

        void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelLoaded);
            UnhookEconomyEvents();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Install directives and apply them now. Idempotent — re-applying the
        /// same directives is a no-op. Call on the main thread.
        /// </summary>
        public void Apply(CareerDirectives directives)
        {
            if (directives == null) return;
            _directives = directives;
            APCareerGameVariables.UnlimitedContracts = directives.UnlimitedContracts;
            HookEconomyEvents();
            ApplyAll();
        }

        /// <summary>
        /// Stop hacking the economy (on AP disconnect). Buildings/funds the
        /// player already has are left as-is; we just stop re-asserting.
        /// </summary>
        public void Reset()
        {
            _directives = null;
            APCareerGameVariables.UnlimitedContracts = false;
            UnhookEconomyEvents();
        }

        private void OnLevelLoaded(GameScenes scene)
        {
            // Funds/rep are reset and facilities re-instantiated on scene loads
            // and save reverts — re-assert so the hack survives both.
            if (_directives != null) ApplyAll();
        }

        private void ApplyAll()
        {
            ApplyBuildingLevels();
            AssertFunds();
            AssertReputation();
        }

        private void ApplyBuildingLevels()
        {
            var upgrades = CareerUpgradesManager.Instance;
            if (upgrades == null || _directives == null) return;
            // Push the item→facility map first so the item-receive path (which
            // may run in the same Update tick) can actuate against the baselines
            // we set next.
            upgrades.SetFacilityItemMap(_directives.FacilityItemMap);
            foreach (var kv in _directives.BuildingLevels)
                upgrades.SetApGrantedLevel(kv.Key, kv.Value);
            // The AP-granted cap is now authoritative — let the POST-revert
            // begin enforcing it (it was gated until this first apply so the
            // connect-time materialiser couldn't clamp facilities to 0).
            upgrades.MarkBaselineApplied();
        }

        private void AssertFunds()
        {
            if (_directives == null || !_directives.InfiniteFunds) return;
            var f = Funding.Instance;
            if (f == null) return;            // null in Sandbox / outside Career
            if (f.Funds < FundsTarget)
                f.SetFunds(FundsTarget, TransactionReasons.None);
        }

        // Reputation's private backing field, set directly to skip
        // AddReputation → ModifyReputationDelta → CurrencyModifierQuery, which
        // NREs here (the modifier/strategy machinery isn't ready when we apply).
        // Funding.SetFunds doesn't have this problem, so funds stay on SetFunds.
        private static readonly System.Reflection.FieldInfo _repField =
            typeof(Reputation).GetField("rep",
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);

        private void AssertReputation()
        {
            if (_directives == null || !_directives.InfiniteReputation) return;
            var r = Reputation.Instance;
            if (r == null || _repField == null) return;
            float current = Reputation.CurrentRep; // CurrentRep is a static accessor
            if (current < ReputationTarget)
                _repField.SetValue(r, ReputationTarget);
        }

        private void HookEconomyEvents()
        {
            if (_eventsHooked) return;
            GameEvents.OnFundsChanged.Add(OnFundsChanged);
            GameEvents.OnReputationChanged.Add(OnReputationChanged);
            _eventsHooked = true;
        }

        private void UnhookEconomyEvents()
        {
            if (!_eventsHooked) return;
            GameEvents.OnFundsChanged.Remove(OnFundsChanged);
            GameEvents.OnReputationChanged.Remove(OnReputationChanged);
            _eventsHooked = false;
        }

        // Re-assert on change. SetFunds/AddReputation fire these again, but the
        // next call sees the value already at target and does nothing —
        // terminating after one re-entry.
        private void OnFundsChanged(double newFunds, TransactionReasons reason) => AssertFunds();
        private void OnReputationChanged(float newRep, TransactionReasons reason) => AssertReputation();
    }
}
