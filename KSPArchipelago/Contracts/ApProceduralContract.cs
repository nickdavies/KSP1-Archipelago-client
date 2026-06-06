using System;
using System.Collections.Generic;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Base class for AP procedural contracts. KSP's ContractSystem calls
    /// Generate() to fill open offer slots; each call rolls a random valid
    /// (body, kind, ...) tuple from the live ContractEnvelope. Generated
    /// contracts pay funds for player progression and, on completion,
    /// consume the next available AP location from the per-type pool
    /// owned by ApProceduralCheckManager.
    ///
    /// Procedural types currently supported:
    ///   - ApProceduralAltitudeOrOrbitContract  ("altitude_or_orbit")
    ///   - ApProceduralTouristContract          ("tourist")
    ///   - ApProceduralSatelliteContract        ("satellite")
    ///   - ApProceduralMassDeliveryContract     ("mass_delivery")
    /// </summary>
    public abstract class ApProceduralContract : ApContract
    {
        /// <summary>Subclass returns its slot_data contract_type string.</summary>
        protected abstract string ContractTypeKey { get; }

        /// <summary>
        /// Kinds this contract type can target. Filters the envelope
        /// EnumerateValidPairs() result. E.g. tourist returns
        /// {"suborbital","orbit","flyby","landing"} and altitude_or_orbit
        /// returns {"altitude","orbit"}.
        /// </summary>
        protected abstract IEnumerable<string> AllowedKinds { get; }

        // Set in GeneratePopulate; persisted via OnSave/OnLoad so the
        // contract survives game save/load with its rolled params intact.
        protected string ChosenBody;
        protected string ChosenKind;
        protected float  ChosenPayloadKg;

        // AP location reported on complete. Reserved at Generate time from
        // ApProceduralCheckManager; null means "fund-only, no AP check".
        public string ChosenApLocationName { get; private set; }

        protected sealed override bool GeneratePopulate()
        {
            ContractEnvelope env = ApContractsRuntime.Envelope;
            if (env == null) return false;

            // Pick a random eligible (body, kind, KindEntry).
            var pairs = new List<KeyValuePair<string, KeyValuePair<string, ContractEnvelope.KindEntry>>>();
            var allowed = new HashSet<string>(AllowedKinds);
            foreach (var p in env.EnumerateValidPairs())
            {
                if (allowed.Contains(p.Value.Key))
                    pairs.Add(p);
            }
            if (pairs.Count == 0) return false;

            var pick = pairs[UnityEngine.Random.Range(0, pairs.Count)];
            ChosenBody = pick.Key;
            ChosenKind = pick.Value.Key;
            ContractEnvelope.KindEntry kindEntry = pick.Value.Value;

            // Roll a payload size within the envelope's max. Subclasses
            // may further shape the payload (e.g. tourist count); default
            // is "no dummy mass" so the empty mission qualifies.
            ChosenPayloadKg = RollPayloadKg(kindEntry);

            // Reserve an AP location from the per-type pool — if any are
            // available. None left means fund-only (the contract still
            // spawns and pays funds; no AP check).
            ChosenApLocationName = ApProceduralCheckManager
                .ReserveNext(ContractTypeKey);

            float funds = RollFunds(kindEntry);
            if (funds > 0f)
            {
                SetFunds(advance: funds * 0.1f, completion: funds, failure: 0f);
            }

            PopulateParameters(ChosenBody, ChosenKind, kindEntry);
            return true;
        }

        /// <summary>
        /// Roll a random payload mass within the envelope's max. Default
        /// implementation rolls uniformly in [0, max]. Override for
        /// contract types where payload semantics differ (tourists are a
        /// crew count, not a mass; satellites need at least the part
        /// mass).
        /// </summary>
        protected virtual float RollPayloadKg(ContractEnvelope.KindEntry entry)
            => UnityEngine.Random.Range(0f, Math.Max(0f, entry.MaxPayloadKg));

        /// <summary>
        /// Funds reward heuristic. Default scales with payload mass:
        /// base 10000 + 100 per kg payload. Tweak after seed iteration.
        /// </summary>
        protected virtual float RollFunds(ContractEnvelope.KindEntry entry)
            => 10000f + 100f * ChosenPayloadKg;

        /// <summary>
        /// Build the KSP ContractParameter tree. Subclasses receive the
        /// rolled body, kind, and envelope entry to size the mission.
        /// </summary>
        protected abstract void PopulateParameters(
            string bodyName, string kind, ContractEnvelope.KindEntry envelope);

        public override bool MeetRequirements()
        {
            ContractEnvelope env = ApContractsRuntime.Envelope;
            return env != null && env.HasAnyValidKind(AllowedKinds);
        }

        protected override string GetHashString()
            => GetType().Name + "|" + ChosenBody + "|" + ChosenKind
             + "|" + ChosenPayloadKg.ToString("F0")
             + "|" + (ChosenApLocationName ?? "<no-ap>");

        protected override string GetTitle()
            => $"{TypeDisplayName} – {ChosenBody} {ChosenKind}";

        protected override string GetSynopsys()
            => $"Procedural {TypeDisplayName.ToLower()} mission to {ChosenBody}.";

        protected override string GetDescription()
            => GetSynopsys();

        /// <summary>Human-readable name for display ("Tourist", "Satellite", etc.).</summary>
        protected abstract string TypeDisplayName { get; }

        protected override void OnLoad(ConfigNode node)
        {
            ChosenBody = node.GetValue("body");
            ChosenKind = node.GetValue("kind");
            float.TryParse(node.GetValue("payload_kg"), out float pkg);
            ChosenPayloadKg = pkg;
            ChosenApLocationName = node.GetValue("ap_location_name");
        }

        protected override void OnSave(ConfigNode node)
        {
            if (!string.IsNullOrEmpty(ChosenBody))
                node.AddValue("body", ChosenBody);
            if (!string.IsNullOrEmpty(ChosenKind))
                node.AddValue("kind", ChosenKind);
            node.AddValue("payload_kg", ChosenPayloadKg);
            if (!string.IsNullOrEmpty(ChosenApLocationName))
                node.AddValue("ap_location_name", ChosenApLocationName);
        }

        protected override void OnCompleted()
        {
            base.OnCompleted();
            if (!string.IsNullOrEmpty(ChosenApLocationName))
            {
                ReportAndLog(ChosenApLocationName);
                ApProceduralCheckManager.ConfirmConsumed(
                    ContractTypeKey, ChosenApLocationName);
            }
        }

        protected override void OnWithdrawn()
        {
            base.OnWithdrawn();
            ReleaseReservation();
        }

        protected override void OnDeclined()
        {
            base.OnDeclined();
            ReleaseReservation();
        }

        protected override void OnFailed()
        {
            base.OnFailed();
            ReleaseReservation();
        }

        private void ReleaseReservation()
        {
            if (string.IsNullOrEmpty(ChosenApLocationName)) return;
            ApProceduralCheckManager.ReleaseReservation(
                ContractTypeKey, ChosenApLocationName);
            ChosenApLocationName = null;
        }
    }
}
