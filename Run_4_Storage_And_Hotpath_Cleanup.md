# Run 4: Storage And Hotpath Cleanup

## Goal
Reduce repeated storage lookup and allocation overhead after the main candidate planning path is stable.

## Hard Constraint
- Implement all changes only in `D:\SteamLibrary\steamapps\common\RimWorld\Mods\PickUpAndHaulCECarryFix`.
- Do not edit or overwrite Workshop folders under `D:\SteamLibrary\steamapps\workshop\content\294100`.
- Do not edit or overwrite other local mods, including `ServitorNoSlaveRebellion`.
- Treat Pick Up And Haul and Combat Extended source folders as read-only references.
- If a task cannot be implemented safely from this fix mod with Harmony/runtime patches, stop and document why before changing anything.

## Current State
- Base PUAH repeatedly searches storage destinations during job planning.
- The fix mod currently does not cache storage lookups or remove LINQ allocations.
- Run 2 instrumentation should identify which hot paths matter before this run starts.

## Important References
- `WorkGiver_HaulToInventory.cs`
  - `AllocateThingAtCell` line 334:
    - Finds or extends storage allocation for each candidate.
    - May call `TryFindBestBetterStorageFor` repeatedly.
  - `TryFindBestBetterStorageFor` around line 429:
    - Combines slot-group storage and non-slot-group storage.
  - `TryFindBestBetterStoreCellFor` around line 474:
    - Walks `map.haulDestinationManager.AllGroupsListInPriorityOrder` and cells.
  - `TryFindBestBetterNonSlotGroupStorageFor` around line 532:
    - Walks non-slot-group destinations.
  - LINQ-like hot paths:
    - `storeCellCapacity.FirstOrDefault(...)` inside `AllocateThingAtCell`.
    - `searchSet.Any()` inside `GetClosestAndRemove` and `FindClosestThing`.
- `JobDriver_HaulToInventory.cs`
  - `DefDatabase<WorkGiverDef>.AllDefsListForReading.First(...)` line 88:
    - Re-resolves the PUAH workgiver during running jobs.
- `PickUpAndHaulCECarryFix.cs`
  - Existing patches can host static caches and helper methods.
  - Be careful with static caches across maps/saves; prefer per-job or short-lived caches.

## Scope
- Focus on repeated storage destination searches and hot-path allocations.
- Keep changes measurable and incremental.
- Avoid broad rewrites unless instrumentation shows they matter.

## Task Status Rules
- Use checkboxes for every task and subtask.
- Mark a task or subtask as `[x]` immediately when it is completed and verified.
- Leave incomplete tasks as `[ ]`.
- Tasks marked `Delayed - do not execute now` must not be implemented until explicitly re-approved.
- If a task cannot be implemented safely inside `PickUpAndHaulCECarryFix`, document the blocker in this file before changing code.

## Tasks
- [x] Cache storage destination searches during one job plan.
  - [x] Cache by item definition and storage priority where safe.
  - [x] Do not reuse cached destinations across jobs unless invalidation is clear.
  - [x] Respect reservations, reachability, storage filters, and changing capacity.
- [x] Cache shape suggestion.
  - [x] Key: `(Map map, ThingDef thingDef, StoragePriority currentPriority, Faction faction, bool acceptSamePriority)`.
  - [x] Value: cached candidate scan lists only; live destination validity is re-checked before use.
  - [x] Prefer per-`JobOnThing` cache object over static global cache.
- [x] Cache repeated workgiver lookup in job driver.
  - [x] Replace repeated `DefDatabase<WorkGiverDef>.AllDefsListForReading.First(...)` lookup with a cached result.
  - [x] Handle missing workgiver defensively.
- [x] Possible implementation for workgiver lookup.
  - [x] Static field in fix mod for `WorkGiver_HaulToInventory`.
  - [x] Lazy resolve once.
  - [x] Prefix/transpiler around generated `MakeNewToils` code may be needed because lookup is inside a lambda.
- [x] Reduce hot-path LINQ allocations.
  - [x] Replace frequent `Any`, `FirstOrDefault`, `ToList`, and sort-heavy LINQ in patched hot paths where practical.
  - [x] Prioritize paths identified by Run 2 instrumentation.
- [x] Add an in-game disable switch for Run 4 cleanup.
  - [x] Expose `Enable storage and hotpath cleanup` in the existing settings window.
  - [x] Default the switch to enabled.

## Risk Status
- Storage caching and broad LINQ rewrites are compatibility-sensitive.
- Execution was explicitly re-approved on 2026-06-19.
- Implemented behind `storageHotpathCleanupEnabled`, exposed as "Enable storage and hotpath cleanup" in the mod settings UI.
- The switch defaults to enabled and is independent from the Run 3 candidate planning switch.
- Storage candidate caches are per `JobOnThing` planning context only, not global or cross-map.
- The workgiver lookup optimization is a defensive transpiler over generated `MakeNewToils` methods; if the expected `Enumerable.First<WorkGiverDef>` call shape is absent, the methods are left unchanged.
- Build verification passed with `dotnet build`.
- Still needs in-game validation with Run 2 diagnostics before treating the behavior as proven.

## Planning Questions For Next Agent
- Should storage cache be per `JobOnThing` invocation only, or can it safely live for a short tick window?
- Which storage lookups are safe to cache without violating reservations and changing capacity?
- Should this run patch original helper methods individually, or replace larger planning pieces after Run 3?
- How much behavior risk is acceptable for non-slot-group storage/container support?

## Known Pitfalls
- Storage filters, priorities, reservations, reachability, and container capacity can change between candidates.
- Caching a cell that becomes reserved/full can produce invalid jobs or over-hauling.
- Non-slot-group storage uses `ThingOwner` capacity and must not be treated like stockpile cells.
- Replacing LINQ is only worth it after the larger algorithmic issues are addressed.

## Acceptance Criteria
- Fewer storage lookup calls per planned job.
- No stale storage destination bugs.
- No increase in failed reservations or invalid haul destinations.
- Release build succeeds.

## Validation
- [x] Build Release.
- [ ] Compare Run 2 instrumentation before and after.
- [ ] Test storage with multiple stockpiles and priorities.
- [ ] Test containers or non-slot-group storage.
- [ ] Test changing storage filters during active hauling.
- [ ] Test item stacks that partially fit in a destination.
- [ ] Disable `Enable storage and hotpath cleanup` and confirm behavior falls back while Run 3 can remain enabled.

## Notes
- This run should happen after candidate planning changes, because the candidate set size affects storage lookup frequency.
