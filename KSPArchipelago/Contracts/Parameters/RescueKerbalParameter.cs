using System;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Rescue-contract parameter: SPAWNS a stranded Kerbal in low orbit of a body
    /// when the contract is active, and completes when that Kerbal is recovered at
    /// the space centre. Unlike every other parameter (which only watches the
    /// player's own vessel), this one creates world state — so it is the one piece
    /// that genuinely needs in-game play-testing.
    ///
    /// Lifecycle:
    ///  - OnUpdate (throttled): spawn the stranded Kerbal + pod once a stable
    ///    scene is up. `_spawned` is set ONLY after a vessel actually exists, so
    ///    a transient failure retries instead of latching a no-vessel contract.
    ///  - onVesselRecovered: complete when the recovered vessel carries the
    ///    stranded Kerbal (the player rendezvoused, boarded them, and came home).
    ///  - OnSave/OnLoad: persist the Kerbal name + spawned flag + stranded vessel
    ///    id so a reload neither re-spawns nor loses the target. OnLoad self-heals
    ///    a `spawned=true, stranded_id=0` save (a previously latched failed spawn).
    /// </summary>
    public class RescueKerbalParameter : ContractParameter
    {
        public string BodyName = "";
        public string KerbalName = "";
        // Server-seeded orbit radius from the body centre, metres (the rescue
        // primitive's required "sma"; circular ⇒ sma == radius). The generator
        // picks it collision-safe against moon SOIs and models the rescue dv to
        // it, so honouring it keeps the contract's logic cost and the spawned
        // target in agreement.
        public double Sma = 0.0;
        private bool _spawned = false;        // true only once a vessel actually exists
        private uint _strandedVesselId = 0;
        private float _nextAttempt = 0f;      // in-memory retry throttle (not persisted)

        public RescueKerbalParameter() { }

        public RescueKerbalParameter(string bodyName, double sma)
        {
            BodyName = bodyName ?? "";
            Sma = sma;
        }

        protected override string GetTitle()
        {
            string who = string.IsNullOrEmpty(KerbalName) ? "a stranded Kerbal" : KerbalName;
            return $"Rescue {who} from orbit of {BodyName} and bring them home";
        }

        protected override string GetHashString()
            => "RescueKerbal|" + BodyName + "|" + KerbalName;

        protected override void OnRegister()
        {
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
        }

        protected override void OnUnregister()
        {
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
        }

        protected override void OnUpdate()
        {
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            if (_spawned) return;
            // Retry throttle: a transient failure (scene not ready, no pod yet)
            // must not spam every tick, but must also never latch permanently —
            // the previous "set _spawned on failure" baked a no-vessel contract
            // into the save forever.
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 5f;
            TrySpawn();
        }

        private void TrySpawn()
        {
            // Spawn only from a stable scene where the game state is ready —
            // never mid scene-load (that races KSP's own vessel loading).
            if (HighLogic.CurrentGame == null) return;
            if (!FlightGlobals.ready
                && HighLogic.LoadedScene != GameScenes.SPACECENTER
                && HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
            try
            {
                CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
                if (body == null)
                {
                    // Bad server data — nothing we can ever do, so stop.
                    Debug.LogWarning($"[KSP-AP] rescue: unknown body '{BodyName}', giving up");
                    _spawned = true;
                    return;
                }
                if (Sma <= 0.0)
                {
                    // Valid seeds always carry a positive sma (required at parse);
                    // a zero here is only a pre-sma save — nothing safe to spawn.
                    Debug.LogWarning($"[KSP-AP] rescue: no seeded orbit (sma) for '{BodyName}', giving up");
                    _spawned = true;
                    return;
                }

                // Resolve a crewed command pod defensively. A hard-coded
                // "mk1pod_v2" returned null from getPartInfoByName at runtime
                // (CreatePartNode then threw "No part in loader found"). Try the
                // known stock names AND scan for any crewed pod, passing the
                // RESOLVED name so the internal lookup is guaranteed to hit.
                AvailablePart pod = ResolveRescuePod();
                if (pod == null)
                {
                    // Transient/unexpected — DON'T latch; retry on the throttle.
                    Debug.LogWarning("[KSP-AP] rescue: no usable crew pod part found yet, will retry");
                    return;
                }

                // Reuse the Kerbal a prior (failed) attempt already named, so a
                // self-healed retry doesn't litter the roster with orphans.
                ProtoCrewMember kerbal = ResolveOrCreateKerbal();
                KerbalName = kerbal.name;

                // A small enclosed pod holding the stranded Kerbal, in a low orbit.
                uint flightId = ShipConstruction.GetUniqueFlightID(
                    HighLogic.CurrentGame.flightState);
                ConfigNode partNode = ProtoVessel.CreatePartNode(
                    pod.name, flightId, kerbal);
                Orbit orbit = BuildOrbit(body);
                ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                    "Stranded " + KerbalName, VesselType.Ship, orbit, 0,
                    new[] { partNode });
                ProtoVessel pv = HighLogic.CurrentGame.AddVessel(vesselNode);
                if (pv == null)
                {
                    // AddVessel failed — DON'T latch; retry.
                    Debug.LogWarning("[KSP-AP] rescue: AddVessel returned null, will retry");
                    return;
                }
                _strandedVesselId = pv.persistentId;
                // Mark the spawned vessel UNOWNED so the player can't just fly it
                // or EVA the Kerbal — they must rendezvous and recover. Done on the
                // proto AFTER AddVessel (null-safe): building the discovery into
                // the vessel node NRE'd inside CreateVesselNode.
                try
                {
                    pv.discoveryInfo = ProtoVessel.CreateDiscoveryNode(
                        DiscoveryLevels.Unowned, UntrackedObjectClass.A,
                        double.PositiveInfinity, double.PositiveInfinity);
                    if (pv.vesselRef != null && pv.vesselRef.DiscoveryInfo != null)
                        pv.vesselRef.DiscoveryInfo.SetLevel(DiscoveryLevels.Unowned);
                }
                catch (Exception dex)
                {
                    Debug.LogWarning($"[KSP-AP] rescue: could not set Unowned discovery: {dex.Message}");
                }
                _spawned = true;   // only NOW is the spawn real
                Debug.Log($"[KSP-AP] rescue: spawned {KerbalName} ({pod.name}) in orbit of {BodyName}");
            }
            catch (Exception ex)
            {
                // Do NOT latch _spawned — a transient failure must be retryable,
                // and a persisted "spawned with no vessel" is the bug we're fixing.
                Debug.LogError($"[KSP-AP] rescue spawn failed (will retry): {ex}");
            }
        }

        // Reuse the Kerbal named by an earlier attempt if it still exists,
        // otherwise mint a fresh unowned rescue target.
        private static ProtoCrewMember LookupKerbal(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return null;
            foreach (ProtoCrewMember c in roster.Unowned)
                if (c != null && c.name == name) return c;
            foreach (ProtoCrewMember c in roster.Crew)
                if (c != null && c.name == name) return c;
            return null;
        }

        private ProtoCrewMember ResolveOrCreateKerbal()
        {
            ProtoCrewMember existing = LookupKerbal(KerbalName);
            if (existing != null) return existing;
            return HighLogic.CurrentGame.CrewRoster.GetNewKerbal(
                ProtoCrewMember.KerbalType.Unowned);
        }

        // The orbit to spawn the stranded vessel in: a circular EQUATORIAL orbit
        // at the server-seeded Sma radius from the body centre — the orbit the
        // generator modelled the dv against and verified clear of moon SOIs. A
        // random phase keeps concurrent rescues from stacking.
        private Orbit BuildOrbit(CelestialBody body)
        {
            double phase = UnityEngine.Random.Range(0f, (float)(2.0 * Math.PI));
            return new Orbit(0.0, 0.0, Sma, 0.0, 0.0, phase,
                             Planetarium.GetUniversalTime(), body);
        }

        // First loaded crewable command pod. Tries the common stock pods by name,
        // then falls back to scanning every loaded part for a crew-carrying
        // command module so a renamed/missing pod can never break the rescue.
        private static AvailablePart ResolveRescuePod()
        {
            string[] candidates = { "mk1pod_v2", "mk1pod.v2", "mk1Pod", "mk1pod",
                                    "landerCabinSmall", "Mark1Cockpit" };
            foreach (string n in candidates)
            {
                AvailablePart ap = PartLoader.getPartInfoByName(n);
                if (ap?.partPrefab != null) return ap;
            }
            var loaded = PartLoader.LoadedPartsList;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    AvailablePart ap = loaded[i];
                    if (ap?.partPrefab == null) continue;
                    if (ap.partPrefab.CrewCapacity >= 1
                        && ap.partPrefab.FindModuleImplementing<ModuleCommand>() != null)
                        return ap;
                }
            }
            return null;
        }

        private void OnVesselRecovered(ProtoVessel pv, bool quick)
        {
            if (state == ParameterState.Complete) return;
            if (pv == null || string.IsNullOrEmpty(KerbalName)) return;
            foreach (ProtoCrewMember crew in pv.GetVesselCrew())
            {
                if (crew != null && crew.name == KerbalName)
                {
                    SetComplete();
                    return;
                }
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("body", BodyName);
            node.AddValue("kerbal", KerbalName);
            node.AddValue("sma", Sma);
            node.AddValue("spawned", _spawned);
            node.AddValue("stranded_id", _strandedVesselId);
        }

        protected override void OnLoad(ConfigNode node)
        {
            BodyName = node.GetValue("body") ?? "";
            KerbalName = node.GetValue("kerbal") ?? "";
            double.TryParse(node.GetValue("sma"), out Sma);
            bool.TryParse(node.GetValue("spawned"), out _spawned);
            uint.TryParse(node.GetValue("stranded_id"), out _strandedVesselId);
            // Self-heal: a save where spawned=true but no vessel id was recorded
            // is a latched failed spawn (the old code set spawned=true in its
            // catch). Clear it so OnUpdate retries and actually creates the craft.
            if (_spawned && _strandedVesselId == 0)
            {
                _spawned = false;
                Debug.Log("[KSP-AP] rescue: clearing latched failed spawn "
                        + $"(body={BodyName}), will respawn");
            }
        }
    }
}
