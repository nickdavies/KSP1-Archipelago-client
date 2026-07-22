using System;
using Contracts;
using FinePrint.Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Tourism-contract host parameter, following the rescue discipline
    /// (resolve-or-create a roster member + persist so reloads/re-offers never
    /// duplicate). On construction it resolves-or-creates a
    /// <c>KerbalType.Tourist</c> for the server-seeded name+gender and hosts the
    /// stock tour objective tree — a <see cref="KerbalTourParameter"/> with a
    /// child <see cref="KerbalDestinationParameter"/>(body, Suborbit|Orbit) —
    /// exactly as stock <c>TourismContract</c> nests them (Assembly-CSharp
    /// decompiled :887520/:887577). Completion rides the stock params (tourist
    /// flight log reaches the destination, then the vessel is recovered).
    ///
    /// The child tree is built in the ctor, BEFORE the contract goes Active,
    /// because <c>ContractParameter.Register</c> recurses into existing children
    /// and then fires <c>OnRegister</c> (:907685) — a child added during
    /// <c>OnRegister</c> would never be registered.
    ///
    /// Tourist death: the stock <see cref="KerbalTourParameter"/> already fails
    /// the whole contract on <c>onCrewKilled</c> (:892127). This host adds an
    /// <c>onKerbalRemoved</c> hook as a complementary self-heal — either way the
    /// contract FAILS, and <c>ApContractManager.ReconcileOffers</c> re-offers it
    /// (location unchecked, no longer live), minting a fresh tourist. With
    /// <c>MissingCrewsRespawn</c> forced true (CareerHackManager) a killed
    /// tourist normally just respawns, so this is a rare safety net, never a
    /// soft-lock.
    /// </summary>
    public class ApTouristParameter : ContractParameter, IContractParameterPostAttach
    {
        /// <summary>Server-requested tourist name (best-effort; may collide). Persisted.</summary>
        public string WireName = "";
        public bool Female;
        /// <summary>Destination body. Persisted.</summary>
        public string BodyName = "";
        /// <summary>Suborbit or Orbit (stock flight-log entry). Persisted.</summary>
        public FlightLog.EntryType EntryType = FlightLog.EntryType.Orbit;
        /// <summary>Roster name actually used (may differ from WireName). Persisted.</summary>
        public string ActualName = "";

        private bool _eventsHooked;

        public ApTouristParameter() { }   // KSP deserialization

        public ApTouristParameter(string wireName, bool female, string bodyName,
                                  FlightLog.EntryType entryType)
        {
            WireName = wireName ?? "";
            Female = female;
            BodyName = bodyName ?? "";
            EntryType = entryType;
            // Children are built in OnContractAttached (post-attach), NOT here.
            // The stock KerbalTourParameter we nest dereferences Root in
            // GetHashString, and KSP only wires this parameter's Root when it is
            // added to the contract — building the subtree now would leave the
            // stock child's Root null and NRE in Contract.Generate's hash pass.
            // See IContractParameterPostAttach.
        }

        // IContractParameterPostAttach: build the stock tour subtree now that this
        // parameter is attached to its contract (Root is set) and before the
        // contract registers. Only the force-offer path runs this; on reload KSP
        // restores the subtree from the save, so it never re-runs.
        public void OnContractAttached() => BuildChildren();

        // Resolve-or-create the tourist and add the stock objective subtree. Runs
        // once, at generation time. On reload KSP restores the subtree from the
        // save, so this is never re-run (the parameterless ctor adds nothing).
        private void BuildChildren()
        {
            CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
            if (body == null)
                throw new FormatException($"tourist primitive: unknown body '{BodyName}'");
            ProtoCrewMember tourist = ResolveOrCreateTourist();
            ActualName = tourist.name;
            // Attach top-down. KSP's AddParameter roots only the node it is handed
            // (NestToParent), never that node's existing children, so every node
            // must be attached to an already-rooted parent BEFORE it receives its
            // own children. Add `tour` to this host (rooted in OnContractAttached)
            // first, THEN the destination to `tour` — otherwise the destination
            // inherits `tour`'s not-yet-set (null) Root and NREs in the hash pass
            // (SuperSeed(Root)). See IContractParameterPostAttach.
            var tour = new KerbalTourParameter(ActualName, tourist.gender);
            AddParameter(tour);
            tour.AddParameter(new KerbalDestinationParameter(body, EntryType, ActualName));
        }

        private ProtoCrewMember ResolveOrCreateTourist()
        {
            KerbalRoster roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
                throw new FormatException("tourist primitive: no crew roster available");
            // Reuse an existing tourist with the requested name (a re-offer after
            // a non-fatal withdrawal must not mint duplicates).
            if (!string.IsNullOrEmpty(WireName) && roster.Exists(WireName))
            {
                ProtoCrewMember existing = roster[WireName];
                if (existing != null && existing.type == ProtoCrewMember.KerbalType.Tourist)
                    return existing;
            }
            // Try the server-requested name first (deterministic display +
            // manifest parity). AddCrewMember rejects a first-name collision with
            // the stock roster, in which case fall back to a game-generated,
            // guaranteed-unique tourist.
            ProtoCrewMember tourist = TryCreateNamedTourist();
            if (tourist == null)
            {
                tourist = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Tourist);
                tourist.gender = Female ? ProtoCrewMember.Gender.Female
                                        : ProtoCrewMember.Gender.Male;
            }
            return tourist;
        }

        // Create a KerbalType.Tourist named for the wire request. AddCrewMember
        // sets the tourist experience trait (SetExperienceTrait keys off the
        // Tourist type) and returns false on a first-name collision.
        private ProtoCrewMember TryCreateNamedTourist()
        {
            if (string.IsNullOrEmpty(WireName)) return null;
            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            var pcm = new ProtoCrewMember(ProtoCrewMember.KerbalType.Tourist, WireName);
            pcm.gender = Female ? ProtoCrewMember.Gender.Female
                                : ProtoCrewMember.Gender.Male;
            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
            if (!roster.AddCrewMember(pcm)) return null;
            return pcm;
        }

        protected override string GetTitle()
        {
            string who = string.IsNullOrEmpty(ActualName) ? WireName : ActualName;
            string dest = EntryType == FlightLog.EntryType.Suborbit
                ? $"a suborbital flight over {BodyName}"
                : $"orbit of {BodyName}";
            return $"Fly tourist {who} to {dest} and return them safely";
        }

        protected override string GetHashString()
            => "ApTourist|" + BodyName + "|" + WireName + "|" + ActualName;

        protected override void OnRegister()
        {
            GameEvents.onKerbalRemoved.Add(OnKerbalRemoved);
            _eventsHooked = true;
        }

        protected override void OnUnregister()
        {
            if (!_eventsHooked) return;
            GameEvents.onKerbalRemoved.Remove(OnKerbalRemoved);
            _eventsHooked = false;
        }

        // Bubble completion up: this host is a top-level contract parameter, so
        // it must complete itself once the stock tour child completes, otherwise
        // the contract never finishes.
        protected override void OnParameterStateChange(ContractParameter p)
        {
            if (state == ParameterState.Complete) return;
            if (AllChildParametersComplete()) SetComplete();
        }

        // Complementary self-heal (the stock KerbalTourParameter also fails on
        // onCrewKilled). If our tourist is removed from the roster entirely while
        // the contract is Active, fail so the manager re-offers with a fresh one.
        private void OnKerbalRemoved(ProtoCrewMember pcm)
        {
            if (pcm == null || string.IsNullOrEmpty(ActualName)) return;
            if (pcm.name != ActualName) return;
            if (Root != null && Root.ContractState == Contract.State.Active)
            {
                Debug.Log($"[KSP-AP] tourist '{ActualName}' removed from roster; "
                        + "failing contract to re-offer with a fresh tourist");
                Root.Fail();
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("wire_name", WireName);
            node.AddValue("female", Female);
            node.AddValue("body", BodyName);
            node.AddValue("entry", EntryType.ToString());
            node.AddValue("actual_name", ActualName);
        }

        protected override void OnLoad(ConfigNode node)
        {
            WireName = node.GetValue("wire_name") ?? "";
            bool.TryParse(node.GetValue("female"), out Female);
            BodyName = node.GetValue("body") ?? "";
            string entry = node.GetValue("entry");
            if (!string.IsNullOrEmpty(entry)
                && Enum.TryParse(entry, out FlightLog.EntryType parsed))
                EntryType = parsed;
            ActualName = node.GetValue("actual_name") ?? "";
        }
    }
}
