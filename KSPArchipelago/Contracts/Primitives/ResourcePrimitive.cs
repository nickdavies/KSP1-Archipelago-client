using System;
using Contracts;
using FinePrint.Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "resource", "resource": "Ore", "min": 100 }</c>
    ///
    /// Maps to stock <see cref="ResourcePossessionParameter"/>: completes when
    /// the active vessel holds at least <c>min</c> units of the named resource.
    /// Verified standalone — its OnUpdate counts the active vessel's resource
    /// (VesselUtilities.VesselResourceAmount(name, null)) against goalResource;
    /// the empty vesselName means "no specific vessel designated".
    /// </summary>
    public sealed class ResourcePrimitive : IContractPrimitive
    {
        public string Kind => "resource";

        public ContractParameter Build(JObject spec)
        {
            string resource = (string)spec["resource"];
            if (string.IsNullOrEmpty(resource))
                throw new FormatException("resource primitive missing 'resource'");

            JToken minTok = spec["min"];
            if (minTok == null)
                throw new FormatException(
                    $"resource primitive for '{resource}' missing 'min'");
            double min = (double)minTok;

            // ctor: (resourceName, resourceTitle, vesselName, goalResource)
            return new ResourcePossessionParameter(resource, resource, "", min);
        }
    }
}
