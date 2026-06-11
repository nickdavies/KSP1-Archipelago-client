using System;
using System.Reflection;

namespace KSPArchipelago
{
    /// <summary>
    /// Single source of truth for the mod's build version. The string is stamped
    /// into the assembly at build time from the git tag (see the Makefile and
    /// .github/workflows/ci.yml) via the AssemblyInformationalVersion attribute.
    /// Unstamped local builds report "dev".
    /// </summary>
    public static class ModVersion
    {
        /// <summary>Build version, e.g. "0.4.3" for a tagged release, "dev" otherwise.</summary>
        public static readonly string Version = Read();

        private static string Read()
        {
            var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                Assembly.GetExecutingAssembly(),
                typeof(AssemblyInformationalVersionAttribute));
            string v = attr?.InformationalVersion;
            return string.IsNullOrEmpty(v) ? "dev" : v;
        }
    }
}
