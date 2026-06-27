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
    /// (VesselUtilities.VesselResourceAmount(name, null)) against goalResource.
    /// The vesselName is display-only here (the title reads "... in your
    /// &lt;vesselName&gt;"); it does NOT create a specific-vessel requirement,
    /// so we pass a generic word rather than leaving it blank.
    /// </summary>
    public sealed class ResourcePrimitive : ContractPrimitiveBase<ResourcePrimitive.Spec>
    {
        public override string Kind => "resource";

        public struct Spec { public string Resource; public double Min; }

        protected override Spec Parse(JObject spec)
        {
            string resource = (string)spec["resource"];
            if (string.IsNullOrEmpty(resource))
                throw new FormatException("resource primitive missing 'resource'");
            JToken minTok = spec["min"];
            if (minTok == null)
                throw new FormatException($"resource primitive for '{resource}' missing 'min'");
            return new Spec { Resource = resource, Min = (double)minTok };
        }

        protected override ContractParameter BuildFrom(Spec p)
            // ctor: (resourceName, resourceTitle, vesselName, goalResource)
            => new ResourcePossessionParameter(p.Resource, p.Resource, "vessel", p.Min);
    }
}
