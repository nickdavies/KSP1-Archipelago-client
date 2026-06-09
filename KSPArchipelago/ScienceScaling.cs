using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Writes absolute per-body, per-situation science values into every
    /// CelestialBody.scienceValues at AP-connect time.  The values come
    /// straight from slot_data (key <c>science_values</c>); no math,
    /// no scaling, no defaults happen here — the server is the single
    /// source of truth and this class just applies what it's told.
    ///
    /// SOURCE OF TRUTH: bodies.py:home_relative_science_values builds
    /// the dict as <c>stock_mult * science_scalar(body, home)</c>; the
    /// server's science_budget uses the same expression.  Because we
    /// write absolute values rather than compute them here, the server
    /// and client cannot drift.
    ///
    /// NO SILENT DEFAULTS: ValidatePayload() rejects any payload that
    /// doesn't contain every loaded CelestialBody, or any body missing
    /// any of the seven situation fields.  An incomplete payload is a
    /// server bug that should fail the connect, not a thing to paper
    /// over with stock fallbacks.
    /// </summary>
    public static class ScienceScaling
    {
        // The seven required slot_data field names. Every body entry
        // in the payload MUST contain every name in this list — missing
        // fields hard-fail. Matches bodies.py:SCIENCE_SITUATIONS.
        private static readonly string[] _situationKeys = new string[]
        {
            "landed", "splashed", "fly_low", "fly_high",
            "space_low", "space_high", "recovery",
        };

        private struct StockValues
        {
            public float LandedDataValue;
            public float SplashedDataValue;
            public float FlyingLowDataValue;
            public float FlyingHighDataValue;
            public float InSpaceLowDataValue;
            public float InSpaceHighDataValue;
            public float RecoveryValue;
        }

        // Stock snapshot exists ONLY to restore on disconnect — Apply()
        // never reads from it (it writes server-provided absolutes).
        // Snapshotted once per session before the first Apply mutates
        // anything.
        private static readonly Dictionary<string, StockValues> _snapshot =
            new Dictionary<string, StockValues>();
        private static bool _snapshotTaken;

        /// <summary>
        /// Validate the payload against the currently-loaded CelestialBodies.
        /// Returns null on success, or a human-readable error string that
        /// the caller (KSPArchipelagoMod.HandleConnect) surfaces as a
        /// disconnect-with-toast.  No silent defaults: every body that
        /// exists in FlightGlobals must have an entry, every entry must
        /// contain all seven situation keys.
        ///
        /// Returns "deferred" if FlightGlobals.Bodies isn't populated
        /// yet — the caller is expected to retry on the next Update().
        /// </summary>
        public static string ValidatePayload(Dictionary<string, Dictionary<string, float>> payload)
        {
            if (payload == null)
                return "science_values payload is null";
            if (FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0)
                return "deferred";

            // Every loaded body must appear in payload — a missing body
            // means the server doesn't know about a planet pack that's
            // installed, and silently leaving it at stock would be a
            // hidden science-economy split.
            var missingBodies = new List<string>();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null) continue;
                if (!payload.ContainsKey(body.bodyName))
                    missingBodies.Add(body.bodyName);
            }
            if (missingBodies.Count > 0)
            {
                return "science_values payload missing bodies present in "
                       + "FlightGlobals.Bodies: " + string.Join(", ", missingBodies)
                       + ". Server's body list doesn't match this KSP install — "
                       + "check planet packs.";
            }

            // Every entry must have all seven situation keys.
            foreach (var kvp in payload)
            {
                var missingFields = new List<string>();
                for (int i = 0; i < _situationKeys.Length; i++)
                {
                    if (!kvp.Value.ContainsKey(_situationKeys[i]))
                        missingFields.Add(_situationKeys[i]);
                }
                if (missingFields.Count > 0)
                {
                    return $"science_values entry for '{kvp.Key}' missing "
                           + "required fields: " + string.Join(", ", missingFields);
                }
            }

            return null;
        }

        private static void SnapshotStockIfNeeded()
        {
            if (_snapshotTaken) return;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body.scienceValues == null) continue;
                _snapshot[body.bodyName] = new StockValues
                {
                    LandedDataValue     = body.scienceValues.LandedDataValue,
                    SplashedDataValue   = body.scienceValues.SplashedDataValue,
                    FlyingLowDataValue  = body.scienceValues.FlyingLowDataValue,
                    FlyingHighDataValue = body.scienceValues.FlyingHighDataValue,
                    InSpaceLowDataValue = body.scienceValues.InSpaceLowDataValue,
                    InSpaceHighDataValue= body.scienceValues.InSpaceHighDataValue,
                    RecoveryValue       = body.scienceValues.RecoveryValue,
                };
            }
            _snapshotTaken = true;
            Debug.Log($"[KSP-AP] ScienceScaling: snapshotted stock values for {_snapshot.Count} bodies");
        }

        /// <summary>
        /// Trigger the one-time stock-values snapshot.
        /// Returns false if FlightGlobals.Bodies isn't populated yet;
        /// caller is expected to retry on the next Update().  Safe to
        /// call from any AP-connect path, including Kerbin-home seeds
        /// that won't go on to call Apply().
        /// </summary>
        public static bool PrepareSnapshot()
        {
            if (FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0)
                return false;
            SnapshotStockIfNeeded();
            return true;
        }

        /// <summary>
        /// Apply the payload — write each body's seven situation values
        /// straight into CelestialBody.scienceValues.  Precondition:
        /// caller has called ValidatePayload and got null back (or
        /// retried until it did).  The snapshot is taken here, before
        /// the first write, so Reset() can restore stock on disconnect.
        /// </summary>
        public static void Apply(Dictionary<string, Dictionary<string, float>> payload)
        {
            SnapshotStockIfNeeded();

            int applied = 0;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body.scienceValues == null) continue;
                if (!payload.TryGetValue(body.bodyName, out var fields)) continue;
                body.scienceValues.LandedDataValue     = fields["landed"];
                body.scienceValues.SplashedDataValue   = fields["splashed"];
                body.scienceValues.FlyingLowDataValue  = fields["fly_low"];
                body.scienceValues.FlyingHighDataValue = fields["fly_high"];
                body.scienceValues.InSpaceLowDataValue = fields["space_low"];
                body.scienceValues.InSpaceHighDataValue= fields["space_high"];
                body.scienceValues.RecoveryValue       = fields["recovery"];
                applied++;
            }
            Debug.Log($"[KSP-AP] ScienceScaling: applied absolute values to {applied} bodies");
        }

        /// <summary>
        /// Restore every body's scienceValues to the stock snapshot.
        /// No-op if no snapshot was taken (the player never connected
        /// to a non-Kerbin-home seed in this session).
        /// </summary>
        public static void Reset()
        {
            if (!_snapshotTaken) return;
            if (FlightGlobals.Bodies == null) return;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body.scienceValues == null) continue;
                if (!_snapshot.TryGetValue(body.bodyName, out var stock)) continue;
                body.scienceValues.LandedDataValue     = stock.LandedDataValue;
                body.scienceValues.SplashedDataValue   = stock.SplashedDataValue;
                body.scienceValues.FlyingLowDataValue  = stock.FlyingLowDataValue;
                body.scienceValues.FlyingHighDataValue = stock.FlyingHighDataValue;
                body.scienceValues.InSpaceLowDataValue = stock.InSpaceLowDataValue;
                body.scienceValues.InSpaceHighDataValue= stock.InSpaceHighDataValue;
                body.scienceValues.RecoveryValue       = stock.RecoveryValue;
            }
            Debug.Log("[KSP-AP] ScienceScaling: reset to stock");
        }
    }
}
