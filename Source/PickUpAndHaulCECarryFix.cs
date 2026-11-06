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
                "Plan opportunistic pickups lazily: score nearby haulables, then validate only the best candidate at each step, instead of Pick Up And Haul's full-list sort and repeated nearest searches.");
            if (Settings.PerformancePatchEnabled)
            {
                listing.Label(
                    $"Maximum candidate validations per haul plan: {Settings.OpportunisticCandidateCap}",
                    -1f,
                    new TipSignal("Upper bound on how many nearby haulables are fully validated (reachability, reservation, storage checks) while planning one multi-item haul. Lower values plan faster in dense piles but may skip valid items farther down the list."));
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
                "Use cheaper storage scans while planning hauls, skip needless Pick Up And Haul work, and index reserved Adaptive Storage cells.");

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
            var statEvaluations = 0;
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

            var candidates = BuildCandidateSet(map, thing, distanceToSearchMore, CandidateCap);
            var candidatesInRadius = candidates.Count;

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
                            var unitCost = plannedCapacity.CostOf(nextThing);
                            statEvaluations += 2;
                            var countThatFits = plannedCapacity.CountThatFits(pawn, nextThing, plannedCount, unitCost);
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

                            plannedCapacity.Add(plannedCount, unitCost);
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
                while ((nextThing = GetNextCandidate(lastThing.Position, storeTarget.Position, map, candidates,
                    PathEndMode.ClosestTouch, TraverseParms.For(pawn), distanceToSearchMore, Validator)) != null);
            }
            finally
            {
                WorkGiver_HaulToInventory.skipCells = null;
                WorkGiver_HaulToInventory.skipThings = null;
                PickUpAndHaulAggregateStats.CountOptimizedPlan(candidatesInRadius, candidates.ValidationCalls, statEvaluations);
            }

            return job;
        }

        private static LazyCandidateSet<Thing> BuildCandidateSet(
            Map map,
            Thing firstThing,
            float maxDistance,
            int validationBudget)
        {
            var maxDistanceSquared = maxDistance * maxDistance;
            var candidates = new LazyCandidateSet<Thing>(validationBudget);

            foreach (var candidate in map.listerHaulables.ThingsPotentiallyNeedingHauling())
            {
                if (candidate == null || candidate == firstThing || !candidate.Spawned)
                {
                    continue;
                }

                if ((firstThing.Position - candidate.Position).LengthHorizontalSquared > maxDistanceSquared)
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static Thing GetNextCandidate(
            IntVec3 center,
            IntVec3 destination,
            Map map,
            LazyCandidateSet<Thing> candidates,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator)
        {
            var maxDistanceSquared = maxDistance * maxDistance;
            candidates.TryPickNext(
                t => t.Spawned
                    ? new RouteScore(
                        (center - t.Position).LengthHorizontalSquared,
                        (destination - t.Position).LengthHorizontalSquared,
                        t.thingIDNumber)
                    : new RouteScore(0, 0, t.thingIDNumber),
                maxDistanceSquared,
                t => t.Spawned,
                t => map.reachability.CanReach(center, t, peMode, traverseParams)
                    && (validator == null || validator(t)),
                out var picked,
                out var evaluated);
            PickUpAndHaulDiagnostics.CountNearestSearch(evaluated);
            return picked;
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
    }

    public static class PickUpAndHaulUnloadRouting
    {
        private const int DestinationCacheMaxAgeTicks = 600;
        private const int MaxTrackedPawns = 64;

        private static readonly Dictionary<int, UnloadJobState> States = new Dictionary<int, UnloadJobState>();

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

                candidates.Add(new UnloadCandidate(trackedThing, actualThing, isReplacement));
            }

            var tick = Find.TickManager.TicksGame;
            var state = GetState(pawn, tick);
            var searchesBefore = state.Cache.Searches;
            var searchedThisCall = new Dictionary<Thing, SelectionMemo>();
            var map = pawn.Map;

            UnloadDestination Search(UnloadCandidate candidate)
            {
                var actual = candidate.ActualThing;
                if (!StoreUtility.TryFindBestBetterStorageFor(
                    actual,
                    pawn,
                    map,
                    StoragePriority.Unstored,
                    pawn.Faction,
                    out var storageCell,
                    out var haulDestination))
                {
                    return UnloadDestination.None;
                }

                var destination = storageCell != IntVec3.Invalid
                    ? storageCell
                    : haulDestination?.Position ?? IntVec3.Invalid;
                if (destination == IntVec3.Invalid)
                {
                    return UnloadDestination.None;
                }

                searchedThisCall[actual] = new SelectionMemo(actual, storageCell, haulDestination, tick);
                return new UnloadDestination(true, destination.x, destination.z);
            }

            var bestIndex = state.Cache.SelectNearest(
                candidates,
                candidate => candidate.ActualThing.def,
                candidate => candidate.ActualThing.thingIDNumber,
                Search,
                pawn.Position.x,
                pawn.Position.z,
                tick);
            PickUpAndHaulAggregateStats.CountUnloadSelection(state.Cache.Searches - searchesBefore);
            if (bestIndex < 0)
            {
                state.Memo = null;
                return false;
            }

            var selected = candidates[bestIndex];
            if (selected.IsReplacement)
            {
                carriedThings.Remove(selected.TrackedThing);
            }

            // Only a search that ran for this exact item this tick can be reused by the unload toil.
            state.Memo = searchedThisCall.TryGetValue(selected.ActualThing, out var memo) ? memo : null;

            result = new ThingCount(selected.ActualThing, selected.ActualThing.stackCount);
            if (PickUpAndHaulDiagnostics.Enabled)
            {
                Log.Message(
                    $"[Pick Up And Haul CE Carry Fix] unload route pawn={pawn} item={selected.ActualThing} candidates={candidates.Count} searches={state.Cache.Searches - searchesBefore}");
            }

            return true;
        }

        /// <summary>
        /// Returns the storage target found for <paramref name="thing"/> during this tick's selection when it is still valid,
        /// so the unload toil does not repeat the storage search.
        /// </summary>
        public static bool TryTakeSelectionMemo(Pawn pawn, Thing thing, out IntVec3 cell, out IHaulDestination destination)
        {
            cell = IntVec3.Invalid;
            destination = null;
            if (!Enabled || pawn?.Map == null || thing == null || !States.TryGetValue(pawn.thingIDNumber, out var state))
            {
                return false;
            }

            var memo = state.Memo;
            state.Memo = null;
            if (memo == null || memo.Thing != thing || memo.Tick != Find.TickManager.TicksGame)
            {
                return false;
            }

            var map = pawn.Map;
            if (memo.Cell != IntVec3.Invalid)
            {
                var slotGroup = memo.Cell.GetSlotGroup(map);
                if (slotGroup == null
                    || slotGroup.parent == null
                    || !ReferenceEquals(slotGroup.parent, memo.Destination)
                    || !slotGroup.parent.Accepts(thing)
                    || !StoreUtility.IsGoodStoreCell(memo.Cell, map, thing, pawn, pawn.Faction))
                {
                    return false;
                }

                cell = memo.Cell;
                destination = memo.Destination;
                return true;
            }

            if (!(memo.Destination is Thing destinationThing)
                || destinationThing.Destroyed
                || !destinationThing.Spawned
                || destinationThing.Faction != pawn.Faction
                || !memo.Destination.Accepts(thing)
                || destinationThing.TryGetInnerInteractableThingOwner() is not { } owner
                || owner.GetCountCanAccept(thing) <= 0
                || destinationThing.IsForbidden(pawn)
                || !pawn.CanReserveNew(destinationThing)
                || !map.reachability.CanReach(pawn.PositionHeld, destinationThing, PathEndMode.ClosestTouch, TraverseParms.For(pawn)))
            {
                return false;
            }

            destination = memo.Destination;
            return true;
        }

        /// <summary>Records the toil's own storage search so later selections rank this item kind by a fresh destination.</summary>
        public static void RefreshDestination(Pawn pawn, Thing thing, bool found, IntVec3 cell, IHaulDestination destination)
        {
            if (!Enabled || pawn == null || thing == null || !States.TryGetValue(pawn.thingIDNumber, out var state))
            {
                return;
            }

            var position = cell != IntVec3.Invalid
                ? cell
                : destination?.Position ?? IntVec3.Invalid;
            var refreshed = found && position != IntVec3.Invalid
                ? new UnloadDestination(true, position.x, position.z)
                : UnloadDestination.None;
            state.Cache.Refresh(thing.def, refreshed, Find.TickManager.TicksGame);
        }

        private static UnloadJobState GetState(Pawn pawn, int tick)
        {
            var jobId = pawn.CurJob?.loadID ?? -1;
            if (States.TryGetValue(pawn.thingIDNumber, out var state) && state.JobLoadId == jobId)
            {
                return state;
            }

            if (States.Count >= MaxTrackedPawns)
            {
                States.Clear();
            }

            state = new UnloadJobState(jobId, new UnloadDestinationCache<ThingDef>(DestinationCacheMaxAgeTicks));
            States[pawn.thingIDNumber] = state;
            return state;
        }

        private sealed class UnloadJobState
        {
            public readonly int JobLoadId;
            public readonly UnloadDestinationCache<ThingDef> Cache;
            public SelectionMemo Memo;

            public UnloadJobState(int jobLoadId, UnloadDestinationCache<ThingDef> cache)
            {
                JobLoadId = jobLoadId;
                Cache = cache;
            }
        }

        private sealed class SelectionMemo
        {
            public readonly Thing Thing;
            public readonly IntVec3 Cell;
            public readonly IHaulDestination Destination;
            public readonly int Tick;

            public SelectionMemo(Thing thing, IntVec3 cell, IHaulDestination destination, int tick)
            {
                Thing = thing;
                Cell = cell;
                Destination = destination;
                Tick = tick;
            }
        }

        private readonly struct UnloadCandidate
        {
            public readonly Thing TrackedThing;
            public readonly Thing ActualThing;
            public readonly bool IsReplacement;

            public UnloadCandidate(Thing trackedThing, Thing actualThing, bool isReplacement)
            {
                TrackedThing = trackedThing;
                ActualThing = actualThing;
                IsReplacement = isReplacement;
            }
        }
    }

    public static class PickUpAndHaulStorageHotpath
    {
        [ThreadStatic]
        private static int planningDepth;

        private static WorkGiver_HaulToInventory cachedHaulToInventoryWorkGiver;
        private static WorkGiverDef cachedHaulToInventoryWorkGiverDef;
        private static bool lookedForHaulToInventoryWorkGiver;

        public static bool Enabled => PickUpAndHaulCECarryFixMod.Settings?.StorageHotpathCleanupEnabled == true;
        public static bool HasContext => Enabled && planningDepth > 0;

        public static void BeginPlanning()
        {
            if (!Enabled)
            {
                return;
            }

            planningDepth++;
        }

        public static void EndPlanning()
        {
            if (planningDepth > 0)
            {
                planningDepth--;
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

            result = AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job);
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

        private static readonly MethodInfo HaulMoreListGetter = AccessTools.PropertyGetter(typeof(JobDriver_HaulToInventory), "TempListForThings");
        private static List<Thing> haulMoreList;
        private static bool warnedHaulMoreLookup;

        /// <summary>
        /// True when <paramref name="searchSet"/> is Pick Up And Haul's "haul more" scratch list
        /// (JobDriver_HaulToInventory.TempListForThings), which is searched exactly once with a fixed center.
        /// </summary>
        public static bool IsHaulMoreList(List<Thing> searchSet)
        {
            if (searchSet == null)
            {
                return false;
            }

            if (haulMoreList == null)
            {
                if (HaulMoreListGetter == null)
                {
                    if (!warnedHaulMoreLookup)
                    {
                        warnedHaulMoreLookup = true;
                        Log.Warning("[Pick Up And Haul CE Carry Fix] Could not find JobDriver_HaulToInventory.TempListForThings; haul-more pre-filtering is disabled.");
                    }

                    return false;
                }

                haulMoreList = HaulMoreListGetter.Invoke(null, null) as List<Thing>;
            }

            return ReferenceEquals(searchSet, haulMoreList);
        }

        /// <summary>
        /// Same result as repeatedly taking the closest remaining thing, but drops unspawned and out-of-range things in one
        /// pass and sorts the rest once. Only valid for a list that is searched a single time with a fixed center.
        /// Ties keep list order, matching the original strict-less-than nearest search.
        /// </summary>
        public static Thing GetClosestAndRemoveSortedOnce(
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

            var listSize = searchSet.Count;
            var maxDistanceSquared = maxDistance * maxDistance;
            var keys = new long[listSize];
            var things = new Thing[listSize];
            var kept = 0;
            for (var i = 0; i < listSize; i++)
            {
                var candidate = searchSet[i];
                if (candidate == null || !candidate.Spawned)
                {
                    continue;
                }

                var distanceSquared = (center - candidate.Position).LengthHorizontalSquared;
                if (distanceSquared > maxDistanceSquared)
                {
                    continue;
                }

                keys[kept] = ((long)distanceSquared << 32) | (uint)i;
                things[kept] = candidate;
                kept++;
            }

            Array.Sort(keys, things, 0, kept);

            var popped = 0;
            var validatorCalls = 0;
            Thing result = null;
            var resultIndex = -1;
            for (var k = 0; k < kept; k++)
            {
                var candidate = things[k];
                popped++;
                if (!map.reachability.CanReach(center, candidate, peMode, traverseParams))
                {
                    continue;
                }

                if (validator != null)
                {
                    validatorCalls++;
                    if (!validator(candidate))
                    {
                        continue;
                    }
                }

                result = candidate;
                resultIndex = k;
                break;
            }

            // Leave the unvisited things in the list in nearest-first order, like the original.
            searchSet.Clear();
            if (resultIndex >= 0)
            {
                for (var k = resultIndex + 1; k < kept; k++)
                {
                    searchSet.Add(things[k]);
                }
            }

            PickUpAndHaulAggregateStats.CountHaulMoreToil(listSize, popped, validatorCalls);
            return result;
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

        private static bool AllocateThingAtCell(
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
                    ?? storeTarget.cell.GetSlotGroup(map).parent.Accepts(nextThing);
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

        private static bool TryFindBestBetterStorageFor(
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
            haulDestination = foundCell2.GetSlotGroup(map).parent;
            return true;
        }

        private static bool TryFindBestBetterStoreCellFor(
            Thing thing,
            Pawn carrier,
            Map map,
            StoragePriority currentPriority,
            Faction faction,
            bool acceptSamePriority,
            out IntVec3 foundCell)
        {
            var slotGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            for (var i = 0; i < slotGroups.Count; i++)
            {
                var slotGroup = slotGroups[i];
                var priority = slotGroup.Settings.Priority;
                if (acceptSamePriority ? priority < currentPriority : priority <= currentPriority)
                {
                    continue;
                }

                // Group-level filters run once per group, not once per cell.
                if (!slotGroup.parent.Accepts(thing))
                {
                    continue;
                }

                var cellsList = slotGroup.CellsList;
                for (var j = 0; j < cellsList.Count; j++)
                {
                    var cell = cellsList[j];
                    if (WorkGiver_HaulToInventory.skipCells.Contains(cell))
                    {
                        continue;
                    }

                    if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
                    {
                        foundCell = cell;
                        WorkGiver_HaulToInventory.skipCells.Add(cell);
                        return true;
                    }
                }
            }

            foundCell = IntVec3.Invalid;
            return false;
        }

        private static bool TryFindBestBetterNonSlotGroupStorageFor(
            Thing thing,
            Pawn carrier,
            Map map,
            StoragePriority currentPriority,
            Faction faction,
            bool acceptSamePriority,
            out IHaulDestination haulDestination)
        {
            var destinations = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
            var origin = thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : carrier.PositionHeld;
            var bestDistance = float.MaxValue;
            var storagePriority = StoragePriority.Unstored;
            haulDestination = null;

            for (var i = 0; i < destinations.Count; i++)
            {
                var destination = destinations[i];
                if (destination is ISlotGroupParent)
                {
                    continue;
                }

                // The list is ordered by descending priority, so the first destination below the thresholds ends the search.
                var priority = destination.GetStoreSettings().Priority;
                if ((int)priority < (int)storagePriority
                    || (acceptSamePriority && (int)priority < (int)currentPriority)
                    || (!acceptSamePriority && (int)priority <= (int)currentPriority))
                {
                    break;
                }

                if (!(destination is Thing destinationThing)
                    || destinationThing.Destroyed
                    || destinationThing.Faction != faction
                    || WorkGiver_HaulToInventory.skipThings.Contains(destinationThing))
                {
                    continue;
                }

                if (destinationThing is Building_Grave && !thing.CanBeBuried())
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

            if (haulDestination is Thing foundThing)
            {
                WorkGiver_HaulToInventory.skipThings.Add(foundThing);
            }

            return haulDestination != null;
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

            /// <summary>Reads mass and bulk once for a candidate; pass the result to <see cref="CountThatFits"/> and <see cref="Add"/>.</summary>
            internal UnitCost CostOf(Thing thing)
            {
                if (!usesCeInventory || thing == null)
                {
                    return default;
                }

                var mass = capacityWeight > 0f ? thing.GetStatValue(StatDefOf.Mass) : 0f;
                var bulk = capacityBulk > 0f && bulkDef != null ? thing.GetStatValue(bulkDef) : 0f;
                return new UnitCost(mass, bulk);
            }

            internal void Add(int count, UnitCost cost)
            {
                if (!usesCeInventory || count <= 0)
                {
                    return;
                }

                weightRatio += CapacityMath.AddedRatio(count, cost.MassPerUnit, capacityWeight);
                bulkRatio += CapacityMath.AddedRatio(count, cost.BulkPerUnit, capacityBulk);
            }

            internal int CountThatFits(Pawn pawn, Thing thing, int maxCount, UnitCost cost)
            {
                if (!usesCeInventory || thing == null || maxCount <= 0)
                {
                    return CeInventoryUtility.CountThatFits(pawn, thing, maxCount);
                }

                maxCount = Math.Min(maxCount, thing.stackCount);
                var countFromCapacity = Math.Min(
                    CapacityMath.CountByDimension(weightRatio, CapacityMath.AddedRatio(1, cost.MassPerUnit, capacityWeight), maxCount),
                    CapacityMath.CountByDimension(bulkRatio, CapacityMath.AddedRatio(1, cost.BulkPerUnit, capacityBulk), maxCount));

                // CE's CanFitInInventory is still needed for equipment-specific rules, but only when something is planned.
                return countFromCapacity <= 0
                    ? 0
                    : CeInventoryUtility.CountThatFits(pawn, thing, countFromCapacity);
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

        // A finalizer (not a postfix) so the planning depth and diagnostics sample are unwound even when planning throws;
        // otherwise the AllocateThingAtCell replacement would stay active outside JobOnThing.
        public static void Finalizer(PickUpAndHaulDiagnostics.PlanningSample __state, Job __result)
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

    /// <summary>
    /// Replaces Pick Up And Haul's four-condition HasJobOnThing (WorkGiver_HaulToInventory.cs) so the cheap checks run
    /// once and the Adaptive Storage reservation-blocked fallback is only evaluated after the normal storage search fails.
    /// Runs only when the fallback can apply; otherwise the original method runs untouched.
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.HasJobOnThing))]
    public static class Patch_WorkGiver_HasJobOnThing_AdaptiveStorageReservation
    {
        public static bool Prefix(Pawn pawn, Thing thing, bool forced, ref bool __result)
        {
            // The fallback result is only turned into a job by the optimized JobOnThing path, so both gates must hold.
            if (!AdaptiveStorageIndex.Active || !PickUpAndHaulPerformance.Enabled || pawn?.Map == null || thing == null)
            {
                return true;
            }

            var startTimestamp = PickUpAndHaulAggregateStats.BeginTimer();
            __result = Evaluate(pawn, thing, forced);
            PickUpAndHaulAggregateStats.EndHasJobOnThing(startTimestamp);
            return false;
        }

        private static bool Evaluate(Pawn pawn, Thing thing, bool forced)
        {
            if (!WorkGiver_HaulToInventory.OkThingToHaul(thing, pawn)
                || !WorkGiver_HaulToInventory.IsNotCorpseOrAllowed(thing)
                || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
            {
                return false;
            }

            var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
            if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out _, out _, false))
            {
                return true;
            }

            return AdaptiveStorageReservationCompatibility.TryFindPlanningFallback(
                thing,
                pawn,
                currentPriority,
                logSelection: false,
                out _,
                out _);
        }
    }

    /// <summary>Times HasJobOnThing for the aggregate diagnostics when the fallback prefix above does not take over.</summary>
    [HarmonyPatch(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.HasJobOnThing))]
    public static class Patch_WorkGiver_HasJobOnThing_Timing
    {
        public static void Prefix(ref long __state)
        {
            __state = AdaptiveStorageIndex.Active && PickUpAndHaulPerformance.Enabled
                ? 0L
                : PickUpAndHaulAggregateStats.BeginTimer();
        }

        public static void Postfix(long __state)
        {
            PickUpAndHaulAggregateStats.EndHasJobOnThing(__state);
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

            if (PickUpAndHaulStorageHotpath.IsHaulMoreList(searchSet))
            {
                __result = PickUpAndHaulStorageHotpath.GetClosestAndRemoveSortedOnce(center, map, searchSet, peMode, traverseParams, maxDistance, validator);
                return false;
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

    /// <summary>
    /// Pick Up And Haul runs RemoveWhere on the hauled-item set on every GetHashSet call, even when nothing is null.
    /// Run it only when the set actually contains a null entry (an O(1) check); the resulting set is identical.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CompHauledToInventory_GetHashSet
    {
        private static readonly FieldInfo TakenField = AccessTools.Field(typeof(CompHauledToInventory), "takenToInventory");
        private static AccessTools.FieldRef<CompHauledToInventory, HashSet<Thing>> takenRef;

        public static bool Prepare()
        {
            if (TakenField != null && TakenField.FieldType == typeof(HashSet<Thing>))
            {
                takenRef = AccessTools.FieldRefAccess<CompHauledToInventory, HashSet<Thing>>("takenToInventory");
                return true;
            }

            Log.Warning("[Pick Up And Haul CE Carry Fix] Could not find CompHauledToInventory.takenToInventory; the GetHashSet shortcut is disabled.");
            return false;
        }

        public static MethodBase TargetMethod()
            => AccessTools.Method(typeof(CompHauledToInventory), nameof(CompHauledToInventory.GetHashSet));

        public static bool Prefix(CompHauledToInventory __instance, ref HashSet<Thing> __result)
        {
            if (!PickUpAndHaulStorageHotpath.Enabled || __instance == null)
            {
                return true;
            }

            var set = takenRef(__instance);
            if (set == null)
            {
                return true;
            }

            if (set.Contains(null))
            {
                set.RemoveWhere(x => x == null);
            }

            __result = set;
            return false;
        }
    }

    /// <summary>
    /// CheckIfPawnShouldUnloadInventory allocates a job before it checks whether the pawn has anything to unload, and it runs on
    /// every pass of the colonist AI decision loop. Skip the original under exactly its early-return conditions.
    /// </summary>
    [HarmonyPatch(typeof(PawnUnloadChecker), nameof(PawnUnloadChecker.CheckIfPawnShouldUnloadInventory))]
    public static class Patch_PawnUnloadChecker_EarlyOut
    {
        public static bool Prefix(Pawn pawn)
        {
            if (!PickUpAndHaulStorageHotpath.Enabled)
            {
                return true;
            }

            var comp = pawn?.GetComp<CompHauledToInventory>();
            if (comp == null)
            {
                return false;
            }

            var carriedThing = comp.GetHashSet();
            if (pawn.Faction != Faction.OfPlayerSilentFail
                || !PickUpAndHaul.Settings.IsAllowedRace(pawn.RaceProps)
                || carriedThing == null
                || carriedThing.Count == 0)
            {
                return false;
            }

            var inventoryContainer = pawn.inventory?.innerContainer;
            if (inventoryContainer == null)
            {
                return true;
            }

            return inventoryContainer.Count != 0;
        }
    }
}
