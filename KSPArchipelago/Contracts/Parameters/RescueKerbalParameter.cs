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
    ///  - OnUpdate (first safe frame): spawn the stranded Kerbal + pod, once.
    ///  - onVesselRecovered: complete when the recovered vessel carries the
    ///    stranded Kerbal (the player rendezvoused, boarded them, and came home).
    ///  - OnSave/OnLoad: persist the Kerbal name + spawned flag + stranded vessel
    ///    id so a reload neither re-spawns nor loses the target.
    /// </summary>
    public class RescueKerbalParameter : ContractParameter
    {
        public string BodyName = "";
        public string KerbalName = "";
        private bool _spawned = false;
        private uint _strandedVesselId = 0;

        public RescueKerbalParameter() { }

        public RescueKerbalParameter(string bodyName)
        {
            BodyName = bodyName ?? "";
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
            if (!_spawned) TrySpawn();
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
                    Debug.LogWarning($"[KSP-AP] rescue: unknown body '{BodyName}'");
                    _spawned = true;   // don't spin forever on a bad body
                    return;
                }

                // A fresh, unowned Kerbal is the rescue target.
                ProtoCrewMember kerbal = HighLogic.CurrentGame.CrewRoster.GetNewKerbal(
                    ProtoCrewMember.KerbalType.Unowned);
                KerbalName = kerbal.name;

                // A small enclosed pod holding the stranded Kerbal, in a low orbit.
                uint flightId = ShipConstruction.GetUniqueFlightID(
                    HighLogic.CurrentGame.flightState);
                ConfigNode partNode = ProtoVessel.CreatePartNode(
                    "mk1pod_v2", flightId, kerbal);
                double floor = body.Radius * 0.12
                    + (body.atmosphere ? body.atmosphereDepth : 0.0);
                double ceil = floor + body.Radius * 0.25;
                Orbit orbit = Orbit.CreateRandomOrbitAround(
                    body, body.Radius + floor, body.Radius + ceil);
                ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                    "Stranded " + KerbalName, VesselType.Ship, orbit, 0,
                    new[] { partNode });
                ProtoVessel pv = HighLogic.CurrentGame.AddVessel(vesselNode);
                _strandedVesselId = pv != null ? pv.persistentId : 0;
                _spawned = true;
                Debug.Log($"[KSP-AP] rescue: spawned {KerbalName} in orbit of {BodyName}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] rescue spawn failed: {ex}");
                _spawned = true;   // a hard failure shouldn't retry every frame
            }
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
            node.AddValue("spawned", _spawned);
            node.AddValue("stranded_id", _strandedVesselId);
        }

        protected override void OnLoad(ConfigNode node)
        {
            BodyName = node.GetValue("body") ?? "";
            KerbalName = node.GetValue("kerbal") ?? "";
            bool.TryParse(node.GetValue("spawned"), out _spawned);
            uint.TryParse(node.GetValue("stranded_id"), out _strandedVesselId);
        }
    }
}
