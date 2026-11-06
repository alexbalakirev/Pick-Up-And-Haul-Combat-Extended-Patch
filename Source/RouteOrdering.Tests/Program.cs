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
        Run("reserved planned unload target only wins over worse alternatives", ReservedPlannedUnloadTargetOnlyWinsOverWorseAlternatives);
        Run("only reservation-blocked Adaptive Storage planning uses fallback", OnlyReservationBlockedAdaptiveStoragePlanningUsesFallback);
#else
        Fail("Adaptive Storage reservation-policy production helper is missing");
#endif

#if HAS_CANDIDATE_SELECTION
        Run("lazy selection validates only picked items", LazySelectionValidatesOnlyPickedItems);
        Run("lazy selection respects the validation budget", LazySelectionRespectsValidationBudget);
        Run("rejected candidates are skipped in score order", RejectedCandidatesAreSkippedInScoreOrder);
        Run("lazy selection stops beyond the search distance", LazySelectionStopsBeyondDistance);
        Run("stale candidates do not use validation budget", StaleCandidatesDoNotUseValidationBudget);
        Run("lazy selection rescoring follows the moving center", LazySelectionFollowsMovingCenter);
#else
        Fail("candidate-selection production helper is missing");
#endif

#if HAS_CAPACITY_MATH
        Run("count by dimension handles zero, negative and epsilon cases", CountByDimensionEdgeCases);
        Run("added ratio ignores dimensions without capacity", AddedRatioIgnoresZeroCapacity);
#else
        Fail("capacity-math production helper is missing");
#endif

#if HAS_UNLOAD_DESTINATION_CACHE
        Run("unload cache searches once per distinct item kind", UnloadCacheSearchesOncePerKind);
        Run("unload cache refresh updates the selected kind", UnloadCacheRefreshUpdatesKind);
        Run("unload cache expires entries and skips kinds without a destination", UnloadCacheExpiresAndSkipsNone);
#else
        Fail("unload-destination-cache production helper is missing");
#endif

#if HAS_ADAPTIVE_TYPE_MEMO && HAS_ADAPTIVE_STORAGE_RESERVATION_POLICY
        Run("adaptive type memo matches the uncached result", AdaptiveTypeMemoMatchesUncachedResult);
        Run("adaptive planning reorder keeps every policy truth-table row", PlanningReorderKeepsPolicyTruthTable);
#else
        Fail("adaptive type-memo production helper is missing");
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

    private static void ReservedPlannedUnloadTargetOnlyWinsOverWorseAlternatives()
    {
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldHoldPlannedUnloadTarget(
            isAdaptiveStorage: true,
            otherwiseEligible: true,
            canReserveNow: false,
            hasNormalDestination: false,
            plannedPriority: 3,
            normalPriority: 0));
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldHoldPlannedUnloadTarget(
            isAdaptiveStorage: true,
            otherwiseEligible: true,
            canReserveNow: false,
            hasNormalDestination: true,
            plannedPriority: 3,
            normalPriority: 2));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldHoldPlannedUnloadTarget(
            isAdaptiveStorage: true,
            otherwiseEligible: true,
            canReserveNow: false,
            hasNormalDestination: true,
            plannedPriority: 3,
            normalPriority: 3));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldHoldPlannedUnloadTarget(
            isAdaptiveStorage: true,
            otherwiseEligible: true,
            canReserveNow: false,
            hasNormalDestination: true,
            plannedPriority: 3,
            normalPriority: 4));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldHoldPlannedUnloadTarget(
            isAdaptiveStorage: false,
            otherwiseEligible: true,
            canReserveNow: false,
            hasNormalDestination: false,
            plannedPriority: 3,
            normalPriority: 0));
    }

    private static void OnlyReservationBlockedAdaptiveStoragePlanningUsesFallback()
    {
        AssertTrue(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            candidatePriority: 4,
            currentPriority: 3,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            candidatePriority: 3,
            currentPriority: 3,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            candidatePriority: 2,
            currentPriority: 3,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: false,
            candidatePriority: 4,
            currentPriority: 3,
            otherwiseEligible: true,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            candidatePriority: 4,
            currentPriority: 3,
            otherwiseEligible: false,
            canReserveNow: false));
        AssertFalse(AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
            isAdaptiveStorage: true,
            candidatePriority: 4,
            currentPriority: 3,
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

#if HAS_CANDIDATE_SELECTION
    private sealed class TestCandidate
    {
        public readonly int Id;
        public readonly int Position;

        public TestCandidate(int id, int position)
        {
            Id = id;
            Position = position;
        }
    }

    private static RouteScore ScoreFrom(TestCandidate candidate, int center)
    {
        var delta = candidate.Position - center;
        return new RouteScore(delta * delta, 0, candidate.Id);
    }

    private static LazyCandidateSet<TestCandidate> MakeCandidates(int budget, params int[] positions)
    {
        var set = new LazyCandidateSet<TestCandidate>(budget);
        for (var i = 0; i < positions.Length; i++)
        {
            set.Add(new TestCandidate(i + 1, positions[i]));
        }

        return set;
    }

    private static void LazySelectionValidatesOnlyPickedItems()
    {
        var set = MakeCandidates(128, 3, 9, 1, 7, 5);
        var validated = new List<int>();
        var found = set.TryPickNext(c => ScoreFrom(c, 0), 10000f, null, c => { validated.Add(c.Id); return true; }, out var picked, out var evaluated);
        AssertTrue(found);
        AssertEqual(3, picked.Id);
        AssertEqual(5, evaluated);
        AssertEqual(1, validated.Count);
        AssertEqual(1, set.ValidationCalls);
        AssertEqual(4, set.Count);
    }

    private static void LazySelectionRespectsValidationBudget()
    {
        var set = MakeCandidates(3, 1, 2, 3, 4, 5, 6, 7);
        var calls = 0;
        var found = set.TryPickNext(c => ScoreFrom(c, 0), 10000f, null, c => { calls++; return false; }, out var picked, out _);
        AssertFalse(found);
        AssertTrue(picked == null);
        AssertEqual(3, calls);
        AssertEqual(3, set.ValidationCalls);
        AssertEqual(0, set.RemainingValidationBudget);

        // The budget is per plan, not per pick: a later pick may not validate anything either.
        found = set.TryPickNext(c => ScoreFrom(c, 0), 10000f, null, c => { calls++; return true; }, out _, out _);
        AssertFalse(found);
        AssertEqual(3, calls);
    }

    private static void RejectedCandidatesAreSkippedInScoreOrder()
    {
        var set = MakeCandidates(128, 8, 2, 5, 1, 9);
        var order = new List<int>();
        var found = set.TryPickNext(
            c => ScoreFrom(c, 0),
            10000f,
            null,
            c =>
            {
                order.Add(c.Position);
                return c.Position >= 5;
            },
            out var picked,
            out _);
        AssertTrue(found);
        AssertEqual(5, picked.Position);
        AssertSequence(new[] { 1, 2, 5 }, order);
        AssertEqual(2, set.Count);
    }

    private static void LazySelectionStopsBeyondDistance()
    {
        var set = MakeCandidates(128, 20, 30);
        var calls = 0;
        var found = set.TryPickNext(c => ScoreFrom(c, 0), 15f * 15f, null, c => { calls++; return true; }, out _, out _);
        AssertFalse(found);
        AssertEqual(0, calls);
    }

    private static void StaleCandidatesDoNotUseValidationBudget()
    {
        var set = MakeCandidates(1, 1, 2, 3);
        var found = set.TryPickNext(c => ScoreFrom(c, 0), 10000f, c => c.Position >= 3, c => true, out var picked, out _);
        AssertTrue(found);
        AssertEqual(3, picked.Position);
        AssertEqual(1, set.ValidationCalls);
    }

    private static void LazySelectionFollowsMovingCenter()
    {
        var set = MakeCandidates(128, 10, 12, 1);
        set.TryPickNext(c => ScoreFrom(c, 0), 10000f, null, c => true, out var first, out _);
        AssertEqual(1, first.Position);
        set.TryPickNext(c => ScoreFrom(c, first.Position + 9), 10000f, null, c => true, out var second, out _);
        AssertEqual(10, second.Position);
        set.TryPickNext(c => ScoreFrom(c, second.Position), 10000f, null, c => true, out var third, out _);
        AssertEqual(12, third.Position);
        AssertEqual(0, set.Count);
    }
#endif

#if HAS_CAPACITY_MATH
    private static void CountByDimensionEdgeCases()
    {
        // A dimension that costs nothing never limits the count.
        AssertEqual(7, CapacityMath.CountByDimension(0.5f, 0f, 7));
        AssertEqual(7, CapacityMath.CountByDimension(0.5f, -0.1f, 7));
        // No remaining room.
        AssertEqual(0, CapacityMath.CountByDimension(1f, 0.1f, 7));
        AssertEqual(0, CapacityMath.CountByDimension(1.5f, 0.1f, 7));
        // Epsilon lets a stack that fills the dimension exactly (within float error) still fit.
        AssertEqual(5, CapacityMath.CountByDimension(0.5f, 0.1f, 99));
        AssertEqual(10, CapacityMath.CountByDimension(0f, 0.1f, 99));
        AssertEqual(1, CapacityMath.CountByDimension(0.9f, 0.1f, 99));
        // The planned count clamps the result; non-positive requests yield zero.
        AssertEqual(3, CapacityMath.CountByDimension(0f, 0.01f, 3));
        AssertEqual(0, CapacityMath.CountByDimension(0f, 0.01f, 0));
        AssertEqual(0, CapacityMath.CountByDimension(0f, 0.01f, -4));
        // Less than one unit of room.
        AssertEqual(0, CapacityMath.CountByDimension(0.95f, 0.1f, 99));
    }

    private static void AddedRatioIgnoresZeroCapacity()
    {
        AssertEqual(0f, CapacityMath.AddedRatio(5, 2f, 0f));
        AssertEqual(0f, CapacityMath.AddedRatio(5, 2f, -3f));
        AssertEqual(0.5f, CapacityMath.AddedRatio(5, 1f, 10f));
    }
#endif

#if HAS_UNLOAD_DESTINATION_CACHE
    private sealed class UnloadItem
    {
        public readonly string Kind;
        public readonly int Id;

        public UnloadItem(string kind, int id)
        {
            Kind = kind;
            Id = id;
        }
    }

    private static List<UnloadItem> MakeUnloadItems()
    {
        var items = new List<UnloadItem>();
        var id = 1;
        foreach (var kind in new[] { "steel", "wood", "cloth" })
        {
            for (var i = 0; i < 3; i++)
            {
                items.Add(new UnloadItem(kind, id++));
            }
        }

        return items;
    }

    private static void UnloadCacheSearchesOncePerKind()
    {
        var cache = new UnloadDestinationCache<string>(600);
        var items = MakeUnloadItems();
        var searches = new List<string>();
        var index = cache.SelectNearest(
            items,
            item => item.Kind,
            item => item.Id,
            item =>
            {
                searches.Add(item.Kind);
                switch (item.Kind)
                {
                    case "steel": return new UnloadDestination(true, 50, 0);
                    case "wood": return new UnloadDestination(true, 4, 3);
                    default: return new UnloadDestination(true, 20, 0);
                }
            },
            0,
            0,
            100);
        AssertEqual(3, searches.Count);
        AssertEqual(3, cache.Searches);
        AssertEqual("wood", items[index].Kind);
        // Ties between items of the same kind go to the lowest stable id.
        AssertEqual(4, items[index].Id);

        // A second selection in the same job reuses every entry.
        cache.SelectNearest(items, item => item.Kind, item => item.Id, item => throw new InvalidOperationException("unexpected search"), 0, 0, 101);
        AssertEqual(3, cache.Searches);
    }

    private static void UnloadCacheRefreshUpdatesKind()
    {
        var cache = new UnloadDestinationCache<string>(600);
        var items = MakeUnloadItems();
        UnloadDestination Search(UnloadItem item)
            => item.Kind == "wood" ? new UnloadDestination(true, 4, 3) : new UnloadDestination(true, 90, 0);

        var first = cache.SelectNearest(items, item => item.Kind, item => item.Id, Search, 0, 0, 10);
        AssertEqual("wood", items[first].Kind);

        // The toil's own search found that wood now has to go far away; steel/cloth become nearer.
        cache.Refresh("wood", new UnloadDestination(true, 200, 0), 11);
        var second = cache.SelectNearest(items, item => item.Kind, item => item.Id, item => throw new InvalidOperationException("unexpected search"), 0, 0, 12);
        AssertEqual("steel", items[second].Kind);
        AssertEqual(3, cache.Searches);
    }

    private static void UnloadCacheExpiresAndSkipsNone()
    {
        var cache = new UnloadDestinationCache<string>(50);
        var items = MakeUnloadItems();
        var searchCount = 0;
        UnloadDestination Search(UnloadItem item)
        {
            searchCount++;
            return item.Kind == "cloth" ? UnloadDestination.None : new UnloadDestination(true, item.Kind == "steel" ? 8 : 3, 0);
        }

        var first = cache.SelectNearest(items, item => item.Kind, item => item.Id, Search, 0, 0, 0);
        AssertEqual("wood", items[first].Kind);
        AssertEqual(3, searchCount);

        // Entries expire after the maximum age, so each kind is searched again.
        cache.SelectNearest(items, item => item.Kind, item => item.Id, Search, 0, 0, 51);
        AssertEqual(6, searchCount);

        var none = new UnloadDestinationCache<string>(50);
        var onlyCloth = new List<UnloadItem> { new UnloadItem("cloth", 1), new UnloadItem("cloth", 2) };
        AssertEqual(-1, none.SelectNearest(onlyCloth, item => item.Kind, item => item.Id, Search, 0, 0, 0));
        AssertEqual(-1, none.SelectNearest(new List<UnloadItem>(), item => item.Kind, item => item.Id, Search, 0, 0, 0));
    }
#endif

#if HAS_ADAPTIVE_TYPE_MEMO && HAS_ADAPTIVE_STORAGE_RESERVATION_POLICY
    private static void AdaptiveTypeMemoMatchesUncachedResult()
    {
        var computeCalls = 0;
        var memo = new TypeMemo(type =>
        {
            computeCalls++;
            return AdaptiveStorageReservationPolicy.IsAdaptiveStorageType(type);
        });

        var types = new[]
        {
            typeof(AdaptiveStorage.ThingClass),
            typeof(DerivedAdaptiveStorage),
            typeof(string),
            typeof(object),
            typeof(List<int>),
            null
        };

        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var type in types)
            {
                AssertEqual(AdaptiveStorageReservationPolicy.IsAdaptiveStorageType(type), memo.Get(type));
            }
        }

        // Each non-null type is computed once; null is never cached.
        AssertEqual(5 + 3, computeCalls);
        AssertEqual(5, memo.Count);
    }

    private static void PlanningReorderKeepsPolicyTruthTable()
    {
        var bools = new[] { false, true };
        foreach (var isAdaptive in bools)
        {
            foreach (var otherwiseEligible in bools)
            {
                foreach (var canReserveNow in bools)
                {
                    for (var candidate = 1; candidate <= 3; candidate++)
                    {
                        for (var current = 1; current <= 3; current++)
                        {
                            var original = AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
                                isAdaptive, candidate, current, otherwiseEligible, canReserveNow);

                            // Group-level rejection, then the unreservable-first cell check, then the policy for the rest.
                            var reordered = !(candidate <= current || !isAdaptive)
                                && !canReserveNow
                                && AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(true, candidate, current, otherwiseEligible, false);
                            AssertEqual(original, reordered);
                        }
                    }
                }
            }
        }
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
