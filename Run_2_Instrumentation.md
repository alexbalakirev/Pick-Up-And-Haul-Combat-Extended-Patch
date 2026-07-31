# Run 2: Instrumentation

## Goal
Add optional measurement around Pick Up And Haul planning so performance changes can be judged with real numbers.

## Hard Constraint
- Implement all changes only in `D:\SteamLibrary\steamapps\common\RimWorld\Mods\PickUpAndHaulCECarryFix`.
- Do not edit or overwrite Workshop folders under `D:\SteamLibrary\steamapps\workshop\content\294100`.
- Do not edit or overwrite other local mods, including `ServitorNoSlaveRebellion`.
- Treat Pick Up And Haul and Combat Extended source folders as read-only references.
- If a task cannot be implemented safely from this fix mod with Harmony/runtime patches, stop and document why before changing anything.

## Current State
- Instrumentation is implemented in `PickUpAndHaulCECarryFix` and is disabled by default.
- Diagnostics can be enabled from the mod settings category `PUAH CE Carry Fix`.
- When enabled, each `WorkGiver_HaulToInventory.JobOnThing` call emits one compact planning summary.
- PUAH's planning path currently logs several unconditional `Log.Message` calls in source, but compiled behavior depends on the Workshop DLL and should not be treated as structured diagnostics.
- The fix mod already patches key planning methods and is a good home for low-noise counters.

## Important References
- `PickUpAndHaulCECarryFix.cs`
  - Startup and `PatchAll`: near `Startup`.
  - `PickUpAndHaulCECarryFixMod` and `PickUpAndHaulCECarryFixSettings`: mod settings UI and persistence.
  - `PickUpAndHaulDiagnostics`: timing and per-planning-attempt counters.
  - `Patch_WorkGiver_JobOnThing`: prefixes/postfixes `WorkGiver_HaulToInventory.JobOnThing` and keeps the existing transpiler.
  - `Patch_WorkGiver_GetClosestAndRemove_Diagnostics`: nearest-search invocation and estimated candidate counts.
  - `Patch_WorkGiver_AllocateThingAtCell_Diagnostics`: storage allocation attempts.
  - `Patch_WorkGiver_TryFindBestBetterStorageFor_Diagnostics`: storage lookup attempts.
  - `Patch_JobDriver_CheckForOverencumberedForCombatExtended`: generated lambda transpiler for CE encumbrance checks.
- `WorkGiver_HaulToInventory.cs`
  - `PotentialWorkThingsGlobal` line 31: copies and sorts all haulables.
  - `JobOnThing` line 56 onward: main planning path.
  - `haulables = new List<Thing>(map.listerHaulables.ThingsPotentiallyNeedingHauling())` around line 146.
  - `AllocateThingAtCell` line 334: storage allocation path.
  - `GetClosestAndRemove` line 246 and `FindClosestThing` line 282: candidate search hot path.
- `JobDriver_HaulToInventory.cs`
  - `MakeNewToils` line 15 onward: runtime pickup/unload flow.
  - `DefDatabase<WorkGiverDef>.AllDefsListForReading.First(...)` line 88.

## Scope
- Add instrumentation in `PickUpAndHaulCECarryFix`, not the Workshop mod.
- Keep logging disabled by default or very quiet by default.
- Prefer counters and timing that can be enabled for diagnosis.

## Task Status Rules
- Use checkboxes for every task and subtask.
- Mark a task or subtask as `[x]` immediately when it is completed and verified.
- Leave incomplete tasks as `[ ]`.
- Tasks marked `Delayed - do not execute now` must not be implemented until explicitly re-approved.
- If a task cannot be implemented safely inside `PickUpAndHaulCECarryFix`, document the blocker in this file before changing code.

## Metrics To Capture
- [x] Number of haulables seen during planning.
- [x] Number of candidate haulables evaluated.
- [x] Number of candidates rejected by CE capacity.
- [x] Number of storage lookup attempts.
- [ ] Number of reachability checks, if practical. Deferred: the approved low-invasive approach avoids patching the inner reachability branch.
- [x] Final number of planned haul targets.
- [x] Time spent in PUAH job planning.
- [x] Number of times normal hauling fallback is used after CE/fullness rejection.
- [x] Number of generated jobs with empty/null target queues, if defensive guards are added in Run 1.

## Implementation Shape
- [x] Add a small static diagnostics utility in the fix mod.
- [x] Gate diagnostics behind a constant, mod setting, or simple static boolean.
- [x] Patch selected PUAH planning methods with Harmony prefixes/postfixes or transpilers only where needed.
- [x] Avoid per-tick noisy logs.
- [x] Log aggregate summaries periodically or per job only when diagnostics are enabled.

## Suggested Technical Approach
- [x] Start with static counters and a `Stopwatch`.
- [x] Use `LongEventHandler.QueueLongEvent` only if later UI/settings are added; not needed for first instrumentation.
- [x] For first pass, avoid invasive transpilers.
  - [x] Prefix/postfix `WorkGiver_HaulToInventory.JobOnThing` to measure total planning time and resulting job queue sizes.
  - [x] Prefix/postfix `WorkGiver_HaulToInventory.GetClosestAndRemove` to count nearest-search invocations.
  - [x] Prefix/postfix `WorkGiver_HaulToInventory.AllocateThingAtCell` to count storage allocation attempts.
- [x] Use `Verse.Log.Message` only when diagnostics are enabled.
- [x] Consider a compile-time constant first.
  - [x] Replaced with the approved in-game mod setting, default disabled.
  - [x] Later convert to a mod setting if repeated testing is needed. Superseded by this run's approved mod setting implementation.

## Planning Questions For Next Agent
- Diagnostics are enabled by an in-game mod setting, default disabled.
- Measurements log per `JobOnThing` planning attempt when diagnostics are enabled.
- Minimum fields before Run 3: elapsed ms, haulables seen, estimated candidates evaluated, CE capacity rejections, storage lookups, storage allocations, fallback flag, malformed/empty queue flag, and planned target count.

## Known Pitfalls
- Instrumentation can itself create allocations and distort hot-path measurements.
- `JobOnThing` may be called often by RimWorld work scanning; avoid string formatting unless logging is enabled.
- Avoid logging every rejected candidate on item-heavy maps.
- Exact reachability-check counts are intentionally deferred because counting the inner branch would require a more fragile patch.

## Validation
- [x] Build Release.
- [ ] Enable diagnostics.
- [ ] Confirm logs include candidate counts and planning time.
- [ ] Disable diagnostics.
- [ ] Confirm normal gameplay does not spam logs.

## Notes
- This run should happen before major performance rewrites.
- The measurements should guide candidate caps and caching strategy in later runs.

