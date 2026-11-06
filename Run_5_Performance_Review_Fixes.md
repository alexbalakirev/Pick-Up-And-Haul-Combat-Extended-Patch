# Plan: PUAH CE Carry Fix — performance review fixes (Run 5)

## Context

A performance review of `PickUpAndHaulCECarryFix` (local) and Pick Up And Haul (Workshop 1279012058, read-only) found:
- algorithmic hot spots that the fix mod introduced or kept, and
- earlier "optimizations" that cost more than they save.

The worst case is "storage is full with many loose items on the map", and that case is also the most common one.

Environment facts this plan relies on (verified):
- **Game and runtime:** the game is **RimWorld 1.7.5000** running on the Waylith .NET 10 CoreCLR runtime. Algorithmic cost matters far more than allocations.
- **Mod versions:** PUAH and CE (16.7.4.0) ship only 1.6 DLLs. Adaptive Storage Framework, ASF Deep Storage and Allow Tool are active. KeyzAllowUtilities patches PUAH's `JobOnThing` and `PotentialWorkThingsGlobal`.
- **The build is broken on this machine:**
  - The `RimWorldWin64_Data\Managed\*` HintPaths don't exist.
  - The Harmony HintPath `2009463077\Current\...` doesn't exist either. That folder now has `1.4`–`1.7` subfolders.
- **Reference package:** `Krafs.Rimworld.Ref` publishes `1.6.4871` (stable) and `1.7.5000-unstable`, which matches the installed game.
- **Vanilla facts (checked in the IL):**
  - `PawnCanAutomaticallyHaulFast` already does `CanReach` and `CanReserve`.
  - `IsGoodStoreCell` checks reservation, `NoStorageBlockersIn` and `CanReach`.
  - `ModsConfig.IsActive` does `ToLower` plus a hash lookup.
  - `GetComp` is dictionary-cached.

Intended outcome:
- Remove the multiplicative scans (haulables × adaptive cells; m² unload searches; eager validation).
- Keep observable hauling behavior. Any intentional behavior change sits behind an existing settings toggle.
- Build natively for 1.6 and 1.7.

Hard constraints (AGENTS.md):
- Edit only `Mods\PickUpAndHaulCECarryFix`.
- Workshop folders and saves are read-only, so every change to PUAH behavior is a Harmony patch inside the fix mod.
- Allman braces, 4-space indentation, keep the `PickUpAndHaulCECarryFix` namespace.

## Phase 0 — Record the plan in the mod

- Copy this plan to `PickUpAndHaulCECarryFix\Run_5_Performance_Review_Fixes.md`, following the existing `Run_1`..`Run_4` convention.
- Use checkbox task tracking like the earlier runs, and tick items as they're verified.

## Phase 1 — Build infrastructure (dual 1.6 + 1.7)

File: `Source\PickUpAndHaulCECarryFix.csproj`
- Add a `RimWorldVersion` property, defaulting to `1.7`.
- Replace the `Assembly-CSharp` and `UnityEngine.CoreModule` references with:
  `<PackageReference Include="Krafs.Rimworld.Ref" Version="1.7.5000-unstable" ExcludeAssets="runtime" PrivateAssets="all" />`
  When `RimWorldVersion == 1.6`, use version `1.6.4871` instead (conditional `ItemGroup`).
- Harmony: point the HintPath at `...\2009463077\$(RimWorldVersion)\Assemblies\0Harmony.dll`, with `Private=false`.
- Keep the PUAH, IHoldMultipleThings and CE HintPaths. They exist: PUAH `1.6\Assemblies`, CE root `Assemblies`.
- Set `OutputPath` to `..\$(RimWorldVersion)\Assemblies\` and `IntermediateOutputPath` to `obj\$(RimWorldVersion)\`.

Mod layout:
- Add `LoadFolders.xml`:
  - `v1.6` → `/` and `1.6`
  - `v1.7` → `/` and `1.7`
- Remove the root `Assemblies\` DLL and PDB. They're build output, and with `/` in LoadFolders they would load twice.
- `About\About.xml`: add `<li>1.7</li>` to `supportedVersions`.

Build commands, run sequentially:
```powershell
dotnet build .\PickUpAndHaulCECarryFix\Source\PickUpAndHaulCECarryFix.csproj -c Release -p:RimWorldVersion=1.6
dotnet build .\PickUpAndHaulCECarryFix\Source\PickUpAndHaulCECarryFix.csproj -c Release -p:RimWorldVersion=1.7
```
- Fix any 1.7 API compile errors here, before touching behavior.
- Acceptance: both configurations build with no errors, and the current code loads in-game on 1.7. That gives a baseline DLL.

## Phase 2 — Instrumentation (capture the baseline before any fix)

The existing diagnostics only time `JobOnThing`. Extend `PickUpAndHaulDiagnostics` (`Source\PickUpAndHaulCECarryFix.cs`) with **aggregated** counters:
- Accumulate in static fields and log one summary line every 2500 ticks while diagnostics are enabled. Do not log per call, or `HasJobOnThing` would spam the log.

Counters:
- `HasJobOnThing` calls, total ms, adaptive fallback invocations, adaptive cells examined.
- Unload selections, storage searches per selection, retry ticks.
- Haul-more toil: list size, items popped, `JobOnThing` validator calls.
- `TryMakeOptimizedJob`: candidates in radius, validator calls, stat evaluations (optional).

Use `Stopwatch.GetTimestamp()`, which doesn't allocate. Then capture baseline numbers in-game for the scenarios in Verification.

## Phase 3 — Fixes (ordered by impact)

### F1. Adaptive Storage fallback in `HasJobOnThing` (high)

Files: `AdaptiveStorageReservationCompatibility.cs`, `AdaptiveStorageReservationPolicy.cs`, new `AdaptiveStorageIndex.cs`, `PickUpAndHaulCECarryFix.cs` (`Patch_WorkGiver_HasJobOnThing_AdaptiveStorageReservation`).

1. **Behavior-identical reorder.** In `TryFindPlanningFallback`, compute `pawn.CanReserveNew(cell)` first and `continue` when it's true. The policy only succeeds when the cell is not reservable now.
   - After that, check `!IsForbidden` → `IsGoodStoreCell` → `CanReach`.
   - Keep `AdaptiveStorageReservationPolicy.ShouldUsePlanningFallback` as the final decision.
2. **Cached gates:**
   - Read `ModsConfig.IsActive("adaptive.storage.framework")` once, into a static readonly bool `AdaptiveStorageIndex.Active`.
   - Use it in `TryFindPlanningFallback` and in `Patch_AdaptiveStorage_FindTargetOrDrop`.
   - Memoize `IsAdaptiveStorageType` per `Type` in a `Dictionary<Type, bool>`. The pure policy method stays as it is for tests.
3. **Reserved-cell index (gated by `StorageHotpathCleanupEnabled`):**
   - Build, per map and per tick (`Find.TickManager.TicksGame`), a `Dictionary<SlotGroup, List<IntVec3>>` of cells that have a reservation and belong to an adaptive slot group.
   - Source: the map's `ReservationManager` reservation list. Confirm the 1.7 member name (e.g. `ReservationsReadOnly`) with the IL dumper first. If no enumerable is exposed, ship step 1 only.
   - `TryFindPlanningFallback` then walks `AllGroupsListInPriorityOrder`, skips groups not in the index, and tests only the indexed cells.
   - This cuts the cost from O(haulables × adaptive cells) to O(haulables × reserved cells).
   - Accepted difference: within a group, cell order follows reservation order instead of `CellsList` order.
4. **Single evaluation in `HasJobOnThing`:**
   - Replace the postfix with a prefix that re-implements PUAH's four-line `HasJobOnThing` (`WorkGiver_HaulToInventory.cs:46-50`) and returns false (skips the original). It runs `OkThingToHaul && IsNotCorpseOrAllowed && PawnCanAutomaticallyHaulFast` once, then the storage search, then the fallback.
   - The cheap checks are no longer evaluated twice.
   - The fallback is gated on `AdaptiveStorageIndex.Active`, not on `PerformancePatchEnabled`. It's a correctness fix, not a performance option.
5. Add the F7 memo store here (see F7).

### F2. Lazy candidate validation in `TryMakeOptimizedJob` (medium-high; under `PerformancePatchEnabled`)

File: `PickUpAndHaulCECarryFix.cs` (`BuildCandidateList`, `GetNextCandidate`), new `CandidateSelection.cs` (pure helper).
- `BuildCandidateList`: filter by distance only.
  - Collect every haulable within `distanceToSearchMore` of the first thing, as `(Thing, RouteScore)` entries.
  - Run **no validator** at this stage.
- `GetNextCandidate`:
  - Score the remaining entries once per call, against the fixed center.
  - Pick the best with `RouteOrdering.FindBestIndex` / `Compare` (reuse `RouteOrdering.cs`).
  - Check `Spawned`, `CanReach` and the validator on that pick only.
  - Remove it with swap-remove.
  - Drop the per-iteration full `Spawned` sweep and rescoring.
- Reinterpret `OpportunisticCandidateCap` as the **maximum validator calls per job plan**. This preserves the "bounded work" guarantee without the "a cap may hide valid items" pitfall noted in Run 3. Update the settings label and tooltip text.
- Put the pick/validate loop in `CandidateSelection` as a generic helper over `RouteScore` with a `Func<int, bool>` validator, so it can be unit-tested.

### F3. Unload routing (medium; under `UnloadRouteOptimizationEnabled`)

Files: `PickUpAndHaulCECarryFix.cs` (`PickUpAndHaulUnloadRouting`), `AdaptiveStorageReservationCompatibility.cs` (`MakeFindTargetOrWait`, `FirstUnloadableThing`), new `UnloadDestinationCache.cs` (pure ranking part).

- **Per-job ranking cache:**
  - Keyed by pawn `thingIDNumber` plus `job.loadID`. Holds `Dictionary<ThingDef, IntVec3>` destination positions.
  - `TrySelectNextUnloadableThing` runs one storage search per **distinct def** not yet cached, ranks every item by cached distance, and selects the nearest.
  - Total searches per unload job fall from about m²/2 to about (distinct defs + m).
- **Selection memo:**
  - Record `(pawn, thing, cell / destination, tick)` for the chosen item.
  - The unload toil reuses it when it's from the same tick and still valid: `IsGoodStoreCell` for a cell, `Accepts` + `GetCountCanAccept > 0` for a container. Otherwise it searches.
  - The search result refreshes that def's cache entry.
- **One toil for both paths:**
  - Generalize `Patch_AdaptiveStorage_FindTargetOrDrop` to replace PUAH's `FindTargetOrDrop` when Adaptive Storage is active **or** unload routing is enabled.
  - Without Adaptive Storage, the hold/retry branch is skipped and the toil completes immediately, matching PUAH's semantics.
- **No reflection:** replace `FirstUnloadableThingMethod.Invoke` with a cached `AccessTools.MethodDelegate<Func<Pawn, HashSet<Thing>, ThingCount>>`.
- **Retry loop:**
  - On each 30-tick retry, first re-check only `pawn.CanReserveNew(plannedTarget)` plus eligibility.
  - Redo the full selection only if the target is reservable now, or if 120 ticks have passed since the last selection.
  - Re-read `docs\superpowers\specs\2026-08-13-*` and `2026-08-16-*` before editing, so the livelock and strict-priority rules stay intact (the existing policy tests must keep passing).

### F4. Remove the counterproductive storage "cache" (medium; under `StorageHotpathCleanupEnabled`)

File: `PickUpAndHaulCECarryFix.cs` (`PickUpAndHaulStorageHotpath`).
- Delete `PlanningStorageContext`'s `slotCellCache` and `nonSlotThingCache`, plus `StorageCacheKey` and `SlotCellCandidate`.
- Slot-cell search:
  - Iterate `map.haulDestinationManager.AllGroupsListInPriorityOrder`.
  - Apply the priority filter and `parent.Accepts(thing)` **once per group**.
  - Then go through the cells with the `skipCells` and `IsGoodStoreCell` checks, as PUAH does at `WorkGiver_HaulToInventory.cs:491-524`.
- Non-slot search: iterate `AllHaulDestinationsListInPriorityOrder` directly, with PUAH's priority `break`.
- Replace the context object with a `[ThreadStatic] int planningDepth`, so the `AllocateThingAtCell` replacement still applies only inside `JobOnThing`. `BeginPlanning` no longer allocates.

### F5. CE capacity stat recomputation (low-medium; under `PerformancePatchEnabled`)

File: `PickUpAndHaulCECarryFix.cs` (`CeInventoryUtility.PlannedCapacity`), new `CapacityMath.cs` (pure `CountByDimension`).
- Compute a `UnitCost { massPerUnit, bulkPerUnit }` once per candidate: one `GetStatValue(Mass)` and one `GetStatValue(Bulk)`.
- Pass it to `CountThatFits` and `Add`.
- Call CE `CanFitInInventory` only when the planned count is above 0. It's still needed for CE's equipment-specific rules.
- Move `CountByDimension` to `CapacityMath` so it can be unit-tested.

### F6. PUAH "haul more" toil (low-medium; under `StorageHotpathCleanupEnabled`)

File: `PickUpAndHaulCECarryFix.cs` (`Patch_WorkGiver_GetClosestAndRemove_Hotpath`).
- When `searchSet` is PUAH's `JobDriver_HaulToInventory.TempListForThings` (reference-equal; get it via `AccessTools` property getter), it's a single call with a fixed center.
- So first remove everything beyond `maxDistance` in one O(N) pass, then sort by distance and iterate.
- Removing out-of-range items up front is safe only for this single-call list. PUAH's `JobOnThing` loop changes the center between calls, so it must be left alone.

### F7. Smaller items (low)

- **`HasJobOnThing` → `JobOnThing` memo** (`StorageHotpathCleanupEnabled`):
  - The F1 prefix stores `(pawn, thing, forced, tick, foundCell, haulDestination)`.
  - `TryMakeOptimizedJob` reuses it instead of `StoreUtility.TryFindBestBetterStorageFor` when everything matches and the target still passes `IsGoodStoreCell` / `Accepts`.
  - This saves one full storage search per job start.
- **Remove `Patch_WorkGiver_PotentialWorkThingsGlobal`.** It's equivalent to PUAH's version, and removing it also eliminates prefix-ordering risk with KeyzAllowUtilities.
- **New prefix on `PawnUnloadChecker.CheckIfPawnShouldUnloadInventory`:**
  - Return false (skip the original) under exactly the original's early-return conditions: no comp, wrong faction or race, empty carried set, empty inventory.
  - This avoids the unused `JobMaker.MakeJob` allocation on every pass of the colonist's AI decision loop.
- **New prefix on `CompHauledToInventory.GetHashSet`:**
  - Call `RemoveWhere` only if `set.Contains(null)`, which is O(1).
  - Get the field via `AccessTools.FieldRefAccess<CompHauledToInventory, HashSet<Thing>>("takenToInventory")`.
  - Same result as the original.

## Tests

Project: `Source\RouteOrdering.Tests` (net8 console).
- Link the new pure helpers with the same `Exists(...)` + `DefineConstants` pattern used in `RouteOrdering.Tests.csproj`:
  - `CandidateSelection.cs`
  - `CapacityMath.cs`
  - `UnloadDestinationCache.cs`
  - the type-memo part of `AdaptiveStorageIndex`, if it's kept free of game types
- New cases:
  - Lazy selection validates only picked items and respects the validation cap.
  - Rejected items are skipped in score order.
  - `CountByDimension` edge cases: zero, negative, epsilon.
  - Unload cache searches once per def and refreshes the selected def.
  - The adaptive type memo matches the uncached result.
  - The reorder keeps every `ShouldUsePlanningFallback` truth-table row.
- Run: `dotnet run --project .\PickUpAndHaulCECarryFix\Source\RouteOrdering.Tests\RouteOrdering.Tests.csproj`. All existing and new tests must pass.

## Verification

1. **Build:** both `RimWorldVersion=1.6` and `=1.7` Release builds are clean. Output goes to `1.6\Assemblies` and `1.7\Assemblies`.
2. **Unit tests** pass.
3. **In-game smoke test on 1.7 (Waylith)**, with the active save's mods (PUAH, CE, Adaptive Storage, Allow Tool, KeyzAllowUtilities):
   - Startup log shows `[Pick Up And Haul CE Carry Fix] Loaded.` and no Harmony errors.
   - Diagnostics on. Compare the Phase 2 baseline with the post-fix aggregates for:
     - (a) Adaptive shelves full and hundreds of loose items (F1, F7): `HasJobOnThing` ms and adaptive cells examined should drop by orders of magnitude.
     - (b) A dense pile after mining or a raid (F2): validator calls ≤ cap; pawns still pick nearby items.
     - (c) A pawn unloading 15+ stacks to Adaptive Storage, including a reserved-shelf wait (F3): searches per unload roughly equal to the number of distinct defs; no livelock; still waits correctly.
     - (d) Many stockpiles with mixed priorities and item types (F4): same destinations as before, fewer lookups.
     - (e) Urgent-haul designation (Allow Tool / Keyz) still restricts extra pickups.
   - Toggle each setting off and confirm the fallback behavior.
4. **1.6 build:** compile-verified only. No 1.6 install exists here, so state this in the Run 5 doc.
5. **Read-only inputs untouched:** confirm Workshop folders and saves have unchanged timestamps (no writes outside `Mods\PickUpAndHaulCECarryFix`).

---

# Implementation Status (updated after implementation)

Status rules: `[x]` means implemented and verified by a clean build and the unit tests. In-game checks remain `[ ]`.

## Tasks
- [x] Phase 0: plan copied into this file.
- [x] Phase 1: dual 1.6 + 1.7 build.
  - [x] `RimWorldVersion` property (default `1.7`) in `Source\Directory.Build.props`; per-version `BaseIntermediateOutputPath` (`Source\obj\<version>\`).
  - [x] 1.6 build uses `Krafs.Rimworld.Ref 1.6.4871` (net48). Harmony from `2009463077\1.6\Assemblies\0Harmony.dll`.
  - [x] 1.7 build: see "1.7 build notes" below. Harmony from `2009463077\1.7\Assemblies\0Harmony.dll` (3.0.0.0).
  - [x] `OutputPath` `..\$(RimWorldVersion)\Assemblies\`; `LoadFolders.xml` added; `About.xml` lists 1.7.
  - [x] Root `Assemblies\` DLL/PDB removed after both new outputs existed.
- [x] Phase 2: aggregated diagnostics (`PickUpAndHaulAggregateStats.cs`), one summary line per 2500 ticks while diagnostics are enabled.
- [x] F1 Adaptive Storage fallback (`AdaptiveStorageIndex.cs`, `AdaptiveStorageReservationCompatibility.cs`, `Patch_WorkGiver_HasJobOnThing_AdaptiveStorageReservation`).
  - [x] Step 1: `CanReserveNew` first, `continue` when reservable; behavior identical.
  - [x] Step 2: `AdaptiveStorageIndex.Active` static readonly; per-`Type` memo (`TypeMemo`); used by fallback, FindTargetOrDrop gate, gather reservation, reservation-state checks.
  - [x] Step 3: per-map, per-tick reserved-cell index from `ReservationManager.ReservationsReadOnly` (exists in 1.6 and 1.7). Gated by `StorageHotpathCleanupEnabled`.
  - [x] Step 4: HasJobOnThing postfix replaced by a prefix that evaluates the cheap checks once.
  - [ ] Step 5 / F7 HasJobOnThing -> JobOnThing memo: SKIPPED (see deviations).
- [x] F2 Lazy candidate validation (`CandidateSelection.cs`, `LazyCandidateSet<T>`). `OpportunisticCandidateCap` is now the maximum validations per plan; label and tooltip updated.
- [x] F3 Unload routing (`UnloadDestinationCache.cs`, `PickUpAndHaulUnloadRouting`, `MakeFindTargetOrWait`).
  - [x] Per-job ranking cache keyed by pawn id + `job.loadID`, one storage search per distinct `ThingDef` (entries expire after 600 ticks).
  - [x] Selection memo reused by the unload toil when it is from the same tick and still valid.
  - [x] One toil for both paths (`Patch_AdaptiveStorage_FindTargetOrDrop` applies when Adaptive Storage is active or unload routing is enabled; the hold/wait branches only run when Adaptive Storage is active).
  - [x] Reflection invoke replaced by a lazily created `AccessTools.MethodDelegate`.
  - [x] Retry loop: cheap re-check (`ShouldRetryUnload` state) every 30 ticks, full selection after 120 ticks or when the target is reservable/ineligible.
- [x] F4 Removed `PlanningStorageContext` caches; group-level `Accepts`/priority filtering; non-slot search with priority break; `[ThreadStatic] planningDepth`.
- [x] F5 `UnitCost` read once per candidate (`CapacityMath.cs`); CE `CanFitInInventory` only when something is planned.
- [x] F6 Haul-more toil: `TempListForThings` detected by reference; one O(N) pass + one sort.
- [x] F7 smaller items.
  - [x] Removed `Patch_WorkGiver_PotentialWorkThingsGlobal` (identical to PUAH's own method).
  - [x] `PawnUnloadChecker.CheckIfPawnShouldUnloadInventory` early-out prefix.
  - [x] `CompHauledToInventory.GetHashSet` prefix (`RemoveWhere` only when the set contains null).
- [x] Tests (`Source\RouteOrdering.Tests`): 26 cases pass (13 existing + 13 new).

## Validation
- [x] Build Release for `RimWorldVersion=1.6` and `1.7` (0 warnings, 0 errors).
- [x] `dotnet run --project Source\RouteOrdering.Tests\RouteOrdering.Tests.csproj`: all pass.
- [ ] In-game smoke test on 1.7 (Waylith) with PUAH, CE, Adaptive Storage, Allow Tool, KeyzAllowUtilities: startup log, no Harmony errors.
- [ ] Compare diagnostics aggregates before/after for scenarios (a)-(e) in the Verification section above.
- [ ] Toggle each setting off and confirm the fallback behavior.
- 1.6 is compile-verified only; there is no 1.6 install here.

## 1.7 build notes
- `Krafs.Rimworld.Ref 1.7.5000-unstable` ships `ref/net10.0` only, so a net48 project cannot restore it (NU1202), and the Harmony 1.7 DLL references `System.Runtime 10.0.0.0` (CS1705 on net48).
- The installed SDK is 8.0.423 and cannot target net10.0. The 1.7 configuration therefore compiles as `net8.0` with `DisableImplicitFrameworkReferences` + `NoStdLib`, using the `Microsoft.NETCore.App.Ref 10.0.14` reference pack (fetched through `PackageDownload`, no compatibility check) and the Krafs 1.7 reference assemblies (minus `System.*` / `Microsoft.*`) added by a target. The produced DLL references `System.Runtime 10.0.0.0`, `0Harmony 3.0.0.0` and `Assembly-CSharp 1.7.9805.38275`.
- No 1.7 API source changes were needed.

## Deviations
- F7 `HasJobOnThing` -> `JobOnThing` memo skipped: `HasJobOnThing` calls `StoreUtility.TryFindBestBetterStorageFor` with `needAccurateResult: false` while `JobOnThing` uses `true`; the cached cell could differ from the accurate result, so reusing it would change destination selection.
- F1 step 4 gating: the prefix fallback requires `AdaptiveStorageIndex.Active` AND `PerformancePatchEnabled`. The fallback result is only turned into a job by the optimized `JobOnThing` path; without it `HasJobOnThing` would report work that `JobOnThing` cannot produce.
- F3 caching is per `ThingDef`, so two stacks of the same def with different quality/HP are ranked by one search; the toil still searches (or reuses a same-tick memo) for the exact chosen item.

## Review (2026-11-06)
- [x] Reviewed F1-F7, build infrastructure and tests against the plan and the original PUAH source. The behavior-identical changes (F1 reorder, F4 group-level `Accepts`, F5, F6 tie order, F7 prefixes) match the originals. Both deviations above are justified.
- [x] Fixed: `Patch_WorkGiver_JobOnThing` unwound planning state in a postfix, which Harmony skips when planning throws. That left `planningDepth` raised, so the `AllocateThingAtCell` replacement stayed active outside `JobOnThing`. It is now a `Finalizer`.
- [x] Rebuilt 1.6 and 1.7 (0 warnings, 0 errors); 26/26 tests pass.
- Accepted risks:
  - The reserved-cell index is keyed on (map, tick, reservation count). A release plus a new reservation in the same tick can miss the fallback for that tick.
  - Unload routing state (`PickUpAndHaulUnloadRouting.States`, at most 64 pawns) is not cleared on game load. Stale entries only affect ranking, for up to 600 ticks.
