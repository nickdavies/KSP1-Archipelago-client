using System;
using System.Globalization;
using Contracts;
using FinePrint.Utilities;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Surface-rescue parameter: SPAWNS a stranded Kerbal standing ON THE
    /// SURFACE of a body (stock RecoverAsset SURFACE-recovery style: a lone
    /// kerbalEVA vessel grounded at a seeded site) and completes when that
    /// Kerbal is recovered. Shares the orbital rescue's lifecycle — retry
    /// discipline, Unowned discovery, recovery completion, save self-heal —
    /// and differs only in the spawn:
    ///  - the site is picked deterministically from the server seed via
    ///    FinePrint's ChooseRandomPosition (water excluded), constrained to the
    ///    seeded latitude bound the generator charged the ascent plane change
    ///    for, and persisted so reloads never re-roll;
    ///  - the vessel node is grounded stock-style (sit=LANDED, landed=True,
    ///    alt=TerrainAltitude+50, hgt=0, rot=surface normal, prst=true);
    ///  - the Kerbal is already ON EVA, so the pod-exit event never fires for
    ///    them — the jetpack+chute top-up is queued whenever the stranded
    ///    vessel is loaded and still missing the jetpack.
    /// </summary>
    public class SurfaceRescueKerbalParameter : RescueKerbalParameter
    {
        // Server-seeded site-latitude bound (deg, symmetric ±). The generator
        // charges the ascent plane change for exactly this bound, so any site
        // inside it costs no more than logic modelled.
        public double LatBound = 0.0;
        // Server-seeded rng seed for the deterministic site pick.
        public int SiteSeed = 0;
        private double _siteLat = 0.0;
        private double _siteLon = 0.0;
        private bool _sitePicked = false;
        private bool _evaEquipped = false;

        public SurfaceRescueKerbalParameter() { }

        public SurfaceRescueKerbalParameter(string bodyName, double latBound, int seed)
            : base(bodyName, 0.0)
        {
            LatBound = latBound;
            SiteSeed = seed;
        }

        protected override string GetTitle()
        {
            string who = string.IsNullOrEmpty(KerbalName) ? "a stranded Kerbal" : KerbalName;
            return $"Rescue {who} from the surface of {BodyName} and bring them home";
        }

        protected override string GetHashString()
            => "SurfaceRescueKerbal|" + BodyName + "|" + KerbalName;

        protected override void OnUpdate()
        {
            base.OnUpdate();   // spawn retry + deferred EVA gear top-up
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            if (_spawned && !_evaEquipped) QueueEvaEquipWhenLoaded();
        }

        protected override void TrySpawn()
        {
            if (!SpawnSceneReady()) return;
            try
            {
                CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
                if (body == null)
                {
                    // Bad server data — nothing we can ever do, so stop.
                    Debug.LogWarning($"[KSP-AP] surface rescue: unknown body '{BodyName}', giving up");
                    _spawned = true;
                    return;
                }
                if (!_sitePicked && !PickSite(body))
                    return;   // transient (no waypoint data yet) — retry on the throttle

                // Reuse the Kerbal a prior (failed) attempt already named, so a
                // self-healed retry doesn't litter the roster with orphans.
                ProtoCrewMember kerbal = ResolveOrCreateKerbal();
                KerbalName = kerbal.name;

                // A lone EVA Kerbal standing on the surface — stock RecoverAsset
                // SURFACE style (GenerateLoneKerbal + GroundNode).
                string evaPart = kerbal.gender == ProtoCrewMember.Gender.Female
                    ? "kerbalEVAfemale" : "kerbalEVA";
                uint flightId = ShipConstruction.GetUniqueFlightID(
                    HighLogic.CurrentGame.flightState);
                ConfigNode partNode = ProtoVessel.CreatePartNode(
                    evaPart, flightId, kerbal);
                // The vessel node needs SOME orbit at build time; grounding
                // below overwrites the situation (mirrors stock, which builds
                // an orbital node then GroundNode-s it).
                Orbit orbit = new Orbit(0.0, 0.0, body.Radius + 100000.0, 0.0,
                                        0.0, 0.0, Planetarium.GetUniversalTime(),
                                        body);
                ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                    "Stranded " + KerbalName, VesselType.EVA, orbit, 0,
                    new[] { partNode });
                vesselNode.AddValue("prst", true);
                GroundNode(vesselNode, body, _siteLat, _siteLon);
                ProtoVessel pv = HighLogic.CurrentGame.AddVessel(vesselNode);
                if (pv == null)
                {
                    // AddVessel failed — DON'T latch; retry.
                    Debug.LogWarning("[KSP-AP] surface rescue: AddVessel returned null, will retry");
                    return;
                }
                _strandedVesselId = pv.persistentId;
                MarkUnowned(pv);
                _spawned = true;   // only NOW is the spawn real
                Debug.Log($"[KSP-AP] surface rescue: spawned {KerbalName} landed on "
                        + $"{BodyName} at lat {_siteLat:F2}, lon {_siteLon:F2}");
            }
            catch (Exception ex)
            {
                // Do NOT latch _spawned — a transient failure must be retryable.
                Debug.LogError($"[KSP-AP] surface rescue spawn failed (will retry): {ex}");
            }
        }

        // Deterministic site pick via the shared seeded-site helper (FinePrint's
        // random water-free position, re-rolled until inside the seeded latitude
        // bound). Returns false only when the body has no terrain data yet —
        // retry on the throttle.
        private bool PickSite(CelestialBody body)
        {
            if (!SeededSites.PickSite(body, SiteSeed, LatBound, out _siteLat, out _siteLon))
                return false;
            _sitePicked = true;
            return true;
        }

        // Stock RecoverAsset.GroundNode: mutate the fresh orbital vessel node
        // into a landed one at (lat, lon), 50 m above the terrain so it
        // settles on physics load.
        private static void GroundNode(ConfigNode node, CelestialBody body,
                                       double lat, double lon)
        {
            if (node.HasValue("sit"))
                node.SetValue("sit", Vessel.Situations.LANDED.ToString());
            if (node.HasValue("landed"))
                node.SetValue("landed", "True");
            if (node.HasValue("lat"))
                node.SetValue("lat", lat.ToString(CultureInfo.InvariantCulture));
            if (node.HasValue("lon"))
                node.SetValue("lon", lon.ToString(CultureInfo.InvariantCulture));
            if (node.HasValue("alt"))
                node.SetValue("alt",
                    (CelestialUtilities.TerrainAltitude(body, lat, lon) + 50.0)
                        .ToString(CultureInfo.InvariantCulture));
            if (node.HasValue("hgt"))
                node.SetValue("hgt", "0");
            if (node.HasValue("rot"))
                node.SetValue("rot", KSPUtil.WriteQuaternion(
                    Quaternion.LookRotation(body.GetSurfaceNVector(lat, lon))));
        }

        // The stranded Kerbal spawns already ON EVA, so onCrewOnEva never fires
        // for them. Whenever their vessel is loaded (player nearby) and the
        // jetpack is still missing, queue the same deferred top-up the orbital
        // rescue uses on pod exit (StoreCargoPartAtSlot has no tech gate).
        private void QueueEvaEquipWhenLoaded()
        {
            try
            {
                if (_strandedVesselId == 0) return;
                Vessel v = null;
                var all = FlightGlobals.Vessels;
                if (all == null) return;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && all[i].persistentId == _strandedVesselId)
                    {
                        v = all[i];
                        break;
                    }
                }
                if (v == null || !v.loaded || v.rootPart == null) return;
                ModuleInventoryPart inv =
                    v.rootPart.FindModuleImplementing<ModuleInventoryPart>();
                if (inv == null)
                {
                    _evaEquipped = true;   // nothing to fill — don't rescan
                    return;
                }
                if (inv.storedParts != null && inv.ContainsPart("evaJetpack"))
                {
                    _evaEquipped = true;
                    return;
                }
                _pendingEvaEquip = v.rootPart;   // base TryEquipPendingEva stores the gear
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] surface rescue: EVA equip check failed: {ex.Message}");
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            node.AddValue("lat_bound", LatBound);
            node.AddValue("site_seed", SiteSeed);
            node.AddValue("site_picked", _sitePicked);
            node.AddValue("site_lat", _siteLat);
            node.AddValue("site_lon", _siteLon);
            node.AddValue("eva_equipped", _evaEquipped);
        }

        protected override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            double.TryParse(node.GetValue("lat_bound"), out LatBound);
            int.TryParse(node.GetValue("site_seed"), out SiteSeed);
            bool.TryParse(node.GetValue("site_picked"), out _sitePicked);
            double.TryParse(node.GetValue("site_lat"), out _siteLat);
            double.TryParse(node.GetValue("site_lon"), out _siteLon);
            bool.TryParse(node.GetValue("eva_equipped"), out _evaEquipped);
        }
    }
}
