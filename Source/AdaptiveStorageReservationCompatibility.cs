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

        private static readonly MethodInfo FirstUnloadableThingMethod = AccessTools.Method(
            typeof(JobDriver_UnloadYourHauledInventory),
            "FirstUnloadableThing",
            new[] { typeof(Pawn), typeof(HashSet<Thing>) });

        private static readonly AccessTools.FieldRef<JobDriver_UnloadYourHauledInventory, int> CountToDropRef =
            AccessTools.FieldRefAccess<JobDriver_UnloadYourHauledInventory, int>("_countToDrop");

        public static bool ShouldReserveDuringGather(LocalTargetInfo target, Map map)
        {
            if (map == null || !target.IsValid || target.HasThing)
            {
                return true;
            }

            var storageParent = target.Cell.GetSlotGroup(map)?.parent;
            return AdaptiveStorageReservationPolicy.ShouldReserveDuringGather(storageParent?.GetType());
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
                    pawn.Reserve(target, job);
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
            bool acceptSamePriority,
            bool logSelection,
            out IntVec3 foundCell,
            out IHaulDestination haulDestination)
        {
            foundCell = IntVec3.Invalid;
            haulDestination = null;

            var map = pawn?.Map;
            if (thing == null || map == null || !ModsConfig.IsActive("adaptive.storage.framework"))
            {
                return false;
            }

            var origin = thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : pawn.PositionHeld;
            var slotGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            for (var i = 0; i < slotGroups.Count; i++)
            {
                var slotGroup = slotGroups[i];
                var priority = slotGroup.Settings.Priority;
                var priorityEligible = acceptSamePriority
                    ? (int)priority >= (int)currentPriority
                    : (int)priority > (int)currentPriority;
                var isAdaptiveStorage = AdaptiveStorageReservationPolicy.IsAdaptiveStorageType(
                    slotGroup.parent?.GetType());

                if (!priorityEligible || !isAdaptiveStorage || !slotGroup.parent.Accepts(thing))
                {
                    continue;
                }

                var cells = slotGroup.CellsList;
                for (var j = 0; j < cells.Count; j++)
                {
                    var cell = cells[j];
                    if (cell == default)
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
                    var canReserveNow = pawn.CanReserveNew(cell);

                    if (!AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback(
                        isAdaptiveStorage,
                        priorityEligible,
                        otherwiseEligible,
                        canReserveNow))
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

                    return true;
                }
            }

            return false;
        }

        public static Toil MakeFindTargetOrWait(
            JobDriver_UnloadYourHauledInventory driver,
            HashSet<Thing> carriedThings)
        {
            var plannedTarget = driver?.job?.targetB ?? LocalTargetInfo.Invalid;
            var nextAttemptTick = 0;
            var waitingForReservation = false;
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
                if (StoreUtility.TryFindBestBetterStorageFor(
                    thing,
                    pawn,
                    pawn.Map,
                    StoragePriority.Unstored,
                    pawn.Faction,
                    out var cell,
                    out var destination))
                {
                    job.SetTarget(TargetIndex.A, thing);
                    job.SetTarget(TargetIndex.B, cell == IntVec3.Invalid ? (LocalTargetInfo)(destination as Thing) : cell);

                    if (pawn.Map.reservationManager.Reserve(pawn, job, job.targetB))
                    {
                        CountToDropRef(driver) = thing.stackCount;
                        driver.ReadyForNextToil();
                        return;
                    }

                    if (ShouldWaitForReservation(pawn, thing, job.targetB))
                    {
                        WaitAndRetry(pawn, job.targetB, thing);
                        return;
                    }

                    DropAndEnd(pawn, thing, JobCondition.Incompletable);
                    return;
                }

                if (ShouldWaitForReservation(pawn, thing, plannedTarget))
                {
                    job.SetTarget(TargetIndex.B, plannedTarget);
                    WaitAndRetry(pawn, plannedTarget, thing);
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
                nextAttemptTick = Find.TickManager.TicksGame + RetryIntervalTicks;
            }

            void DropAndEnd(Pawn pawn, Thing thing, JobCondition condition)
            {
                pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out _);
                driver.EndJobWith(condition);
            }

            toil.initAction = AttemptUnloadReservation;
            toil.tickAction = () =>
            {
                if (Find.TickManager.TicksGame >= nextAttemptTick)
                {
                    AttemptUnloadReservation();
                }
            };

            return toil;
        }

        private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
        {
            if (FirstUnloadableThingMethod == null)
            {
                return default;
            }

            return (ThingCount)FirstUnloadableThingMethod.Invoke(null, new object[] { pawn, carriedThings });
        }

        private static bool ShouldWaitForReservation(Pawn pawn, Thing thing, LocalTargetInfo target)
        {
            if (pawn?.Map == null || thing == null || !target.IsValid || target.HasThing)
            {
                return false;
            }

            var cell = target.Cell;
            var slotGroup = cell.GetSlotGroup(pawn.Map);
            var isAdaptiveStorage = AdaptiveStorageReservationPolicy.IsAdaptiveStorageType(slotGroup?.parent?.GetType());
            var otherwiseEligible = isAdaptiveStorage
                && slotGroup.parent.Accepts(thing)
                && !cell.IsForbidden(pawn)
                && StoreUtility.IsGoodStoreCell(cell, pawn.Map, thing, null, null)
                && pawn.Map.reachability.CanReach(
                    pawn.PositionHeld,
                    cell,
                    PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn, Danger.Deadly));
            var canReserveNow = pawn.CanReserveNew(target);

            return AdaptiveStorageReservationPolicy.ShouldRetryUnload(
                isAdaptiveStorage,
                otherwiseEligible,
                canReserveNow);
        }
    }

    [HarmonyPatch]
    public static class Patch_AdaptiveStorage_FindTargetOrDrop
    {
        public static MethodBase TargetMethod()
            => AccessTools.Method(
                typeof(JobDriver_UnloadYourHauledInventory),
                "FindTargetOrDrop",
                new[] { typeof(HashSet<Thing>) });

        public static bool Prefix(
            JobDriver_UnloadYourHauledInventory __instance,
            HashSet<Thing> carriedThings,
            ref Toil __result)
        {
            if (!ModsConfig.IsActive("adaptive.storage.framework"))
            {
                return true;
            }

            __result = AdaptiveStorageReservationCompatibility.MakeFindTargetOrWait(__instance, carriedThings);
            return false;
        }
    }
}
