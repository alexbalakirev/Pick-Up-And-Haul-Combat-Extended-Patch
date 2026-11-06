using System;
using System.Collections.Generic;
using System.Reflection;

namespace PickUpAndHaulCECarryFix
{
    internal static class HarmonyTargetDiscovery
    {
        private const BindingFlags DeclaredMethodFlags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Methods compiled out of <paramref name="fragment"/> (for example MakeNewToils): every declared method of a nested type
        /// whose name contains the fragment (the iterator state machine), plus compiler-generated lambdas named
        /// "&lt;fragment&gt;b__N" in any directly nested type (closure display classes and the "&lt;&gt;c" cache class) and on
        /// <paramref name="containingType"/> itself (lambdas that capture only this). Each method is returned once.
        /// </summary>
        internal static IEnumerable<MethodBase> GetDeclaredMethods(Type containingType, string nestedTypeNameFragment)
        {
            var seen = new HashSet<MethodBase>();
            var lambdaMarker = "<" + nestedTypeNameFragment + ">";

            foreach (var nestedType in containingType.GetNestedTypes(BindingFlags.NonPublic))
            {
                var wholeType = nestedType.Name.IndexOf(nestedTypeNameFragment, StringComparison.Ordinal) >= 0;
                foreach (var method in nestedType.GetMethods(DeclaredMethodFlags))
                {
                    if ((wholeType || method.Name.IndexOf(lambdaMarker, StringComparison.Ordinal) >= 0) && seen.Add(method))
                    {
                        yield return method;
                    }
                }
            }

            foreach (var method in containingType.GetMethods(DeclaredMethodFlags))
            {
                if (method.Name.IndexOf(lambdaMarker, StringComparison.Ordinal) >= 0 && seen.Add(method))
                {
                    yield return method;
                }
            }
        }
    }
}
