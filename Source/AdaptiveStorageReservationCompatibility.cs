using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PickUpAndHaul;
using RimWorld;
using Verse;
using Verse.AI;

namespace PickUpAndHaulCECarryFix
{
    public static class AdaptiveStorageReservationCompatibility
    {
        private const int RetryIntervalTicks = 30;

        private const int FullReattemptIntervalTicks = 120;

        private static readonly MethodInfo FirstUnloadableThingMethod = AccessTools.Method(
            typeof(JobDriver_UnloadYourHauledInventory),
            "FirstUnloadableThing",
            new[] { typeof(Pawn), typeof(HashSet<Thing>) });

        // Created on first use (after Harmony has patched the target) so the delegate always reaches the patched method.
        private static Func<Pawn, HashSet<Thing>, ThingCount> firstUnloadableThingDelegate;

        private static readonly AccessTools.FieldRef<JobDriver_UnloadYourHauledInventory, int> CountToDropRef =
            AccessTools.FieldRefAccess<JobDriver_UnloadYourHauledInventory, int>("_countToDrop");

        public static bool ShouldReserveDuringGather(LocalTargetInfo target, Map map)
        {
            if (map == null || !target.IsValid || target.HasThing)
            {
                return true;
            }

            var storageParent = target.Cell.GetSlotGroup(map)?.parent;
            return !AdaptiveStorageIndex.IsAdaptiveStorageType(storageParent?.GetType());
        }

        public static void ReserveAdditionalGatherDestinations(Pawn pawn, Job job)
        {
            if (pawn?.Map == null || job?.targetQueueB.NullOrEmpty() != false)
            {
                return;
            }

            for (var i = 0; i < job.targetQueueB.Count; i++)
            {
                var target = job.targetQueueB[i];
                if (ShouldReserveDuringGather(target, pawn.Map))
                {
                    pawn.Reserve(target, job, errorOnFailed: false);
                }
            }
        }

        public static bool ReservePrimaryGatherDestination(Pawn pawn, Job job)
        {
            if (pawn?.Map == null || job == null)
            {
                return false;
            }

            return !ShouldReserveDuringGather(job.targetB, pawn.Map) || pawn.Reserve(job.targetB, job);
        }

        internal static bool TryFindPlanningFallback(
            Thing thing,
            Pawn pawn,
            StoragePriority currentPriority,
            bool logSelection,
            out IntVec3 foundCell,
            out IHaulDestination haulDestination)
        {
            foundCell = IntVec3.Invalid;
            haulDestination = null;

            var map = pawn?.Map;
            if (thing == null || map == null || !AdaptiveStorageIndex.Active)
            {
                return false;
            }

            var origin = thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : pawn.PositionHeld;
            var currentPriorityValue = (int)currentPriority;

            // The fallback only succeeds for cells the pawn cannot reserve right now, and a cell is only unreservable
            // when a reservation targets it. Walk just the reserved cells when the index is available.
            Dictionary<SlotGroup, List<IntVec3>> reservedCells = null;
            if (PickUpAndHaulStorageHotpath.Enabled && pawn.Spawned)
            {
                AdaptiveStorageIndex.TryGetReservedCells(map, out reservedCells);
            }

            var cellsExamined = 0;
            var slotGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            for (var i = 0; i < slotGroups.Count; i++)
            {
                var slotGroup = slotGroups[i];
                var candidatePriority = (int)slotGroup.Settings.Priority;
                if (candidatePriority <= currentPriorityValue
                    || !AdaptiveStorageIndex.IsAdaptiveStorageType(slotGroup.parent?.GetType()))
                {
                    continue;
                }

                List<IntVec3> cells;
                if (reservedCells != null)
                {
                    if (!reservedCells.TryGetValue(slotGroup, out cells))
                    {
                        continue;
                    }
                }
                else
                {
                    cells = slotGroup.CellsList;
                }

                if (!slotGroup.parent.Accepts(thing))
                {
                    continue;
                }

                for (var j = 0; j < cells.Count; j++)
                {
                    var cell = cells[j];
                    if (cell == default)
                    {
                        continue;
                    }

                    cellsExamined++;

                    // Cheapest decisive check first: the policy requires the cell to be unreservable right now.
                    if (pawn.CanReserveNew(cell))
                    {
                        continue;
                    }

                    var otherwiseEligible = !cell.IsForbidden(pawn)
                        && StoreUtility.IsGoodStoreCell(cell, map, thing, null, null)
                        && map.reachability.CanReach(
                            origin,
                            cell,
                            PathEndMode.ClosestTouch,
                            TraverseParms.For(pawn, Danger.Deadly));

                    if (!AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
                        true,
                        candidatePriority,
                        currentPriorityValue,
                        otherwiseEligible,
                        false))
                    {
                        continue;
                    }

                    foundCell = cell;
                    haulDestination = slotGroup.parent;
                    if (logSelection && PickUpAndHaulDiagnostics.Enabled)
                    {
                        Log.Message(
                            $"[Pick Up And Haul CE Carry Fix] {pawn} planning {thing} through temporary Adaptive Storage reservation at {cell}.");
                    }

                    PickUpAndHaulAggregateStats.CountAdaptiveFallback(cellsExamined);
                    return true;
                }
            }

            PickUpAndHaulAggregateStats.CountAdaptiveFallback(cellsExamined);
            return false;
        }

        public static Toil MakeFindTargetOrWait(
            JobDriver_UnloadYourHauledInventory driver,
            HashSet<Thing> carriedThings)
        {
            // PUAH creates the unload job as MakeJob(Unload, storeCell), so the planned cell is targetA. targetA is overwritten by
            // each selection, so persist it in targetC (saved with the job; vanilla only writes targetC when targetQueueB is
            // non-empty, which this job never fills).
            var plannedTarget = LocalTargetInfo.Invalid;
            var plannedJob = driver?.job;
            if (plannedJob != null)
            {
                if (!plannedJob.targetC.IsValid && plannedJob.targetA.IsValid && !plannedJob.targetA.HasThing)
                {
                    plannedJob.targetC = plannedJob.targetA;
                }

                if (plannedJob.targetC.IsValid && !plannedJob.targetC.HasThing)
                {
                    plannedTarget = plannedJob.targetC;
                }
            }

            var nextAttemptTick = 0;
            var lastFullAttemptTick = int.MinValue;
            var waitingForReservation = false;
            Thing waitThing = null;
            LocalTargetInfo waitTarget = LocalTargetInfo.Invalid;
            var toil = new Toil
            {
                defaultCompleteMode = ToilCompleteMode.Never
            };

            void AttemptUnloadReservation()
            {
                var pawn = driver?.pawn;
                var job = driver?.job;
                if (pawn?.Map == null || job == null || carriedThings == null)
                {
                    driver?.EndJobWith(JobCondition.Incompletable);
                    return;
                }

                lastFullAttemptTick = Find.TickManager.TicksGame;
                var unloadableThing = FirstUnloadableThing(pawn, carriedThings);
                if (unloadableThing.Count == 0)
                {
                    if (carriedThings.Count == 0)
                    {
                        driver.EndJobWith(JobCondition.Succeeded);
                    }
                    else
                    {
                        driver.ReadyForNextToil();
                    }

                    return;
                }

                var thing = unloadableThing.Thing;
                IntVec3 cell;
                IHaulDestination destination;
                bool foundNormalStorage;
                if (PickUpAndHaulUnloadRouting.TryTakeSelectionMemo(pawn, thing, out cell, out destination))
                {
                    foundNormalStorage = true;
                }
                else
                {
                    foundNormalStorage = StoreUtility.TryFindBestBetterStorageFor(
                        thing,
                        pawn,
                        pawn.Map,
                        StoragePriority.Unstored,
                        pawn.Faction,
                        out cell,
                        out destination);
                    PickUpAndHaulUnloadRouting.RefreshDestination(pawn, thing, foundNormalStorage, cell, destination);
                }

                var normalPriority = foundNormalStorage
                    ? StoragePriorityOf(cell, destination, pawn.Map)
                    : StoragePriority.Unstored;

                // Wait only when using the normal fallback would create an immediate lower-priority re-haul loop.
                if (AdaptiveStorageIndex.Active
                    && ShouldHoldPlannedUnloadTarget(pawn, thing, plannedTarget, foundNormalStorage, normalPriority))
                {
                    job.SetTarget(TargetIndex.B, plannedTarget);
                    WaitAndRetry(pawn, plannedTarget, thing);
                    return;
                }

                if (foundNormalStorage)
                {
                    job.SetTarget(TargetIndex.A, thing);
                    job.SetTarget(TargetIndex.B, cell == IntVec3.Invalid ? (LocalTargetInfo)(destination as Thing) : cell);

                    if (pawn.Map.reservationManager.Reserve(pawn, job, job.targetB))
                    {
                        CountToDropRef(driver) = thing.stackCount;
                        driver.ReadyForNextToil();
                        return;
                    }

                    if (AdaptiveStorageIndex.Active && ShouldWaitForReservation(pawn, thing, job.targetB))
                    {
                        WaitAndRetry(pawn, job.targetB, thing);
                        return;
                    }

                    DropAndEnd(pawn, thing, JobCondition.Incompletable);
                    return;
                }

                DropAndEnd(pawn, thing, JobCondition.Succeeded);
            }

            void WaitAndRetry(Pawn pawn, LocalTargetInfo target, Thing thing)
            {
                if (!waitingForReservation && PickUpAndHaulDiagnostics.Enabled)
                {
                    Log.Message(
                        $"[Pick Up And Haul CE Carry Fix] {pawn} waiting for Adaptive Storage destination {target} to unload {thing}.");
                }

                waitingForReservation = true;
                waitThing = thing;
                waitTarget = target;
                nextAttemptTick = Find.TickManager.TicksGame + RetryIntervalTicks;
            }

            // While waiting, re-check only whether the planned target is still blocked and eligible. Redo the full
            // selection when the target became reservable, stopped being eligible, or enough time has passed.
            bool StillWaitingOnSameTarget(int tick)
            {
                if (!waitingForReservation
                    || waitThing == null
                    || tick - lastFullAttemptTick >= FullReattemptIntervalTicks)
                {
                    return false;
                }

                var pawn = driver?.pawn;
                if (pawn?.Map == null || pawn.inventory?.innerContainer == null || !pawn.inventory.innerContainer.Contains(waitThing))
                {
                    return false;
                }

                if (!TryGetReservationState(
                    pawn,
                    waitThing,
                    waitTarget,
                    out var isAdaptiveStorage,
                    out var otherwiseEligible,
                    out var canReserveNow,
                    out _))
                {
                    return false;
                }

                return AdaptiveStorageReservationPolicy.ShouldRetryUnload(isAdaptiveStorage, otherwiseEligible, canReserveNow);
            }

            void DropAndEnd(Pawn pawn, Thing thing, JobCondition condition)
            {
                pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out _);
                driver.EndJobWith(condition);
            }

            toil.initAction = AttemptUnloadReservation;
            toil.tickAction = () =>
            {
                var tick = Find.TickManager.TicksGame;
                if (tick < nextAttemptTick)
                {
                    return;
                }

                if (StillWaitingOnSameTarget(tick))
                {
                    PickUpAndHaulAggregateStats.CountUnloadRetryTick();
                    nextAttemptTick = tick + RetryIntervalTicks;
                    return;
                }

                PickUpAndHaulAggregateStats.CountUnloadRetryTick();
                AttemptUnloadReservation();
            };

            return toil;
        }

        private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
        {
            if (FirstUnloadableThingMethod == null)
            {
                return default;
            }

            if (firstUnloadableThingDelegate == null)
            {
                firstUnloadableThingDelegate = AccessTools.MethodDelegate<Func<Pawn, HashSet<Thing>, ThingCount>>(FirstUnloadableThingMethod);
            }

            return firstUnloadableThingDelegate(pawn, carriedThings);
        }

        private static bool ShouldWaitForReservation(Pawn pawn, Thing thing, LocalTargetInfo target)
        {
            if (!TryGetReservationState(
                pawn,
                thing,
                target,
                out var isAdaptiveStorage,
                out var otherwiseEligible,
                out var canReserveNow,
                out _))
            {
                return false;
            }

            return AdaptiveStorageReservationPolicy.ShouldRetryUnload(
                isAdaptiveStorage,
                otherwiseEligible,
                canReserveNow);
        }

        private static bool ShouldHoldPlannedUnloadTarget(
            Pawn pawn,
            Thing thing,
            LocalTargetInfo target,
            bool hasNormalDestination,
            StoragePriority normalPriority)
        {
            if (!TryGetReservationState(
                pawn,
                thing,
                target,
                out var isAdaptiveStorage,
                out var otherwiseEligible,
                out var canReserveNow,
                out var plannedPriority))
            {
                return false;
            }

            return AdaptiveStorageReservationPolicy.ShouldHoldPlannedUnloadTarget(
                isAdaptiveStorage,
                otherwiseEligible,
                canReserveNow,
                hasNormalDestination,
                (int)plannedPriority,
                (int)normalPriority);
        }

        private static bool TryGetReservationState(
            Pawn pawn,
            Thing thing,
            LocalTargetInfo target,
            out bool isAdaptiveStorage,
            out bool otherwiseEligible,
            out bool canReserveNow,
            out StoragePriority priority)
        {
            isAdaptiveStorage = false;
            otherwiseEligible = false;
            canReserveNow = false;
            priority = StoragePriority.Unstored;
            if (pawn?.Map == null || thing == null || !target.IsValid || target.HasThing)
            {
                return false;
            }

            var cell = target.Cell;
            var slotGroup = cell.GetSlotGroup(pawn.Map);
            isAdaptiveStorage = AdaptiveStorageIndex.IsAdaptiveStorageType(slotGroup?.parent?.GetType());
            priority = slotGroup?.Settings.Priority ?? StoragePriority.Unstored;
            otherwiseEligible = isAdaptiveStorage
                && slotGroup.parent.Accepts(thing)
                && !cell.IsForbidden(pawn)
                && StoreUtility.IsGoodStoreCell(cell, pawn.Map, thing, null, null)
                && pawn.Map.reachability.CanReach(
                    pawn.PositionHeld,
                    cell,
                    PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn, Danger.Deadly));
            canReserveNow = pawn.CanReserveNew(target);

            return true;
        }

        private static StoragePriority StoragePriorityOf(
            IntVec3 cell,
            IHaulDestination destination,
            Map map)
        {
            if (cell != IntVec3.Invalid)
            {
                return cell.GetSlotGroup(map)?.Settings.Priority ?? StoragePriority.Unstored;
            }

            return destination?.GetStoreSettings()?.Priority ?? StoragePriority.Unstored;
        }

    }

    [HarmonyPatch]
    public static class Patch_AdaptiveStorage_FindTargetOrDrop
    {
        private static readonly MethodBase Target = AccessTools.Method(
            typeof(JobDriver_UnloadYourHauledInventory),
            "FindTargetOrDrop",
            new[] { typeof(HashSet<Thing>) });

        public static bool Prepare()
        {
            if (Target != null)
            {
                return true;
            }

            Log.Warning(
                "[Pick Up And Haul CE Carry Fix] Could not find JobDriver_UnloadYourHauledInventory.FindTargetOrDrop; Adaptive Storage unload waiting and unload selection memo are disabled.");
            return false;
        }

        public static MethodBase TargetMethod() => Target;

        public static bool Prefix(
            JobDriver_UnloadYourHauledInventory __instance,
            HashSet<Thing> carriedThings,
            ref Toil __result)
        {
            // One toil serves both paths: the Adaptive Storage hold/retry branches are skipped inside it when that mod
            // is absent, which matches Pick Up And Haul's own toil.
            if (!AdaptiveStorageIndex.Active && !PickUpAndHaulUnloadRouting.Enabled)
            {
                return true;
            }

            __result = AdaptiveStorageReservationCompatibility.MakeFindTargetOrWait(__instance, carriedThings);
            return false;
        }
    }
}
