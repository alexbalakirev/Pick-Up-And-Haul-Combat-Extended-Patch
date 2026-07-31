using System;
using System.Collections.Generic;
using System.Reflection;

namespace PickUpAndHaulCECarryFix
{
    internal static class HarmonyTargetDiscovery
    {
        internal static IEnumerable<MethodBase> GetDeclaredMethods(Type containingType, string nestedTypeNameFragment)
        {
            foreach (var nestedType in containingType.GetNestedTypes(BindingFlags.NonPublic))
            {
                if (nestedType.Name.IndexOf(nestedTypeNameFragment, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                foreach (var method in nestedType.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly))
                {
                    yield return method;
                }
            }
        }
    }
}
