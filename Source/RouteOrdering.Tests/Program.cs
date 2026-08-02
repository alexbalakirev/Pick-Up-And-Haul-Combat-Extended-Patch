using System;
using System.Collections.Generic;
using System.Reflection;

#if HAS_ROUTE_ORDERING
using PickUpAndHaulCECarryFix;
#endif

internal static class Program
{
    private static int failures;

    private static int Main()
    {
#if HAS_ROUTE_ORDERING
        Run("moving nearest prevents radial zigzag", MovingNearestPreventsRadialZigzag);
        Run("current distance has first precedence", CurrentDistanceHasFirstPrecedence);
        Run("destination distance breaks current-distance ties", DestinationDistanceBreaksTies);
        Run("stable id breaks complete ties", StableIdBreaksCompleteTies);
        Run("nearest unload destination wins", NearestUnloadDestinationWins);
        Run("bounded retention finds complete-key worst", BoundedRetentionFindsCompleteKeyWorst);
        Run("empty candidates return no index", EmptyCandidatesReturnNoIndex);
        Run("candidate cap remains bounded", CandidateCapRemainsBounded);
#else
        Fail("route-ordering production helper is missing");
#endif

#if HAS_HARMONY_TARGET_DISCOVERY
        Run("Harmony target discovery excludes inherited methods", HarmonyTargetDiscoveryExcludesInheritedMethods);
#else
        Fail("Harmony target-discovery production helper is missing");
#endif

#if HAS_ADAPTIVE_STORAGE_RESERVATION_POLICY
        Run("Adaptive Storage uses just-in-time destination reservations", AdaptiveStorageUsesJustInTimeReservations);
        Run("only reservation-blocked Adaptive Storage unloads retry", OnlyReservationBlockedAdaptiveStorageRetries);
        Run("only reservation-blocked Adaptive Storage planning uses fallback", OnlyReservationBlockedAdaptiveStoragePlanningUsesFallback);
#else
        Fail("Adaptive Storage reservation-policy production helper is missing");
#endif

        Console.WriteLine(failures == 0
            ? "All regression tests passed."
            : $"{failures} regression test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception exception)
        {
            Fail($"{name}: {exception.Message}");
        }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {message}");
    }

#if HAS_ROUTE_ORDERING
    private static void MovingNearestPreventsRadialZigzag()
    {
        var remaining = new List<int> { 5, -6, 7 };
        var actual = new List<int>();
        var current = 0;
        const int destination = 20;

        while (remaining.Count > 0)
        {
            var scores = new List<RouteScore>(remaining.Count);
            for (var i = 0; i < remaining.Count; i++)
            {
                var position = remaining[i];
                scores.Add(new RouteScore(
                    Square(position - current),
                    Square(position - destination),
                    position + 100));
            }

            var index = RouteOrdering.FindBestIndex(scores);
            current = remaining[index];
            actual.Add(current);
            remaining.RemoveAt(index);
        }

        AssertSequence(new[] { 5, 7, -6 }, actual);
    }

    private static void CurrentDistanceHasFirstPrecedence()
    {
        var candidate = new RouteScore(4, 100, 20);
        var currentBest = new RouteScore(9, 0, 1);
        AssertTrue(RouteOrdering.IsBetter(candidate, currentBest));
        AssertFalse(RouteOrdering.IsBetter(currentBest, candidate));
    }

    private static void DestinationDistanceBreaksTies()
    {
        var towardStorage = new RouteScore(25, 4, 20);
        var awayFromStorage = new RouteScore(25, 100, 1);
        AssertTrue(RouteOrdering.IsBetter(towardStorage, awayFromStorage));
    }

    private static void StableIdBreaksCompleteTies()
    {
        var lowerId = new RouteScore(25, 25, 10);
        var higherId = new RouteScore(25, 25, 20);
        AssertTrue(RouteOrdering.IsBetter(lowerId, higherId));
    }

    private static void NearestUnloadDestinationWins()
    {
        var scores = new[]
        {
            new RouteScore(100, 0, 10),
            new RouteScore(9, 0, 20),
            new RouteScore(25, 0, 30)
        };

        AssertEqual(1, RouteOrdering.FindBestIndex(scores));
    }

    private static void BoundedRetentionFindsCompleteKeyWorst()
    {
        var method = typeof(RouteOrdering).GetMethod(
            "FindWorstIndex",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException("RouteOrdering.FindWorstIndex is missing.");
        }

        var scores = new[]
        {
            new RouteScore(25, 4, 30),
            new RouteScore(25, 100, 5),
            new RouteScore(25, 100, 20)
        };

        AssertEqual(2, (int)method.Invoke(null, new object[] { scores }));
    }

    private static void EmptyCandidatesReturnNoIndex()
    {
        AssertEqual(-1, RouteOrdering.FindBestIndex(Array.Empty<RouteScore>()));
    }

    private static void CandidateCapRemainsBounded()
    {
        AssertEqual(32, RouteOrdering.Clamp(12, 32, 512));
        AssertEqual(128, RouteOrdering.Clamp(128, 32, 512));
        AssertEqual(512, RouteOrdering.Clamp(900, 32, 512));
    }

    private static int Square(int value) => value * value;

#if HAS_HARMONY_TARGET_DISCOVERY
    private static void HarmonyTargetDiscoveryExcludesInheritedMethods()
    {
        var methods = new List<MethodBase>(
            HarmonyTargetDiscovery.GetDeclaredMethods(
                typeof(HarmonyTargetDiscoveryFixture),
                "MakeNewToils"));

        AssertEqual(2, methods.Count);
        AssertEqual(typeof(HarmonyTargetDiscoveryFixture.MakeNewToilsProbe), methods[0].DeclaringType);
        AssertEqual(typeof(HarmonyTargetDiscoveryFixture.MakeNewToilsProbe), methods[1].DeclaringType);

        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            methodNames.Add(method.Name);
        }

        AssertTrue(methodNames.Contains(nameof(HarmonyTargetDiscoveryFixture.MakeNewToilsProbe.MoveNext)));
        AssertTrue(methodNames.Contains("Reset"));
        AssertFalse(methodNames.Contains(nameof(object.GetType)));
        AssertFalse(methodNames.Contains(nameof(object.Equals)));
    }
#endif


#if HAS_ADAPTIVE_STORAGE_RESERVATION_POLICY
    private static void AdaptiveStorageUsesJustInTimeReservations()
    {
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldReserveDuringGather(typeof(AdaptiveStorage.ThingClass)));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldReserveDuringGather(typeof(DerivedAdaptiveStorage)));
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldReserveDuringGather(typeof(NormalStorage)));
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldReserveDuringGather(null));
    }

    private static void OnlyReservationBlockedAdaptiveStorageRetries()
    {
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldRetryUnload(
            isAdaptiveStorage: true,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldRetryUnload(
            isAdaptiveStorage: true,
            otherwiseEligible: false,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldRetryUnload(
            isAdaptiveStorage: true,
            otherwiseEligible: true,
            canReserveNow: true));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldRetryUnload(
            isAdaptiveStorage: false,
            otherwiseEligible: true,
            canReserveNow: false));
    }

    private static void OnlyReservationBlockedAdaptiveStoragePlanningUsesFallback()
    {
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            priorityEligible: true,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: false,
            priorityEligible: true,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            priorityEligible: false,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            priorityEligible: true,
            otherwiseEligible: false,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            priorityEligible: true,
            otherwiseEligible: true,
            canReserveNow: true));
    }

    private class NormalStorage
    {
    }

    private sealed class DerivedAdaptiveStorage : AdaptiveStorage.ThingClass
    {
    }
#endif

    private static void AssertSequence(IReadOnlyList<int> expected, IReadOnlyList<int> actual)
    {
        AssertEqual(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            AssertEqual(expected[i], actual[i]);
        }
    }

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true, received false.");
        }
    }

    private static void AssertFalse(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false, received true.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
        }
    }
#endif

#if HAS_HARMONY_TARGET_DISCOVERY
    private static class HarmonyTargetDiscoveryFixture
    {
        internal sealed class MakeNewToilsProbe
        {
            public void MoveNext()
            {
            }

            private static void Reset()
            {
            }
        }
    }
#endif
}

#if HAS_ADAPTIVE_STORAGE_RESERVATION_POLICY
namespace AdaptiveStorage
{
    internal class ThingClass
    {
    }
}
#endif
