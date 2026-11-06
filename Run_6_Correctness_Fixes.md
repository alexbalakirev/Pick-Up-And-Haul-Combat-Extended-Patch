# Plan: PUAH CE Carry Fix - correctness fixes (Run 6)

## Context

A logic review of `PickUpAndHaulCECarryFix` and Pick Up And Haul (PUAH) found correctness bugs. A recheck after the Run 5 performance work showed that Run 5 fixed only the exception-safety issue (the `Finalizer` on `JobOnThing`). Everything else was still present:

1. **Pawns ignore the planned pickup count.** `HarmonyTargetDiscovery` only matched nested types whose *name* contains `MakeNewToils`. PUAH's take-thing and haul-more toils are compiled into `<>c__DisplayClass1_0::<MakeNewToils>b__N`, so both MakeNewToils transpilers (`Patch_JobDriver_MakeNewToils_CePickupCount` and `..._WorkGiverLookupCache`) silently did nothing. CE pickup took everything that fits, and storage-limited remainders were dropped at the storage.
2. **The Adaptive Storage "hold for the planned cell" never fired.** `MakeFindTargetOrWait` read `job.targetB`, but PUAH creates the unload job with `MakeJob(Unload, storeCell)`, which sets `targetA`. Run 5's retry machinery and its scenario (c) were unreachable.
3. **Planning and gathering used different limits.** The planner fills CE capacity to 100%; the gather toil aborted at the 80% `CeOverweight` threshold.
4. **Unloading picked up the pawn's own stacks.** A second same-type stack in a trip is merged by `ThingOwner.TryAdd(item, canMerge: true)` into the first matching inventory stack, which may be the pawn's own (CE ammo, medicine, meals). Unload's "replacement" step then dumped that whole personal stack.
5. **Partly placed items did not count against the inventory budget.** An item that `AllocateThingAtCell` queues with a storage-limited partial count (it returns `false`) was not added to planned capacity, so later items were over-planned.
6. **Low-severity issues:** leftover `targetQueueB` reservations when a zero-fit item is removed from the plan; exact-fill float rounding sent the plan into `CountThatFitsBeforeScalarCapacity` (full-stack math); `ReserveAdditionalGatherDestinations` used `errorOnFailed: true`, which logs red errors.

Decisions (from the user):
- Gather to 100%. The 80% setting is only the "start a trip" threshold.
- When a pawn takes exactly the planned partial amount, leave the remainder and keep going.
- All fixes are always on, with no new settings toggles.

Constraints (AGENTS.md): edit only `Mods\PickUpAndHaulCECarryFix`; PUAH, CE and saves are read-only, so every change is a Harmony patch; Allman braces, 4-space indentation, `PickUpAndHaulCECarryFix` namespace; no commits unless asked.

## Fixes

### Fix 1: transpiler target discovery, plus pickup count
- `Source\HarmonyTargetDiscovery.cs`: `GetDeclaredMethods(containingType, fragment)` now also returns methods named `<fragment>b__...` in any directly nested type (closure display classes, `<>c`) and on the containing type itself (lambdas capturing only `this`). The nested-type-name rule stays (iterator `MoveNext`). Results are de-duplicated.
- `Patch_JobDriver_MakeNewToils_CePickupCount` and `..._WorkGiverLookupCache` count their replacements in `TranspilerStats`; `Startup` logs one `Log.Warning` after `PatchAll` if the pickup-count, lookup-cache or gather-check counter is 0.

### Fix 1b: leave a planned remainder and keep going
- New pure `Source\PartialPickupPolicy.cs`: `LeaveRemainder(planned, picked, stack) => picked > 0 && picked == planned && planned < stack`.
- `CeInventoryUtility.CountThatFitsCurrentJob` records a `[ThreadStatic]` marker (thing, leave-remainder). The take-thing transpiler replaces the `Thing.Spawned` check that follows the `CanFitInInventory` call with `CeInventoryUtility.RemainderNeedsSeparateHaul(Thing)` (`Spawned && !marker`, clears the marker). PUAH's end-trip and hand-haul branch now only runs when the pawn took less than planned.

### Fix 2: Adaptive Storage planned target
- `Source\AdaptiveStorageReservationCompatibility.cs` (`MakeFindTargetOrWait`): at toil construction, if `job.targetC` is invalid and `job.targetA` is a cell, copy `targetA` into `targetC` (saved with the job, so the hold survives save/load). `plannedTarget` comes from `targetC`. Vanilla `Toils_Haul` writes `targetC` only when `targetQueueB` is non-empty; PUAH's unload job never fills it.
- The hold/retry policy (`AdaptiveStorageReservationPolicy`) and Run 5's 30/120-tick retry loop are unchanged.

### Fix 3: gather to 100%
- `Patch_JobDriver_CheckForOverencumberedForCombatExtended` makes two replacements in `<CheckForOverencumberedForCombatExtended>b__0`: `CompatHelper.CeOverweight` -> `CeInventoryUtility.NextGatherTargetDoesNotFit(Pawn)` (`CountThatFits(...) <= 0`), and `MassUtility.EncumbrancePercent` -> `CeInventoryUtility.GatherAbortRatio(Pawn)` (the CE ratio only when over capacity, else 0).
- `Patch_CompatHelper_CeOverweight` and `OverAllowedGearCapacity` keep the 80% start threshold.

### Fix 4: never unload personal stacks
- Take-thing transpiler: `ThingOwner.TryAdd(Thing, bool)` -> `CeInventoryUtility.TryAddHauled(ThingOwner, Thing, bool)`: absorb into a stack tracked by `CompHauledToInventory` that `CanStackWith` the new thing, then `TryAdd(thing, false)` for any remainder; falls back to `owner.TryAdd(thing, canMerge)` when the pawn cannot be resolved.
- `Patch_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing` prefix always calls `PickUpAndHaulUnloadRouting.PruneStaleTrackedThings` first (also when routing is disabled), removing tracked entries that are not in `pawn.inventory.innerContainer`. The "replacement same-type stack" logic in `TrySelectNextUnloadableThing` is removed.
- Documented limits: hauled items already merged into personal stacks in existing saves stay with the pawn; other jobs merging personal items into a tracked stack are out of scope.

### Fix 5: planner bookkeeping
- `TryMakeOptimizedJob`: the per-item capacity block is a local function `CommitQueuedItem`, used when `AllocateThingAtCell` returns `true` and when it returns `false` but queued the item with a partial count (queue A grew and `countQueue` has the same length as queue A).
- Zero-fit removal also `RemoveRange`s the queue-B entries added during that `AllocateThingAtCell` call.
- CE path: after `plannedCapacity.Add`, `CapacityMath.IsFull(ratio)` (`ratio >= 1 - Epsilon`) ends planning with the count kept; `CountPastCapacity` stays for the non-CE path only.
- PUAH's own (non-optimized) `JobOnThing` still has finding #5. It only runs when the performance patch is disabled.

### Fix 6: quiet extra reservations
- `ReserveAdditionalGatherDestinations` uses `pawn.Reserve(target, job, errorOnFailed: false)`, matching PUAH's `ReserveAsManyAsPossible`.

---

# Implementation Status

Status rules: `[x]` means implemented and verified by a clean build and the unit tests. In-game checks remain `[ ]`.

## Tasks
- [x] Phase 0: this file; Run 5 verification scenario (c) marked blocked by Run 6 fix 2.
- [x] Fix 1: discovery covers display-class and this-capturing lambdas; transpiler replacement counters; startup warning.
- [x] Fix 1b: `PartialPickupPolicy`, marker in `CountThatFitsCurrentJob`, `RemainderNeedsSeparateHaul`.
- [x] Fix 2: `targetC` persistence; planned target read from `targetC`.
- [x] Fix 3: `NextGatherTargetDoesNotFit`, `GatherAbortRatio`.
- [x] Fix 4: `TryAddHauled`, stale-entry prune, replacement substitution removed.
- [x] Fix 5: `CommitQueuedItem`, queue-B trim, `CapacityMath.IsFull`.
- [x] Fix 6: `errorOnFailed: false`.
- [x] Tests (`Source\RouteOrdering.Tests`): 30 cases pass (26 existing + 4 new: discovery against a real compiled iterator with capturing lambdas asserting the IL `call` to a marker, `PartialPickupPolicy` cases, `CapacityMath.IsFull` exact fill).

## Validation
- [x] Build Release for `RimWorldVersion=1.6` and `1.7` (0 warnings, 0 errors).
- [x] `dotnet run --project Source\RouteOrdering.Tests\RouteOrdering.Tests.csproj`: all pass.
- [x] Standalone repro against the new `HarmonyTargetDiscovery.cs`: "RESULT: discovery covers the call site".
- [ ] In-game on 1.7, diagnostics on, with the active save's mods:
  - [ ] Startup: `[Pick Up And Haul CE Carry Fix] Loaded.`, no "transpiler matched nothing" warning, no Harmony errors.
  - [ ] Storage-limited partial: one stockpile cell with room for 20 and a loose 75 stack. The pawn takes 20, continues its queue, stores 20, and leaves 55 where they were. Nothing is dropped at the stockpile.
  - [ ] Gather to 100%: a pawn starting below 80% plans several items; the trip does not end at 80% and reaches about 100% CE capacity.
  - [ ] Personal stacks: a pawn carrying personal medicine or a CE ammo loadout hauls two loose stacks of the same type; after unloading, the personal stack count is unchanged.
  - [ ] Adaptive Storage wait (Run 5 scenario (c) plus 8/16 spec runtime acceptance #2): the pawn waits at a reserved higher-priority shelf and delivers once it is released, with no drop and no re-haul; save and reload mid-wait still waits; the "waiting for Adaptive Storage destination" diagnostic appears.
  - [ ] Log: no `Could not reserve` errors from gather reservations.
  - [ ] Run 5's pending checks: scenarios (a), (b), (d), (e), plus an `unload route` diagnostic line.
- [x] Workshop and save inputs were not written by this change.

## Notes
- Dependency assumptions: PUAH 1279012058 (compiled structure: `<>c__DisplayClass1_0::<MakeNewToils>b__2` take-thing toil, `<CheckForOverencumberedForCombatExtended>b__0`), CE `CompInventory.CanFitInInventory`, RimWorld 1.7.5000 on the Waylith .NET 10 host; 1.6 is compile-verified only.
- `TryAddHauled` absorbs through `Thing.TryAbsorbStack`, which does not notify the `ThingOwner`; the take-thing toil calls `CompatHelper.UpdateInventory` right afterwards, which refreshes CE's cache.
