using System;
using FinePrint;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Deterministic, water-free surface site picker shared by the
    /// server-seeded contracts that need a fixed ground location (surface-rescue
    /// spawn site, survey waypoint). The same (body, seed, latitude-bound)
    /// always yields the same site, so a reload or re-offer never re-rolls it.
    ///
    /// Mirrors stock RecoverAsset / Survey site selection: FinePrint's random
    /// water-free position, re-rolled until it falls inside the seeded latitude
    /// bound. On exhaustion (a mostly-ocean latitude band) it returns the
    /// smallest-|lat| water-free candidate and logs — still water-free,
    /// marginally beyond what the generator charged, deterministic either way.
    /// </summary>
    public static class SeededSites
    {
        private const int SitePickAttempts = 50;

        /// <summary>
        /// Pick a deterministic water-free site on <paramref name="body"/> from a
        /// seed and a symmetric latitude bound (deg). Returns false only when the
        /// body has no terrain data yet (caller should retry); true with
        /// <paramref name="lat"/>/<paramref name="lon"/> set otherwise.
        /// </summary>
        public static bool PickSite(CelestialBody body, int seed, double latBoundDeg,
                                    out double lat, out double lon)
        {
            lat = 0.0;
            lon = 0.0;
            if (body == null) return false;
            var rng = new System.Random(seed);
            double bestLat = double.NaN, bestLon = 0.0;
            for (int i = 0; i < SitePickAttempts; i++)
            {
                double candLat, candLon;
                WaypointManager.ChooseRandomPosition(
                    out candLat, out candLon, body.GetName(),
                    waterAllowed: false, equatorial: false, generator: rng);
                if (double.IsNaN(bestLat) || Math.Abs(candLat) < Math.Abs(bestLat))
                {
                    bestLat = candLat;
                    bestLon = candLon;
                }
                if (Math.Abs(bestLat) <= latBoundDeg) break;
            }
            if (double.IsNaN(bestLat)) return false;   // no terrain data yet — retry
            if (Math.Abs(bestLat) > latBoundDeg)
                Debug.LogWarning($"[KSP-AP] seeded site: no land within ±{latBoundDeg:F1}° "
                               + $"on {body.GetName()} after {SitePickAttempts} tries; "
                               + $"using lat {bestLat:F2}");
            lat = bestLat;
            lon = bestLon;
            return true;
        }
    }
}
