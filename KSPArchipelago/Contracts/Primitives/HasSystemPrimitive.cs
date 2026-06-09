using System;
using System.Collections.Generic;
using Contracts;
using FinePrint.Contracts.Parameters;
using FinePrint.Utilities;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "has_system", "system": "Generator", "label": "power generation" }</c>
    ///
    /// Maps to stock <see cref="VesselSystemsParameter"/> — the same check stock
    /// "build a base/station" contracts use. <c>system</c> is matched against the
    /// vessel's parts by KSP's contract-objective system (Vessel.HasValid-
    /// ContractObjectives): it accepts either a ContractObjectiveType
    /// (<c>"Generator"</c> = any solar panel / RTG / fuel cell; <c>"Antenna"</c> =
    /// any transmitter) or a PartModule class name (<c>"ModuleScienceLab"</c>,
    /// <c>"ModuleResourceHarvester"</c>). One system per parameter; the generator
    /// emits several for an implicit AND. DLC/mod-robust and self-tracking.
    /// </summary>
    public sealed class HasSystemPrimitive : IContractPrimitive
    {
        public string Kind => "has_system";

        public ContractParameter Build(JObject spec)
        {
            string system = (string)spec["system"];
            if (string.IsNullOrEmpty(system))
                throw new FormatException("has_system primitive missing 'system'");
            string label = (string)spec["label"] ?? system;

            // ctor: (checkModuleTypes, checkModuleDescriptions, vesselDescription,
            //        mannedStatus, requireNew)
            return new VesselSystemsParameter(
                new List<string> { system },
                new List<string> { label },
                label,
                MannedStatus.ANY,
                false);
        }
    }
}
