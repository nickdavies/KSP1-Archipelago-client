using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Boolean expression over AP location-checked predicates. Used by
    /// ContractSlotSpec.Requires to gate when a contract slot becomes
    /// offerable based on which AP locations the player has already
    /// completed.
    ///
    /// Wire format (matches the slot_data emitted by the generator):
    ///   - JSON string                       => leaf, location name
    ///   - { "all": [ &lt;child&gt;, ... ] }       => AND
    ///   - { "any": [ &lt;child&gt;, ... ] }       => OR
    ///   - { "not": &lt;child&gt; }                => NOT
    ///   - null or missing                  => empty, always satisfied
    ///
    /// Children may be any of the above (recursive). Empty all/any lists
    /// follow vacuous-truth conventions: empty all = true, empty any = false.
    /// </summary>
    public sealed class ContractRequirement
    {
        public enum Kind { Leaf, All, Any, Not, Always }

        public Kind Type { get; private set; }
        public string LocationName { get; private set; } // only set when Type==Leaf
        public IList<ContractRequirement> Children { get; private set; }

        public static readonly ContractRequirement AlwaysSatisfied =
            new ContractRequirement { Type = Kind.Always };

        public bool IsSatisfied(Func<string, bool> isLocationChecked)
        {
            switch (Type)
            {
                case Kind.Always: return true;
                case Kind.Leaf:   return isLocationChecked(LocationName);
                case Kind.Not:    return !Children[0].IsSatisfied(isLocationChecked);
                case Kind.All:
                    foreach (var c in Children)
                        if (!c.IsSatisfied(isLocationChecked)) return false;
                    return true; // empty all => vacuously true
                case Kind.Any:
                    foreach (var c in Children)
                        if (c.IsSatisfied(isLocationChecked)) return true;
                    return false; // empty any => vacuously false
                default:
                    throw new InvalidOperationException("Unhandled kind: " + Type);
            }
        }

        /// <summary>
        /// Build a tree from a JToken. Returns AlwaysSatisfied for null/missing.
        /// Throws FormatException on malformed input — callers should let
        /// it propagate so generation-side bugs are visible, not silent.
        /// </summary>
        public static ContractRequirement Parse(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return AlwaysSatisfied;

            if (token.Type == JTokenType.String)
            {
                string name = token.Value<string>();
                if (string.IsNullOrEmpty(name))
                    throw new FormatException("ContractRequirement leaf is empty string");
                return new ContractRequirement
                {
                    Type = Kind.Leaf,
                    LocationName = name,
                };
            }

            if (token.Type != JTokenType.Object)
                throw new FormatException(
                    $"ContractRequirement node must be string or object, got {token.Type}");

            JObject obj = (JObject)token;
            // Exactly one of all/any/not should be present.
            int present = 0;
            if (obj["all"] != null) present++;
            if (obj["any"] != null) present++;
            if (obj["not"] != null) present++;
            if (present != 1)
                throw new FormatException(
                    "ContractRequirement object must have exactly one of "
                    + "'all', 'any', or 'not' (got " + present + ")");

            if (obj["not"] is JToken notTok)
            {
                return new ContractRequirement
                {
                    Type = Kind.Not,
                    Children = new[] { Parse(notTok) },
                };
            }

            string key = obj["all"] != null ? "all" : "any";
            Kind kind = key == "all" ? Kind.All : Kind.Any;
            JToken childrenTok = obj[key];
            if (childrenTok.Type != JTokenType.Array)
                throw new FormatException(
                    $"ContractRequirement '{key}' must be an array, got {childrenTok.Type}");

            var children = new List<ContractRequirement>();
            foreach (JToken c in (JArray)childrenTok)
                children.Add(Parse(c));
            return new ContractRequirement
            {
                Type = kind,
                Children = children,
            };
        }

        public override string ToString()
        {
            switch (Type)
            {
                case Kind.Always: return "<always>";
                case Kind.Leaf:   return LocationName;
                case Kind.Not:    return "!" + Children[0];
                case Kind.All:
                case Kind.Any:
                {
                    var op = Type == Kind.All ? " && " : " || ";
                    return "(" + string.Join(op, ChildStrings()) + ")";
                }
                default:
                    return "<?>";
            }
        }

        private string[] ChildStrings()
        {
            var arr = new string[Children.Count];
            for (int i = 0; i < Children.Count; i++) arr[i] = Children[i].ToString();
            return arr;
        }
    }
}
