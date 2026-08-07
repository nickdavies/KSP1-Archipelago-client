using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Goal-contract-mode (count / progressive_unlock) watcher.
    ///
    /// The generator awards the goal contract item(s) by pre-placing them on
    /// "Contract Threshold N" locations and telling the client, via slot_data
    /// <c>contract_thresholds</c>, at which completed-contract count each
    /// threshold location should be reported:
    /// <code>{ "8": ["Contract Threshold 1", ...] }  // report these once 8 done</code>
    ///
    /// Players complete contracts in any order while the generator only reasons
    /// about the COUNT, so the client mirrors that: it asks
    /// <see cref="ApContractManager.CountCompleted"/> how many non-goal contracts
    /// are done and reports every threshold whose count has been reached — the
    /// same count the UI shows, so the two can never disagree. Reporting releases
    /// the locked goal item through the normal AP channel, after which the existing
    /// item-gated offer machinery (<see cref="ApContractManager"/>) offers the goal
    /// contract.
    ///
    /// <see cref="ReportLocation"/> is idempotent, so re-evaluating every tick is
    /// safe; this also makes offline progress, reconnects, and server-side
    /// !collect self-heal. Empty config (findable / starting) makes it a no-op.
    /// </summary>
    public static class ContractThresholdWatcher
    {
        private sealed class Threshold
        {
            public int Count;
            public List<string> Locations;
        }

        private static readonly List<Threshold> _thresholds = new List<Threshold>();

        /// <summary>
        /// Configure from slot_data on connect. <paramref name="thresholdsTok"/>
        /// is the <c>contract_thresholds</c> map (count -> [location names]).
        /// An empty map disables the watcher. The completed-contract count comes
        /// from <see cref="ApContractManager"/>, which already owns the manifest.
        /// </summary>
        public static void Configure(JToken thresholdsTok)
        {
            _thresholds.Clear();

            if (thresholdsTok is JObject jo)
            {
                foreach (JProperty prop in jo.Properties())
                {
                    if (!int.TryParse(prop.Name, out int count)) continue;
                    var locs = new List<string>();
                    if (prop.Value is JArray arr)
                    {
                        foreach (JToken t in arr)
                        {
                            string s = (string)t;
                            if (!string.IsNullOrEmpty(s)) locs.Add(s);
                        }
                    }
                    if (locs.Count > 0)
                        _thresholds.Add(new Threshold { Count = count, Locations = locs });
                }
            }

            Debug.Log($"[KSP-AP] ContractThresholdWatcher: {_thresholds.Count} "
                    + $"threshold group(s), {ApContractManager.NonGoalCount} non-goal contract(s)");
        }

        /// <summary>
        /// Count completed contracts and report any reached thresholds. Driven
        /// from the main-thread Update loop (never re-entrantly from inside
        /// ReportLocation). Idempotent.
        /// </summary>
        public static void Evaluate(MissionTracker tracker)
        {
            if (tracker == null || _thresholds.Count == 0) return;

            int completed = ApContractManager.CountCompleted(tracker);

            foreach (Threshold th in _thresholds)
            {
                if (completed < th.Count) continue;
                foreach (string loc in th.Locations)
                    if (!tracker.IsLocationChecked(loc)) tracker.ReportLocation(loc);
            }
        }
    }
}
