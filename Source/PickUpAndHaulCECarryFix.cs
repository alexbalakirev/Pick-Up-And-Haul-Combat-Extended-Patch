using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using PickUpAndHaul;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace PickUpAndHaulCECarryFix
{
    public class PickUpAndHaulCECarryFixMod : Mod
    {
        public static PickUpAndHaulCECarryFixSettings Settings;

        public PickUpAndHaulCECarryFixMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PickUpAndHaulCECarryFixSettings>();
        }

        public override string SettingsCategory() => "PUAH CE Carry Fix";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled(
                "Enable candidate planning performance patch",
                ref Settings.PerformancePatchEnabled,
                "Use a bounded opportunistic pickup candidate list instead of Pick Up And Haul's full-list sort and repeated nearest searches.");
            if (Settings.PerformancePatchEnabled)
            {
                listing.Label($"Opportunistic candidate cap: {Settings.OpportunisticCandidateCap}");
                Settings.OpportunisticCandidateCap = (int)Math.Round(listing.Slider(Settings.OpportunisticCandidateCap, 32f, 512f));
            }

            listing.Gap();
            listing.CheckboxLabeled(
                "Enable nearest-destination unload routing",
                ref Settings.UnloadRouteOptimizationEnabled,
                "Choose the hauled inventory item whose currently valid storage destination is nearest.");

            listing.Gap();
            listing.CheckboxLabeled(
                "Enable storage and hotpath cleanup",
                ref Settings.StorageHotpathCleanupEnabled,
                "Cache per-job storage scan candidates and avoid a few Pick Up And Haul hot-path allocations.");

            listing.Gap();
            listing.CheckboxLabeled(
                "Enable diagnostics logging",
                ref Settings.DiagnosticsEnabled,
                "Log one compact Pick Up And Haul planning summary for each JobOnThing call.");
            listing.End();
        }
    }

    public class PickUpAndHaulCECarryFixSettings : ModSettings
    {
        public bool PerformancePatchEnabled = true;
        public int OpportunisticCandidateCap = PickUpAndHaulPerformance.DefaultCandidateCap;
        public bool UnloadRouteOptimizationEnabled = true;
        public bool StorageHotpathCleanupEnabled = true;
        public bool DiagnosticsEnabled;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref PerformancePatchEnabled, "performancePatchEnabled", true);
            Scribe_Values.Look(ref OpportunisticCandidateCap, "opportunisticCandidateCap", PickUpAndHaulPerformance.DefaultCandidateCap);
            Scribe_Values.Look(ref UnloadRouteOptimizationEnabled, "unloadRouteOptimizationEnabled", true);
            Scribe_Values.Look(ref StorageHotpathCleanupEnabled, "storageHotpathCleanupEnabled", true);
            Scribe_Values.Look(ref DiagnosticsEnabled, "diagnosticsEnabled", false);
            OpportunisticCandidateCap = PickUpAndHaulPerformance.ClampCandidateCap(OpportunisticCandidateCap);
        }
    }

    public static class PickUpAndHaulPerformance
    {
        public const int DefaultCandidateCap = 128;
        private const int MinCandidateCap = 32;
        private const int MaxCandidateCap = 512;
        private const float SearchForOthersRangeFraction = 0.5f;

        public static bool Enabled => PickUpAndHaulCECarryFixMod.Settings?.PerformancePatchEnabled == true;
        public static int CandidateCap => ClampCandidateCap(PickUpAndHaulCECarryFixMod.Settings?.OpportunisticCandidateCap ?? DefaultCandidateCap);

        public static int ClampCandidateCap(int value)
            => RouteOrdering.Clamp(value, MinCandidateCap, MaxCandidateCap);

        public static IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            if (pawn?.Map?.listerHaulables == null)
            {
                return Enumerable.Empty<Thing>();
            }

            var list = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
            var rootCell = pawn.Position;
            list.Sort((left, right) => (left.Position - rootCell).LengthHorizontalSquared.CompareTo((right.Position - rootCell).LengthHorizontalSquared));
            return list;
        }

        public static Job TryMakeOptimizedJob(Pawn pawn, Thing thing, bool forced)
        {
            if (!Enabled)
            {
                return null;
            }

            if (!WorkGiver_HaulToInventory.OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
            {
                return null;
            }

            if (WorkGiver_HaulToInventory.OverAllowedGearCapacity(pawn)
                || pawn.GetComp<CompHauledToInventory>() is null
                || !WorkGiver_HaulToInventory.IsNotCorpseOrAllowed(thing)
                || CeInventoryUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1))
            {
                return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
            }

            var map = pawn.Map;
            var designationManager = map.designationManager;
            var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
            ThingOwner nonSlotGroupThingOwner = null;
            WorkGiver_HaulToInventory.StoreTarget storeTarget;
            IntVec3 targetCell;
            IHaulDestination haulDestination;
            var foundStorage = StoreUtility.TryFindBestBetterStorageFor(
                thing,
                pawn,
                map,
                currentPriority,
                pawn.Faction,
                out targetCell,
                out haulDestination,
                true);
            if (!foundStorage)
            {
                foundStorage = AdaptiveStorageReservationCompatibility.TryFindPlanningFallback(
                    thing,
                    pawn,
                    currentPriority,
                    logSelection: true,
                    out targetCell,
                    out haulDestination);
            }

            if (foundStorage)
            {
                if (haulDestination is ISlotGroupParent)
                {
                    if (HaulToHopperJob(thing, targetCell, map))
                    {
                        return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
                    }

                    storeTarget = new WorkGiver_HaulToInventory.StoreTarget(targetCell);
                }
                else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
                {
                    storeTarget = new WorkGiver_HaulToInventory.StoreTarget(destinationAsThing);
                }
                else
                {
                    Log.Error("Don't know how to handle HaulToStorageJob for storage " + haulDestination.ToStringSafe() + ". thing=" + thing.ToStringSafe());
                    return null;
                }
            }
            else
            {
                JobFailReason.Is("NoEmptyPlaceLower".Translate());
                return null;
            }

            var capacityStoreCell = storeTarget.container is null
                ? WorkGiver_HaulToInventory.CapacityAt(thing, storeTarget.cell, map)
                : nonSlotGroupThingOwner.GetCountCanAccept(thing);

            if (capacityStoreCell == 0)
            {
                return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
            }

            var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory, null, storeTarget);
            if (PickUpAndHaulDiagnostics.Enabled)
            {
                Log.Message($"-------------------------------------------------------------------");
                Log.Message($"------------------------------------------------------------------");
                Log.Message($"{pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}, looking for more now");
            }

            var nextThingLeftOverCount = 0;
            var encumberance = CeInventoryUtility.EncumbrancePercent(pawn);
            var plannedCapacity = CeInventoryUtility.PlannedCapacity.For(pawn);
            job.targetQueueA = new List<LocalTargetInfo>();
            job.targetQueueB = new List<LocalTargetInfo>();
            job.countQueue = new List<int>();

            var ceOverweight = false;
            if (ModCompatibilityCheck.CombatExtendedIsActive)
            {
                ceOverweight = CeInventoryUtility.OverAllowedCapacity(pawn);
            }

            var distanceToHaul = (storeTarget.Position - thing.Position).LengthHorizontal * SearchForOthersRangeFraction;
            var distanceToSearchMore = Math.Max(12f, distanceToHaul);

            var haulUrgentlyDesignation = PickUpAndHaulDesignationDefOf.haulUrgently;
            var isUrgent = ModCompatibilityCheck.AllowToolIsActive && designationManager.DesignationOn(thing)?.def == haulUrgentlyDesignation;

            bool Validator(Thing t)
                => (!isUrgent || designationManager.DesignationOn(t)?.def == haulUrgentlyDesignation)
                && WorkGiver_HaulToInventory.GoodThingToHaul(t, pawn)
                && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false);

            var haulables = BuildCandidateList(map, thing, storeTarget.Position, distanceToSearchMore, CandidateCap, Validator);
            var routeScores = new List<RouteScore>(haulables.Count);

            var nextThing = thing;
            var lastThing = thing;

            var storeCellCapacity = new Dictionary<WorkGiver_HaulToInventory.StoreTarget, WorkGiver_HaulToInventory.CellAllocation>
            {
                [storeTarget] = new WorkGiver_HaulToInventory.CellAllocation(nextThing, capacityStoreCell)
            };

            WorkGiver_HaulToInventory.skipCells = new HashSet<IntVec3>();
            WorkGiver_HaulToInventory.skipThings = new HashSet<Thing>();
            if (storeTarget.container != null)
            {
                WorkGiver_HaulToInventory.skipThings.Add(storeTarget.container);
            }
            else
            {
                WorkGiver_HaulToInventory.skipCells.Add(storeTarget.cell);
            }

            try
            {
                do
                {
                    if (WorkGiver_HaulToInventory.AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job))
                    {
                        lastThing = nextThing;
                        var plannedCount = CeInventoryUtility.LastPlannedCount(job, nextThing);
                        if (plannedCapacity.UsesCeInventory)
                        {
                            var countThatFits = plannedCapacity.CountThatFits(pawn, nextThing, plannedCount);
                            if (countThatFits < plannedCount || ceOverweight)
                            {
                                if (countThatFits <= 0 && job.targetQueueA.Count > 1)
                                {
                                    job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
                                    job.countQueue.RemoveAt(job.countQueue.Count - 1);
                                    break;
                                }

                                nextThingLeftOverCount = Math.Max(1, countThatFits);
                                job.countQueue.Pop();
                                job.countQueue.Add(nextThingLeftOverCount);
                                if (PickUpAndHaulDiagnostics.Enabled)
                                {
                                    Log.Message($"Inventory allocated, will carry {nextThing}:{nextThingLeftOverCount}");
                                }

                                break;
                            }

                            plannedCapacity.Add(nextThing, plannedCount);
                            encumberance = plannedCapacity.CapacityRatio;
                        }
                        else
                        {
                            encumberance += CeInventoryUtility.AddedEncumbrance(pawn, nextThing);
                        }

                        if (encumberance > 1 || ceOverweight)
                        {
                            nextThingLeftOverCount = WorkGiver_HaulToInventory.CountPastCapacity(pawn, nextThing, encumberance);
                            job.countQueue.Pop();
                            job.countQueue.Add(nextThingLeftOverCount);
                            if (PickUpAndHaulDiagnostics.Enabled)
                            {
                                Log.Message($"Inventory allocated, will carry {nextThing}:{nextThingLeftOverCount}");
                            }
                            break;
                        }
                    }
                }
                while ((nextThing = GetNextCandidate(lastThing.Position, storeTarget.Position, map, haulables, routeScores,
                    PathEndMode.ClosestTouch, TraverseParms.For(pawn), distanceToSearchMore, Validator)) != null);
            }
            finally
            {
                WorkGiver_HaulToInventory.skipCells = null;
                WorkGiver_HaulToInventory.skipThings = null;
            }

            return job;
        }

        private static List<Thing> BuildCandidateList(
            Map map,
            Thing firstThing,
            IntVec3 destination,
            float maxDistance,
            int cap,
            Predicate<Thing> validator)
        {
            var candidates = new List<Candidate>(cap);
            var candidateScores = new List<RouteScore>(cap);
            var maxDistanceSquared = maxDistance * maxDistance;
            var worstIndex = -1;

            foreach (var candidate in map.listerHaulables.ThingsPotentiallyNeedingHauling())
            {
                if (candidate == null || candidate == firstThing || !candidate.Spawned)
                {
                    continue;
                }

                var distanceSquared = (firstThing.Position - candidate.Position).LengthHorizontalSquared;
                if (distanceSquared > maxDistanceSquared)
                {
                    continue;
                }

                if (validator != null && !validator(candidate))
                {
                    continue;
                }

                var score = new RouteScore(
                    distanceSquared,
                    (destination - candidate.Position).LengthHorizontalSquared,
                    candidate.thingIDNumber);

                if (candidates.Count < cap)
                {
                    candidates.Add(new Candidate(candidate, score));
                    candidateScores.Add(score);
                    if (worstIndex < 0 || RouteOrdering.Compare(score, candidateScores[worstIndex]) > 0)
                    {
                        worstIndex = candidates.Count - 1;
                    }

                    continue;
                }

                if (!RouteOrdering.IsBetter(score, candidateScores[worstIndex]))
                {
                    continue;
                }

                candidates[worstIndex] = new Candidate(candidate, score);
                candidateScores[worstIndex] = score;
                worstIndex = RouteOrdering.FindWorstIndex(candidateScores);
            }

            candidates.Sort((left, right) => RouteOrdering.Compare(left.Score, right.Score));

            var result = new List<Thing>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                result.Add(candidates[i].Thing);
            }

            return result;
        }

        private static Thing GetNextCandidate(
            IntVec3 center,
            IntVec3 destination,
            Map map,
            List<Thing> searchSet,
            List<RouteScore> routeScores,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator)
        {
            if (searchSet.NullOrEmpty())
            {
                return null;
            }

            var maxDistanceSquared = maxDistance * maxDistance;
            var evaluated = 0;
            while (searchSet.Count > 0)
            {
                for (var i = searchSet.Count - 1; i >= 0; i--)
                {
                    if (searchSet[i] == null || !searchSet[i].Spawned)
                    {
                        searchSet.RemoveAt(i);
                    }
                }

                if (searchSet.Count == 0)
                {
                    break;
                }

                routeScores.Clear();
                for (var i = 0; i < searchSet.Count; i++)
                {
                    var scoredCandidate = searchSet[i];
                    routeScores.Add(new RouteScore(
                        (center - scoredCandidate.Position).LengthHorizontalSquared,
                        (destination - scoredCandidate.Position).LengthHorizontalSquared,
                        scoredCandidate.thingIDNumber));
                }

                evaluated += routeScores.Count;
                var bestIndex = RouteOrdering.FindBestIndex(routeScores);
                if (bestIndex < 0)
                {
                    break;
                }

                var bestScore = routeScores[bestIndex];
                var candidate = searchSet[bestIndex];
                searchSet.RemoveAt(bestIndex);
                if (bestScore.CurrentDistanceSquared > maxDistanceSquared)
                {
                    break;
                }

                if (!map.reachability.CanReach(center, candidate, peMode, traverseParams))
                {
                    continue;
                }

                if (validator == null || validator(candidate))
                {
                    PickUpAndHaulDiagnostics.CountNearestSearch(evaluated);
                    return candidate;
                }
            }

            PickUpAndHaulDiagnostics.CountNearestSearch(evaluated);
            return null;
        }

        private static bool HaulToHopperJob(Thing thing, IntVec3 targetCell, Map map)
        {
            if (thing.def.IsNutritionGivingIngestible
                && thing.def.ingestible.preferability is FoodPreferability.RawBad or FoodPreferability.RawTasty)
            {
                var thingList = targetCell.GetThingList(map);
                for (var i = 0; i < thingList.Count; i++)
                {
                    if (thingList[i].def == ThingDefOf.Hopper)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private readonly struct Candidate
        {
            public readonly Thing Thing;
            public readonly RouteScore Score;

            public Candidate(Thing thing, RouteScore score)
            {
                Thing = thing;
                Score = score;
            }
        }
    }

    public static class PickUpAndHaulUnloadRouting
    {
        public static bool Enabled => PickUpAndHaulCECarryFixMod.Settings?.UnloadRouteOptimizationEnabled == true;

        public static bool TrySelectNextUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings, out ThingCount result)
        {
            result = default;
            var inventory = pawn?.inventory?.innerContainer;
            if (!Enabled || inventory == null || carriedThings == null || carriedThings.Count == 0 || pawn.Map == null)
            {
                return false;
            }

            var trackedSnapshot = new List<Thing>(carriedThings);
            var candidates = new List<UnloadCandidate>(trackedSnapshot.Count);
            var scores = new List<RouteScore>(trackedSnapshot.Count);
            var seenActualThings = new HashSet<Thing>();

            for (var i = 0; i < trackedSnapshot.Count; i++)
            {
                var trackedThing = trackedSnapshot[i];
                if (trackedThing == null)
                {
                    continue;
                }

                var actualThing = trackedThing;
                var isReplacement = !inventory.Contains(actualThing);
                if (isReplacement)
                {
                    actualThing = null;
                    for (var j = 0; j < inventory.Count; j++)
                    {
                        var inventoryThing = inventory[j];
                        if (inventoryThing.def == trackedThing.def && !seenActualThings.Contains(inventoryThing))
                        {
                            actualThing = inventoryThing;
                            break;
                        }
                    }
                }

                if (actualThing == null || !seenActualThings.Add(actualThing))
                {
                    continue;
                }

                if (!StoreUtility.TryFindBestBetterStorageFor(
                    actualThing,
                    pawn,
                    pawn.Map,
                    StoragePriority.Unstored,
                    pawn.Faction,
                    out var storageCell,
                    out var haulDestination))
                {
                    continue;
                }

                var destination = storageCell != IntVec3.Invalid
                    ? storageCell
                    : haulDestination?.Position ?? IntVec3.Invalid;
                if (destination == IntVec3.Invalid)
                {
                    continue;
                }

                candidates.Add(new UnloadCandidate(trackedThing, actualThing, destination, isReplacement));
                scores.Add(new RouteScore(
                    (pawn.Position - destination).LengthHorizontalSquared,
                    0,
                    actualThing.thingIDNumber));
            }

            var bestIndex = RouteOrdering.FindBestIndex(scores);
            if (bestIndex < 0)
            {
                return false;
            }

            var selected = candidates[bestIndex];
            if (selected.IsReplacement)
            {
                carriedThings.Remove(selected.TrackedThing);
            }

            result = new ThingCount(selected.ActualThing, selected.ActualThing.stackCount);
            if (PickUpAndHaulDiagnostics.Enabled)
            {
                Log.Message(
                    $"[Pick Up And Haul CE Carry Fix] unload route pawn={pawn} item={selected.ActualThing} destination={selected.Destination} distanceSquared={scores[bestIndex].CurrentDistanceSquared} candidates={candidates.Count}");
            }

            return true;
        }

        private readonly struct UnloadCandidate
        {
            public readonly Thing TrackedThing;
            public readonly Thing ActualThing;
            public readonly IntVec3 Destination;
            public readonly bool IsReplacement;

            public UnloadCandidate(Thing trackedThing, Thing actualThing, IntVec3 destination, bool isReplacement)
            {
                TrackedThing = trackedThing;
                ActualThing = actualThing;
                Destination = destination;
                IsReplacement = isReplacement;
            }
        }
    }

    public static class PickUpAndHaulStorageHotpath
    {
        [ThreadStatic]
        private static PlanningStorageContext current;

        private static WorkGiver_HaulToInventory cachedHaulToInventoryWorkGiver;
        private static WorkGiverDef cachedHaulToInventoryWorkGiverDef;
        private static bool lookedForHaulToInventoryWorkGiver;

        public static bool Enabled => PickUpAndHaulCECarryFixMod.Settings?.StorageHotpathCleanupEnabled == true;
        public static bool HasContext => Enabled && current != null;

        public static void BeginPlanning()
        {
            if (!Enabled)
            {
                return;
            }

            current = new PlanningStorageContext(current);
        }

        public static void EndPlanning()
        {
            if (current != null)
            {
                current = current.Parent;
            }
        }

        public static bool TryAllocateThingAtCell(
            Dictionary<WorkGiver_HaulToInventory.StoreTarget, WorkGiver_HaulToInventory.CellAllocation> storeCellCapacity,
            Pawn pawn,
            Thing nextThing,
            Job job,
            out bool result)
        {
            result = false;
            if (!HasContext || storeCellCapacity == null || pawn?.Map == null || nextThing == null || job == null)
            {
                return false;
            }

            result = current.AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job);
            return true;
        }

        public static WorkGiver_HaulToInventory ResolveHaulToInventoryWorkGiver()
        {
            if (lookedForHaulToInventoryWorkGiver)
            {
                return cachedHaulToInventoryWorkGiver;
            }

            lookedForHaulToInventoryWorkGiver = true;
            var allDefs = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (var i = 0; i < allDefs.Count; i++)
            {
                if (allDefs[i].Worker is WorkGiver_HaulToInventory worker)
                {
                    cachedHaulToInventoryWorkGiverDef = allDefs[i];
                    cachedHaulToInventoryWorkGiver = worker;
                    break;
                }
            }

            if (cachedHaulToInventoryWorkGiver == null)
            {
                Log.Warning("[Pick Up And Haul CE Carry Fix] Could not resolve cached HaulToInventory workgiver.");
            }

            return cachedHaulToInventoryWorkGiver;
        }

        public static WorkGiverDef ResolveHaulToInventoryWorkGiverDef(IEnumerable<WorkGiverDef> defs, Func<WorkGiverDef, bool> predicate)
        {
            if (!Enabled)
            {
                return defs.First(predicate);
            }

            if (cachedHaulToInventoryWorkGiverDef != null || ResolveHaulToInventoryWorkGiver() != null)
            {
                return cachedHaulToInventoryWorkGiverDef;
            }

            return defs.First(predicate);
        }

        public static Thing GetClosestAndRemoveWithoutLinq(
            IntVec3 center,
            Map map,
            List<Thing> searchSet,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator)
        {
            if (searchSet == null || searchSet.Count == 0)
            {
                return null;
            }

            var maxDistanceSquared = maxDistance * maxDistance;

            while (FindClosestThingWithoutLinq(searchSet, center, out var index) is { } closestThing)
            {
                searchSet.RemoveAt(index);
                if (!closestThing.Spawned)
                {
                    continue;
                }

                if ((center - closestThing.Position).LengthHorizontalSquared > maxDistanceSquared)
                {
                    break;
                }

                if (!map.reachability.CanReach(center, closestThing, peMode, traverseParams))
                {
                    continue;
                }

                if (validator == null || validator(closestThing))
                {
                    return closestThing;
                }
            }

            return null;
        }

        public static Thing FindClosestThingWithoutLinq(List<Thing> searchSet, IntVec3 center, out int index)
        {
            if (searchSet == null || searchSet.Count == 0)
            {
                index = -1;
                return null;
            }

            var closestThing = searchSet[0];
            index = 0;
            var closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
            var count = searchSet.Count;
            for (var i = 1; i < count; i++)
            {
                var distanceSquared = (center - searchSet[i].Position).LengthHorizontalSquared;
                if (closestThingSquaredLength > distanceSquared)
                {
                    closestThing = searchSet[i];
                    index = i;
                    closestThingSquaredLength = distanceSquared;
                }
            }

            return closestThing;
        }

        private class PlanningStorageContext
        {
            public readonly PlanningStorageContext Parent;
            private readonly Dictionary<StorageCacheKey, List<SlotCellCandidate>> slotCellCache = new Dictionary<StorageCacheKey, List<SlotCellCandidate>>();
            private readonly Dictionary<StorageCacheKey, List<Thing>> nonSlotThingCache = new Dictionary<StorageCacheKey, List<Thing>>();

            public PlanningStorageContext(PlanningStorageContext parent)
            {
                Parent = parent;
            }

            public bool AllocateThingAtCell(
                Dictionary<WorkGiver_HaulToInventory.StoreTarget, WorkGiver_HaulToInventory.CellAllocation> storeCellCapacity,
                Pawn pawn,
                Thing nextThing,
                Job job)
            {
                var map = pawn.Map;
                var storeCell = default(WorkGiver_HaulToInventory.StoreTarget);
                var foundExistingAllocation = false;

                foreach (var allocation in storeCellCapacity)
                {
                    var storeTarget = allocation.Key;
                    var accepts = storeTarget.container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
                        ?? storeTarget.cell.GetSlotGroup(map)?.parent?.Accepts(nextThing) == true;
                    if (accepts && WorkGiver_HaulToInventory.Stackable(nextThing, allocation))
                    {
                        storeCell = storeTarget;
                        foundExistingAllocation = true;
                        break;
                    }
                }

                if (!foundExistingAllocation)
                {
                    var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
                    if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, false, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
                    {
                        if (innerInteractableThingOwner is null)
                        {
                            storeCell = new WorkGiver_HaulToInventory.StoreTarget(nextStoreCell);
                            job.targetQueueB.Add(nextStoreCell);
                            storeCellCapacity[storeCell] = new WorkGiver_HaulToInventory.CellAllocation(nextThing, WorkGiver_HaulToInventory.CapacityAt(nextThing, nextStoreCell, map));
                            if (PickUpAndHaulDiagnostics.Enabled)
                            {
                                Log.Message($"New cell for unstackable {nextThing} = {nextStoreCell}");
                            }
                        }
                        else
                        {
                            var destinationAsThing = (Thing)haulDestination;
                            storeCell = new WorkGiver_HaulToInventory.StoreTarget(destinationAsThing);
                            job.targetQueueB.Add(destinationAsThing);
                            storeCellCapacity[storeCell] = new WorkGiver_HaulToInventory.CellAllocation(nextThing, innerInteractableThingOwner.GetCountCanAccept(nextThing));
                            if (PickUpAndHaulDiagnostics.Enabled)
                            {
                                Log.Message($"New haulDestination for unstackable {nextThing} = {haulDestination}");
                            }
                        }
                    }
                    else
                    {
                        if (PickUpAndHaulDiagnostics.Enabled)
                        {
                            Log.Message($"{nextThing} can't stack with allocated cells");
                        }

                        if (job.targetQueueA.NullOrEmpty())
                        {
                            job.targetQueueA.Add(nextThing);
                        }

                        return false;
                    }
                }

                job.targetQueueA.Add(nextThing);
                var count = nextThing.stackCount;
                storeCellCapacity[storeCell].capacity -= count;
                if (PickUpAndHaulDiagnostics.Enabled)
                {
                    Log.Message($"{pawn} allocating {nextThing}:{count}, now {storeCell}:{storeCellCapacity[storeCell].capacity}");
                }

                while (storeCellCapacity[storeCell].capacity <= 0)
                {
                    var capacityOver = -storeCellCapacity[storeCell].capacity;
                    storeCellCapacity.Remove(storeCell);

                    if (PickUpAndHaulDiagnostics.Enabled)
                    {
                        Log.Message($"{pawn} overdone {storeCell} by {capacityOver}");
                    }

                    if (capacityOver == 0)
                    {
                        break;
                    }

                    var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
                    if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, false, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner))
                    {
                        if (innerInteractableThingOwner is null)
                        {
                            storeCell = new WorkGiver_HaulToInventory.StoreTarget(nextStoreCell);
                            job.targetQueueB.Add(nextStoreCell);

                            var capacity = WorkGiver_HaulToInventory.CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
                            storeCellCapacity[storeCell] = new WorkGiver_HaulToInventory.CellAllocation(nextThing, capacity);
                            if (PickUpAndHaulDiagnostics.Enabled)
                            {
                                Log.Message($"New cell {nextStoreCell}:{capacity}, allocated extra {capacityOver}");
                            }
                        }
                        else
                        {
                            var destinationAsThing = (Thing)nextHaulDestination;
                            storeCell = new WorkGiver_HaulToInventory.StoreTarget(destinationAsThing);
                            job.targetQueueB.Add(destinationAsThing);

                            var capacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;
                            storeCellCapacity[storeCell] = new WorkGiver_HaulToInventory.CellAllocation(nextThing, capacity);
                            if (PickUpAndHaulDiagnostics.Enabled)
                            {
                                Log.Message($"New haulDestination {nextHaulDestination}:{capacity}, allocated extra {capacityOver}");
                            }
                        }
                    }
                    else
                    {
                        count -= capacityOver;
                        job.countQueue.Add(count);
                        if (PickUpAndHaulDiagnostics.Enabled)
                        {
                            Log.Message($"Nowhere else to store, allocated {nextThing}:{count}");
                        }
                        return false;
                    }
                }

                job.countQueue.Add(count);
                if (PickUpAndHaulDiagnostics.Enabled)
                {
                    Log.Message($"{nextThing}:{count} allocated");
                }
                return true;
            }

            private bool TryFindBestBetterStorageFor(
                Thing thing,
                Pawn carrier,
                Map map,
                StoragePriority currentPriority,
                Faction faction,
                bool acceptSamePriority,
                out IntVec3 foundCell,
                out IHaulDestination haulDestination,
                out ThingOwner innerInteractableThingOwner)
            {
                PickUpAndHaulDiagnostics.CountStorageLookupAttempt();

                var storagePriority = StoragePriority.Unstored;
                innerInteractableThingOwner = null;
                if (TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, acceptSamePriority, out var foundCell2))
                {
                    storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
                }

                if (!TryFindBestBetterNonSlotGroupStorageFor(thing, carrier, map, currentPriority, faction, acceptSamePriority, out var haulDestination2))
                {
                    haulDestination2 = null;
                }

                if (storagePriority == StoragePriority.Unstored && haulDestination2 == null)
                {
                    foundCell = IntVec3.Invalid;
                    haulDestination = null;
                    return false;
                }

                if (haulDestination2 != null && (storagePriority == StoragePriority.Unstored || (int)haulDestination2.GetStoreSettings().Priority > (int)storagePriority))
                {
                    foundCell = IntVec3.Invalid;
                    haulDestination = haulDestination2;

                    if (haulDestination2 is not Thing destinationAsThing)
                    {
                        Log.Error($"{haulDestination2} is not a valid Thing. Pick Up And Haul can't work with this");
                    }
                    else
                    {
                        innerInteractableThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner();
                    }

                    if (innerInteractableThingOwner is null)
                    {
                        Log.Error($"{haulDestination2} gave null ThingOwner during lookup in Pick Up And Haul's WorkGiver_HaulToInventory");
                    }

                    return true;
                }

                foundCell = foundCell2;
                haulDestination = foundCell2.GetSlotGroup(map)?.parent;
                return haulDestination != null;
            }

            private bool TryFindBestBetterStoreCellFor(
                Thing thing,
                Pawn carrier,
                Map map,
                StoragePriority currentPriority,
                Faction faction,
                bool acceptSamePriority,
                out IntVec3 foundCell)
            {
                var key = new StorageCacheKey(map, thing.def, currentPriority, faction, acceptSamePriority);
                if (!slotCellCache.TryGetValue(key, out var candidates))
                {
                    candidates = BuildSlotCellCandidates(map, currentPriority, acceptSamePriority);
                    slotCellCache[key] = candidates;
                }

                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (WorkGiver_HaulToInventory.skipCells != null && WorkGiver_HaulToInventory.skipCells.Contains(candidate.Cell))
                    {
                        continue;
                    }

                    var slotGroup = candidate.Cell.GetSlotGroup(map);
                    if (slotGroup == null || slotGroup.Settings.Priority != candidate.Priority || !slotGroup.parent.Accepts(thing))
                    {
                        continue;
                    }

                    if (StoreUtility.IsGoodStoreCell(candidate.Cell, map, thing, carrier, faction) && candidate.Cell != default)
                    {
                        foundCell = candidate.Cell;
                        WorkGiver_HaulToInventory.skipCells?.Add(candidate.Cell);
                        return true;
                    }
                }

                foundCell = IntVec3.Invalid;
                return false;
            }

            private bool TryFindBestBetterNonSlotGroupStorageFor(
                Thing thing,
                Pawn carrier,
                Map map,
                StoragePriority currentPriority,
                Faction faction,
                bool acceptSamePriority,
                out IHaulDestination haulDestination)
            {
                var key = new StorageCacheKey(map, thing.def, currentPriority, faction, acceptSamePriority);
                if (!nonSlotThingCache.TryGetValue(key, out var candidates))
                {
                    candidates = BuildNonSlotCandidates(map, currentPriority, acceptSamePriority);
                    nonSlotThingCache[key] = candidates;
                }

                var origin = thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : carrier.PositionHeld;
                var bestDistance = float.MaxValue;
                var storagePriority = StoragePriority.Unstored;
                haulDestination = null;

                for (var i = 0; i < candidates.Count; i++)
                {
                    var destinationThing = candidates[i];
                    if (destinationThing == null || destinationThing.Destroyed || destinationThing.Faction != faction || (WorkGiver_HaulToInventory.skipThings != null && WorkGiver_HaulToInventory.skipThings.Contains(destinationThing)))
                    {
                        continue;
                    }

                    if (destinationThing is Building_Grave && !thing.CanBeBuried())
                    {
                        continue;
                    }

                    if (destinationThing is IHaulDestination destination)
                    {
                        var priority = destination.GetStoreSettings().Priority;
                        if ((int)priority < (int)storagePriority
                            || (acceptSamePriority && (int)priority < (int)currentPriority)
                            || (!acceptSamePriority && (int)priority <= (int)currentPriority))
                        {
                            continue;
                        }

                        var distance = origin.DistanceToSquared(destination.Position);
                        if (distance > bestDistance || !destination.Accepts(thing))
                        {
                            continue;
                        }

                        if (carrier != null)
                        {
                            if (destinationThing.IsForbidden(carrier)
                                || !carrier.CanReserveNew(destinationThing)
                                || !carrier.Map.reachability.CanReach(origin, destinationThing, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
                            {
                                continue;
                            }
                        }
                        else if (faction != null)
                        {
                            if (destinationThing.IsForbidden(faction) || map.reservationManager.IsReservedByAnyoneOf(destinationThing, faction))
                            {
                                continue;
                            }
                        }

                        bestDistance = distance;
                        storagePriority = priority;
                        haulDestination = destination;
                    }
                }

                if (haulDestination is Thing foundThing)
                {
                    WorkGiver_HaulToInventory.skipThings?.Add(foundThing);
                }

                return haulDestination != null;
            }

            private static List<SlotCellCandidate> BuildSlotCellCandidates(Map map, StoragePriority currentPriority, bool acceptSamePriority)
            {
                var result = new List<SlotCellCandidate>();
                var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
                for (var i = 0; i < haulDestinations.Count; i++)
                {
                    var slotGroup = haulDestinations[i];
                    if (acceptSamePriority ? slotGroup.Settings.Priority < currentPriority : slotGroup.Settings.Priority <= currentPriority)
                    {
                        continue;
                    }

                    var cellsList = slotGroup.CellsList;
                    for (var j = 0; j < cellsList.Count; j++)
                    {
                        result.Add(new SlotCellCandidate(cellsList[j], slotGroup.Settings.Priority));
                    }
                }

                return result;
            }

            private static List<Thing> BuildNonSlotCandidates(Map map, StoragePriority currentPriority, bool acceptSamePriority)
            {
                var result = new List<Thing>();
                var allHaulDestinationsListInPriorityOrder = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
                for (var i = 0; i < allHaulDestinationsListInPriorityOrder.Count; i++)
                {
                    var haulDestination = allHaulDestinationsListInPriorityOrder[i];
                    if (haulDestination is ISlotGroupParent)
                    {
                        continue;
                    }

                    var priority = haulDestination.GetStoreSettings().Priority;
                    if (acceptSamePriority ? priority < currentPriority : priority <= currentPriority)
                    {
                        continue;
                    }

                    if (haulDestination is Thing destinationThing)
                    {
                        result.Add(destinationThing);
                    }
                }

                return result;
            }
        }

        private readonly struct StorageCacheKey : IEquatable<StorageCacheKey>
        {
            private readonly Map map;
            private readonly ThingDef thingDef;
            private readonly StoragePriority currentPriority;
            private readonly Faction faction;
            private readonly bool acceptSamePriority;

            public StorageCacheKey(Map map, ThingDef thingDef, StoragePriority currentPriority, Faction faction, bool acceptSamePriority)
            {
                this.map = map;
                this.thingDef = thingDef;
                this.currentPriority = currentPriority;
                this.faction = faction;
                this.acceptSamePriority = acceptSamePriority;
            }

            public bool Equals(StorageCacheKey other)
                => ReferenceEquals(map, other.map)
                && ReferenceEquals(thingDef, other.thingDef)
                && currentPriority == other.currentPriority
                && ReferenceEquals(faction, other.faction)
                && acceptSamePriority == other.acceptSamePriority;

            public override bool Equals(object obj)
                => obj is StorageCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = map != null ? map.GetHashCode() : 0;
                    hashCode = (hashCode * 397) ^ (thingDef != null ? thingDef.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (int)currentPriority;
                    hashCode = (hashCode * 397) ^ (faction != null ? faction.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ acceptSamePriority.GetHashCode();
                    return hashCode;
                }
            }
        }

        private readonly struct SlotCellCandidate
        {
            public readonly IntVec3 Cell;
            public readonly StoragePriority Priority;

            public SlotCellCandidate(IntVec3 cell, StoragePriority priority)
            {
                Cell = cell;
                Priority = priority;
            }
        }
    }

    public static class PickUpAndHaulDiagnostics
    {
        [ThreadStatic]
        private static PlanningSample current;

        public static bool Enabled => PickUpAndHaulCECarryFixMod.Settings?.DiagnosticsEnabled == true;

        public static PlanningSample BeginPlanning(Pawn pawn, Thing firstThing)
        {
            if (!Enabled)
            {
                return null;
            }

            var sample = new PlanningSample(current, pawn, firstThing, CountHaulables(pawn));
            current = sample;
            return sample;
        }

        public static void EndPlanning(PlanningSample sample, Job result)
        {
            if (sample == null)
            {
                return;
            }

            sample.Stopwatch.Stop();
            sample.ResultDefName = result?.def?.defName ?? "null";
            sample.ReturnedNull = result == null;
            sample.ReturnedInventoryJob = result?.def == PickUpAndHaulJobDefOf.HaulToInventory;
            sample.ReturnedFallbackJob = result != null && !sample.ReturnedInventoryJob;
            sample.PlannedTargets = result?.targetQueueA?.Count ?? 0;
            sample.PlannedCounts = result?.countQueue?.Count ?? 0;
            sample.EmptyOrNullTargetQueue = sample.ReturnedInventoryJob && result.targetQueueA.NullOrEmpty();

            if (current == sample)
            {
                current = sample.Parent;
            }
            else if (current == null || current.Parent == sample)
            {
                current = sample.Parent;
            }

            Log.Message(sample.FormatSummary());
        }

        public static void CountNearestSearch(int candidatesEvaluated)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.NearestSearches++;
            current.CandidatesEvaluated += Math.Max(0, candidatesEvaluated);
        }

        public static void CountStorageAllocationAttempt()
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.StorageAllocationAttempts++;
        }

        public static void CountStorageLookupAttempt()
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.StorageLookupAttempts++;
        }

        public static void CountCeCapacityRejection()
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.CeCapacityRejections++;
        }

        public static void CountMalformedReservation()
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.MalformedReservations++;
        }

        private static int CountHaulables(Pawn pawn)
        {
            try
            {
                return pawn?.Map?.listerHaulables?.ThingsPotentiallyNeedingHauling()?.Count() ?? 0;
            }
            catch (Exception ex)
            {
                Log.Warning("[Pick Up And Haul CE Carry Fix] Diagnostics could not count haulables: " + ex.Message);
                return 0;
            }
        }

        public class PlanningSample
        {
            public readonly PlanningSample Parent;
            public readonly string PawnLabel;
            public readonly string FirstThingLabel;
            public readonly int HaulablesSeen;
            public readonly Stopwatch Stopwatch;
            public int NearestSearches;
            public int CandidatesEvaluated;
            public int CeCapacityRejections;
            public int StorageAllocationAttempts;
            public int StorageLookupAttempts;
            public int MalformedReservations;
            public bool ReturnedNull;
            public bool ReturnedInventoryJob;
            public bool ReturnedFallbackJob;
            public bool EmptyOrNullTargetQueue;
            public int PlannedTargets;
            public int PlannedCounts;
            public string ResultDefName;

            public PlanningSample(PlanningSample parent, Pawn pawn, Thing firstThing, int haulablesSeen)
            {
                Parent = parent;
                PawnLabel = pawn?.LabelShortCap ?? pawn?.ToStringSafe() ?? "null";
                FirstThingLabel = firstThing?.LabelCap ?? firstThing?.ToStringSafe() ?? "null";
                HaulablesSeen = haulablesSeen;
                Stopwatch = Stopwatch.StartNew();
            }

            public string FormatSummary()
                => "[Pick Up And Haul CE Carry Fix] Diagnostics "
                + $"pawn={PawnLabel}; thing={FirstThingLabel}; result={ResultDefName}; "
                + $"elapsedMs={Stopwatch.Elapsed.TotalMilliseconds:F2}; haulablesSeen={HaulablesSeen}; "
                + $"nearestSearches={NearestSearches}; candidatesEvaluated~={CandidatesEvaluated}; "
                + $"ceCapacityRejections={CeCapacityRejections}; storageLookups={StorageLookupAttempts}; "
                + $"storageAllocations={StorageAllocationAttempts}; fallback={ReturnedFallbackJob}; "
                + $"nullResult={ReturnedNull}; emptyQueue={EmptyOrNullTargetQueue}; malformedReservations={MalformedReservations}; "
                + $"plannedTargets={PlannedTargets}; plannedCounts={PlannedCounts}";
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            if (!ModsConfig.IsActive("Mehni.PickUpAndHaul") || !ModsConfig.IsActive("CETeam.CombatExtended"))
            {
                return;
            }

            try
            {
                new Harmony("local.pickupandhaul.cecarryfix").PatchAll();
                Log.Message("[Pick Up And Haul CE Carry Fix] Loaded.");
            }
            catch (Exception ex)
            {
                Log.Error("[Pick Up And Haul CE Carry Fix] Failed to patch: " + ex);
            }
        }
    }

    public static class CeInventoryUtility
    {
        private static StatDef bulkStatDef;
        private static bool lookedForBulkStatDef;

        public static bool TryGetInventory(Pawn pawn, out CompInventory inventory)
        {
            inventory = pawn?.GetComp<CompInventory>();
            return inventory != null;
        }

        public static int CountThatFits(Pawn pawn, Thing thing)
            => CountThatFits(pawn, thing, thing?.stackCount ?? 0);

        public static int CountThatFits(Pawn pawn, Thing thing, int maxCount)
        {
            if (thing == null || maxCount <= 0)
            {
                return 0;
            }

            maxCount = Math.Min(maxCount, thing.stackCount);
            if (!TryGetInventory(pawn, out var inventory))
            {
                var vanillaCount = MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing);
                return Math.Max(0, Math.Min(maxCount, vanillaCount));
            }

            inventory.CanFitInInventory(thing, out var countToPickUp, false, false);
            return Math.Max(0, Math.Min(countToPickUp, maxCount));
        }

        public static int CountThatFitsCurrentJob(Pawn pawn, Thing thing)
            => CountThatFits(pawn, thing, pawn?.CurJob?.count ?? thing?.stackCount ?? 0);

        public static int LastPlannedCount(Job job, Thing thing)
        {
            if (job?.countQueue == null || job.countQueue.Count == 0)
            {
                return thing?.stackCount ?? 0;
            }

            return Math.Max(0, job.countQueue[job.countQueue.Count - 1]);
        }

        public static bool OverAllowedCapacity(Pawn pawn)
        {
            if (!TryGetInventory(pawn, out var inventory))
            {
                return false;
            }

            var overAllowedCapacity = CapacityRatio(inventory) >= PickUpAndHaul.Settings.MaximumOccupiedCapacityToConsiderHauling;
            if (overAllowedCapacity)
            {
                PickUpAndHaulDiagnostics.CountCeCapacityRejection();
            }

            return overAllowedCapacity;
        }

        public static float EncumbrancePercent(Pawn pawn)
        {
            if (!TryGetInventory(pawn, out var inventory))
            {
                return MassUtility.EncumbrancePercent(pawn);
            }

            return CapacityRatio(inventory);
        }

        public static float AddedEncumbrance(Pawn pawn, Thing thing)
        {
            if (thing == null)
            {
                return 0f;
            }

            if (!TryGetInventory(pawn, out var inventory))
            {
                return thing.stackCount * thing.GetStatValue(StatDefOf.Mass) / MassUtility.Capacity(pawn);
            }

            return AddedCapacityRatio(inventory, thing);
        }

        public static float WeightRatio(CompInventory inventory)
        {
            if (inventory == null || inventory.capacityWeight <= 0f)
            {
                return 1f;
            }

            return inventory.currentWeight / inventory.capacityWeight;
        }

        public static float BulkRatio(CompInventory inventory)
        {
            if (inventory == null || inventory.capacityBulk <= 0f)
            {
                return 1f;
            }

            return inventory.currentBulk / inventory.capacityBulk;
        }

        public static float CapacityRatio(CompInventory inventory)
            => Math.Max(WeightRatio(inventory), BulkRatio(inventory));

        public static float AddedCapacityRatio(CompInventory inventory, Thing thing)
        {
            if (thing == null)
            {
                return 0f;
            }

            var addedWeightRatio = inventory?.capacityWeight > 0f
                ? thing.stackCount * thing.GetStatValue(StatDefOf.Mass) / inventory.capacityWeight
                : 1f;

            var bulkDef = BulkStatDef;
            if (inventory?.capacityBulk <= 0f || bulkDef == null)
            {
                return addedWeightRatio;
            }

            var addedBulkRatio = thing.stackCount * thing.GetStatValue(bulkDef) / inventory.capacityBulk;
            return Math.Max(addedWeightRatio, addedBulkRatio);
        }

        public static int CountThatFitsBeforeScalarCapacity(Pawn pawn, Thing thing, float projectedCapacityRatio)
        {
            if (thing == null || !TryGetInventory(pawn, out var inventory))
            {
                return CountThatFits(pawn, thing);
            }

            var fullStackAdded = AddedCapacityRatio(inventory, thing);
            if (fullStackAdded <= 0f || thing.stackCount <= 0)
            {
                return CountThatFits(pawn, thing);
            }

            var previousRatio = projectedCapacityRatio - fullStackAdded;
            var unitRatio = fullStackAdded / thing.stackCount;
            var countFromCapacity = unitRatio > 0f
                ? (int)Math.Floor(Math.Max(0f, 1f - previousRatio) / unitRatio)
                : thing.stackCount;
            return CountThatFits(pawn, thing, countFromCapacity);
        }

        public struct PlannedCapacity
        {
            private readonly bool usesCeInventory;
            private readonly float capacityWeight;
            private readonly float capacityBulk;
            private readonly StatDef bulkDef;
            private float weightRatio;
            private float bulkRatio;

            private PlannedCapacity(CompInventory inventory)
            {
                usesCeInventory = inventory != null;
                capacityWeight = inventory?.capacityWeight ?? 0f;
                capacityBulk = inventory?.capacityBulk ?? 0f;
                bulkDef = BulkStatDef;
                weightRatio = WeightRatio(inventory);
                bulkRatio = BulkRatio(inventory);
            }

            public bool UsesCeInventory => usesCeInventory;
            public float CapacityRatio => Math.Max(weightRatio, bulkRatio);

            public static PlannedCapacity For(Pawn pawn)
            {
                TryGetInventory(pawn, out var inventory);
                return new PlannedCapacity(inventory);
            }

            public void Add(Thing thing, int count)
            {
                if (!usesCeInventory || thing == null || count <= 0)
                {
                    return;
                }

                weightRatio += AddedWeightRatio(thing, count);
                bulkRatio += AddedBulkRatio(thing, count);
            }

            public int CountThatFits(Pawn pawn, Thing thing, int maxCount)
            {
                if (!usesCeInventory || thing == null || maxCount <= 0)
                {
                    return CeInventoryUtility.CountThatFits(pawn, thing, maxCount);
                }

                maxCount = Math.Min(maxCount, thing.stackCount);
                var countFromCapacity = Math.Min(
                    CountByDimension(weightRatio, AddedWeightRatio(thing, 1), maxCount),
                    CountByDimension(bulkRatio, AddedBulkRatio(thing, 1), maxCount));
                return CeInventoryUtility.CountThatFits(pawn, thing, countFromCapacity);
            }

            private float AddedWeightRatio(Thing thing, int count)
            {
                if (capacityWeight <= 0f)
                {
                    return 0f;
                }

                return count * thing.GetStatValue(StatDefOf.Mass) / capacityWeight;
            }

            private float AddedBulkRatio(Thing thing, int count)
            {
                if (capacityBulk <= 0f || bulkDef == null)
                {
                    return 0f;
                }

                return count * thing.GetStatValue(bulkDef) / capacityBulk;
            }

            private static int CountByDimension(float currentRatio, float addedRatioPerUnit, int maxCount)
            {
                if (addedRatioPerUnit <= 0f)
                {
                    return maxCount;
                }

                var remainingRatio = 1f - currentRatio;
                if (remainingRatio <= 0f)
                {
                    return 0;
                }

                return Math.Max(0, Math.Min(maxCount, (int)Math.Floor((remainingRatio + 0.0001f) / addedRatioPerUnit)));
            }
        }

        private static StatDef BulkStatDef
        {
            get
            {
                if (lookedForBulkStatDef)
                {
                    return bulkStatDef;
                }

                lookedForBulkStatDef = true;
                bulkStatDef = CE_StatDefOf.Bulk ?? DefDatabase<StatDef>.GetNamedSilentFail("Bulk");
                return bulkStatDef;
            }
        }

        public static bool WillBeOverEncumberedAfterPickingUp(Pawn pawn, Thing thing, int count)
        {
            if (TryGetInventory(pawn, out _))
            {
                var willBeOverEncumbered = CountThatFits(pawn, thing) < Math.Max(1, count);
                if (willBeOverEncumbered)
                {
                    PickUpAndHaulDiagnostics.CountCeCapacityRejection();
                }

                return willBeOverEncumbered;
            }

            return MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, count);
        }

        public static void UpdateInventory(Pawn pawn)
        {
            if (TryGetInventory(pawn, out var inventory))
            {
                inventory.UpdateInventory();
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_CompatHelper_CanFitInInventory
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("PickUpAndHaul.CompatHelper"), "CanFitInInventory");

        public static bool Prefix(Pawn pawn, Thing thing, ref int __result)
        {
            __result = CeInventoryUtility.CountThatFits(pawn, thing);
            if (__result <= 0)
            {
                PickUpAndHaulDiagnostics.CountCeCapacityRejection();
            }

            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_CompatHelper_CeOverweight
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("PickUpAndHaul.CompatHelper"), "CeOverweight");

        public static bool Prefix(Pawn pawn, ref bool __result)
        {
            __result = CeInventoryUtility.OverAllowedCapacity(pawn);
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_CompatHelper_UpdateInventory
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("PickUpAndHaul.CompatHelper"), "UpdateInventory");

        public static bool Prefix(Pawn pawn)
        {
            CeInventoryUtility.UpdateInventory(pawn);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.OverAllowedGearCapacity))]
    public static class Patch_WorkGiver_OverAllowedGearCapacity
    {
        public static bool Prefix(Pawn pawn, ref bool __result)
        {
            if (!CeInventoryUtility.TryGetInventory(pawn, out _))
            {
                return true;
            }

            __result = CeInventoryUtility.OverAllowedCapacity(pawn);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.AddedEncumberance))]
    public static class Patch_WorkGiver_AddedEncumberance
    {
        public static bool Prefix(Pawn pawn, Thing thing, ref float __result)
        {
            if (!CeInventoryUtility.TryGetInventory(pawn, out _))
            {
                return true;
            }

            __result = CeInventoryUtility.AddedEncumbrance(pawn, thing);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.CountPastCapacity))]
    public static class Patch_WorkGiver_CountPastCapacity
    {
        public static bool Prefix(Pawn pawn, Thing thing, float encumberance, ref int __result)
        {
            if (!CeInventoryUtility.TryGetInventory(pawn, out _))
            {
                return true;
            }

            __result = CeInventoryUtility.CountThatFitsBeforeScalarCapacity(pawn, thing, encumberance);
            if (__result <= 0)
            {
                PickUpAndHaulDiagnostics.CountCeCapacityRejection();
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.PotentialWorkThingsGlobal))]
    public static class Patch_WorkGiver_PotentialWorkThingsGlobal
    {
        public static bool Prefix(Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!PickUpAndHaulPerformance.Enabled)
            {
                return true;
            }

            __result = PickUpAndHaulPerformance.PotentialWorkThingsGlobal(pawn);
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing
    {
        private static readonly MethodBase Target = AccessTools.Method(
            typeof(JobDriver_UnloadYourHauledInventory),
            "FirstUnloadableThing",
            new[] { typeof(Pawn), typeof(HashSet<Thing>) });

        private static bool warnedSelectionFailure;

        public static bool Prepare()
        {
            if (Target != null)
            {
                return true;
            }

            Log.Warning(
                "[Pick Up And Haul CE Carry Fix] Could not find JobDriver_UnloadYourHauledInventory.FirstUnloadableThing; nearest-destination unload routing is disabled.");
            return false;
        }

        public static MethodBase TargetMethod() => Target;

        public static bool Prefix(Pawn pawn, HashSet<Thing> carriedThings, ref ThingCount __result)
        {
            if (!PickUpAndHaulUnloadRouting.Enabled)
            {
                return true;
            }

            try
            {
                if (!PickUpAndHaulUnloadRouting.TrySelectNextUnloadableThing(pawn, carriedThings, out __result))
                {
                    return true;
                }

                return false;
            }
            catch (Exception exception)
            {
                if (!warnedSelectionFailure)
                {
                    warnedSelectionFailure = true;
                    Log.Warning(
                        "[Pick Up And Haul CE Carry Fix] Nearest-destination unload selection failed; falling back to Pick Up And Haul's original order. "
                        + exception);
                }

                return true;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_HaulToInventory), nameof(JobDriver_HaulToInventory.TryMakePreToilReservations))]
    public static class Patch_JobDriver_TryMakePreToilReservations
    {
        public static bool Prefix(JobDriver_HaulToInventory __instance, ref bool __result)
        {
            var pawn = __instance?.pawn;
            var job = __instance?.job;

            if (pawn == null || job == null || job.targetQueueA.NullOrEmpty())
            {
                PickUpAndHaulDiagnostics.CountMalformedReservation();
                Log.Warning("[Pick Up And Haul CE Carry Fix] Rejected malformed HaulToInventory reservation job with missing targetQueueA.");
                __result = false;
                return false;
            }

            if (PickUpAndHaulDiagnostics.Enabled)
            {
                Log.Message($"[Pick Up And Haul CE Carry Fix] {pawn} starting HaulToInventory job: {job.targetQueueA.ToStringSafeEnumerable()}:{job.countQueue.ToStringSafeEnumerable()}");
            }

            pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
            AdaptiveStorageReservationCompatibility.ReserveAdditionalGatherDestinations(pawn, job);

            __result = pawn.Reserve(job.targetQueueA[0], job)
                && AdaptiveStorageReservationCompatibility.ReservePrimaryGatherDestination(pawn, job);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.JobOnThing))]
    public static class Patch_WorkGiver_JobOnThing
    {
        public static bool Prefix(Pawn pawn, Thing thing, bool forced, ref Job __result, ref PickUpAndHaulDiagnostics.PlanningSample __state)
        {
            __state = PickUpAndHaulDiagnostics.BeginPlanning(pawn, thing);
            PickUpAndHaulStorageHotpath.BeginPlanning();
            if (!PickUpAndHaulPerformance.Enabled)
            {
                return true;
            }

            __result = PickUpAndHaulPerformance.TryMakeOptimizedJob(pawn, thing, forced);
            return false;
        }

        public static void Postfix(PickUpAndHaulDiagnostics.PlanningSample __state, Job __result)
        {
            PickUpAndHaulStorageHotpath.EndPlanning();
            PickUpAndHaulDiagnostics.EndPlanning(__state, __result);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var willOverEncumber = AccessTools.Method(typeof(MassUtility), nameof(MassUtility.WillBeOverEncumberedAfterPickingUp), new[] { typeof(Pawn), typeof(Thing), typeof(int) });
            var ceWillOverEncumber = AccessTools.Method(typeof(CeInventoryUtility), nameof(CeInventoryUtility.WillBeOverEncumberedAfterPickingUp));
            var encumbrancePercent = AccessTools.Method(typeof(MassUtility), nameof(MassUtility.EncumbrancePercent), new[] { typeof(Pawn) });
            var ceEncumbrancePercent = AccessTools.Method(typeof(CeInventoryUtility), nameof(CeInventoryUtility.EncumbrancePercent));

            foreach (var instruction in instructions)
            {
                if (instruction.Calls(willOverEncumber))
                {
                    yield return new CodeInstruction(OpCodes.Call, ceWillOverEncumber);
                }
                else if (instruction.Calls(encumbrancePercent))
                {
                    yield return new CodeInstruction(OpCodes.Call, ceEncumbrancePercent);
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.HasJobOnThing))]
    public static class Patch_WorkGiver_HasJobOnThing_AdaptiveStorageReservation
    {
        public static void Postfix(Pawn pawn, Thing thing, bool forced, ref bool __result)
        {
            if (__result
                || !PickUpAndHaulPerformance.Enabled
                || pawn?.Map == null
                || thing == null
                || !WorkGiver_HaulToInventory.OkThingToHaul(thing, pawn)
                || !WorkGiver_HaulToInventory.IsNotCorpseOrAllowed(thing)
                || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
            {
                return;
            }

            __result = AdaptiveStorageReservationCompatibility.TryFindPlanningFallback(
                thing,
                pawn,
                StoreUtility.CurrentStoragePriorityOf(thing),
                logSelection: false,
                out _,
                out _);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.GetClosestAndRemove))]
    public static class Patch_WorkGiver_GetClosestAndRemove_Diagnostics
    {
        public static void Prefix(List<Thing> searchSet, ref int __state)
        {
            __state = PickUpAndHaulDiagnostics.Enabled ? searchSet?.Count ?? 0 : -1;
        }

        public static void Postfix(List<Thing> searchSet, int __state)
        {
            if (__state < 0)
            {
                return;
            }

            var afterCount = searchSet?.Count ?? 0;
            PickUpAndHaulDiagnostics.CountNearestSearch(__state - afterCount);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.GetClosestAndRemove))]
    public static class Patch_WorkGiver_GetClosestAndRemove_Hotpath
    {
        public static bool Prefix(
            IntVec3 center,
            Map map,
            List<Thing> searchSet,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator,
            ref Thing __result)
        {
            if (!PickUpAndHaulStorageHotpath.Enabled)
            {
                return true;
            }

            __result = PickUpAndHaulStorageHotpath.GetClosestAndRemoveWithoutLinq(center, map, searchSet, peMode, traverseParams, maxDistance, validator);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.FindClosestThing))]
    public static class Patch_WorkGiver_FindClosestThing_Hotpath
    {
        public static bool Prefix(List<Thing> searchSet, IntVec3 center, ref int index, ref Thing __result)
        {
            if (!PickUpAndHaulStorageHotpath.Enabled)
            {
                return true;
            }

            __result = PickUpAndHaulStorageHotpath.FindClosestThingWithoutLinq(searchSet, center, out index);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.AllocateThingAtCell))]
    public static class Patch_WorkGiver_AllocateThingAtCell_Diagnostics
    {
        public static void Prefix()
        {
            PickUpAndHaulDiagnostics.CountStorageAllocationAttempt();
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.AllocateThingAtCell))]
    public static class Patch_WorkGiver_AllocateThingAtCell_Hotpath
    {
        public static bool Prefix(
            Dictionary<WorkGiver_HaulToInventory.StoreTarget, WorkGiver_HaulToInventory.CellAllocation> storeCellCapacity,
            Pawn pawn,
            Thing nextThing,
            Job job,
            ref bool __result)
        {
            if (!PickUpAndHaulStorageHotpath.TryAllocateThingAtCell(storeCellCapacity, pawn, nextThing, job, out __result))
            {
                return true;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.TryFindBestBetterStorageFor))]
    public static class Patch_WorkGiver_TryFindBestBetterStorageFor_Diagnostics
    {
        public static void Prefix()
        {
            PickUpAndHaulDiagnostics.CountStorageLookupAttempt();
        }
    }

    [HarmonyPatch]
    public static class Patch_JobDriver_MakeNewToils_WorkGiverLookupCache
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return HarmonyTargetDiscovery.GetDeclaredMethods(
                typeof(JobDriver_HaulToInventory),
                "MakeNewToils");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(
                typeof(PickUpAndHaulStorageHotpath),
                nameof(PickUpAndHaulStorageHotpath.ResolveHaulToInventoryWorkGiverDef),
                new[] { typeof(IEnumerable<WorkGiverDef>), typeof(Func<WorkGiverDef, bool>) });

            foreach (var instruction in instructions)
            {
                if (IsWorkGiverDefFirstCall(instruction))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        private static bool IsWorkGiverDefFirstCall(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Call || instruction.operand is not MethodInfo method || !method.IsGenericMethod)
            {
                return false;
            }

            var definition = method.GetGenericMethodDefinition();
            var parameters = definition.GetParameters();
            return definition.DeclaringType == typeof(Enumerable)
                && definition.Name == nameof(Enumerable.First)
                && parameters.Length == 2
                && method.GetGenericArguments()[0] == typeof(WorkGiverDef);
        }
    }

    [HarmonyPatch]
    public static class Patch_JobDriver_MakeNewToils_CePickupCount
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return HarmonyTargetDiscovery.GetDeclaredMethods(
                typeof(JobDriver_HaulToInventory),
                "MakeNewToils");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var compatHelper = AccessTools.TypeByName("PickUpAndHaul.CompatHelper");
            var canFitInInventory = compatHelper == null ? null : AccessTools.Method(compatHelper, "CanFitInInventory");
            var replacement = AccessTools.Method(
                typeof(CeInventoryUtility),
                nameof(CeInventoryUtility.CountThatFitsCurrentJob),
                new[] { typeof(Pawn), typeof(Thing) });

            foreach (var instruction in instructions)
            {
                if (canFitInInventory != null && replacement != null && instruction.Calls(canFitInInventory))
                {
                    var replacementInstruction = new CodeInstruction(instruction)
                    {
                        operand = replacement
                    };
                    yield return replacementInstruction;
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_JobDriver_CheckForOverencumberedForCombatExtended
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
        {
            foreach (var nestedType in typeof(JobDriver_HaulToInventory).GetNestedTypes(BindingFlags.NonPublic))
            {
                var method = AccessTools.Method(nestedType, "<CheckForOverencumberedForCombatExtended>b__0");
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var encumbrancePercent = AccessTools.Method(typeof(MassUtility), nameof(MassUtility.EncumbrancePercent), new[] { typeof(Pawn) });
            var ceEncumbrancePercent = AccessTools.Method(typeof(CeInventoryUtility), nameof(CeInventoryUtility.EncumbrancePercent));

            foreach (var instruction in instructions)
            {
                if (instruction.Calls(encumbrancePercent))
                {
                    yield return new CodeInstruction(OpCodes.Call, ceEncumbrancePercent);
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }
}
