# Run 3: Candidate Planning Performance

## Goal
Reduce the CPU cost of finding opportunistic haul targets in item-heavy maps.

## Hard Constraint
- Implement all changes only in `D:\SteamLibrary\steamapps\common\RimWorld\Mods\PickUpAndHaulCECarryFix`.
- Do not edit or overwrite Workshop folders under `D:\SteamLibrary\steamapps\workshop\content\294100`.
- Do not edit or overwrite other local mods, including `ServitorNoSlaveRebellion`.
- Treat Pick Up And Haul and Combat Extended source folders as read-only references.
- If a task cannot be implemented safely from this fix mod with Harmony/runtime patches, stop and document why before changing anything.

## Current State
- Base PUAH performs full haulable list copies and repeated nearest searches.
- The fix mod currently patches capacity semantics but does not change candidate enumeration or sorting.
- Any changes should be implemented as Harmony patches in `PickUpAndHaulCECarryFix`, not by editing Workshop source.

## Important References
- `WorkGiver_HaulToInventory.cs`
  - `PotentialWorkThingsGlobal` line 31:
    - Gets `pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling().ToList()`.
    - Sorts the entire list by distance to pawn.
  - `JobOnThing` around line 146:
    - Creates `var haulables = new List<Thing>(map.listerHaulables.ThingsPotentiallyNeedingHauling())`.
    - Sorts entire list by distance to first target.
  - Planning loop around lines 177-197:
    - Allocates next thing.
    - Adds `AddedEncumberance`.
    - Calls `GetClosestAndRemove` repeatedly for more candidates.
  - `GetClosestAndRemove` line 246:
    - Repeatedly calls `FindClosestThing`, removes from list, validates reachability, and returns the first valid candidate.
  - `FindClosestThing` line 282:
    - Linear scans the entire remaining list.
- `PickUpAndHaulCECarryFix.cs`
  - `Patch_WorkGiver_JobOnThing` around line 261 is already a transpiler touching `JobOnThing`.
  - Additional invasive candidate planning patches may need to replace larger sections of `JobOnThing`.

## Scope
- Focus on haulable candidate collection and nearest-candidate selection.
- Keep behavior close to original Pick Up And Haul behavior.
- Implement through the fix mod with Harmony patches.

## Task Status Rules
- Use checkboxes for every task and subtask.
- Mark a task or subtask as `[x]` immediately when it is completed and verified.
- Leave incomplete tasks as `[ ]`.
- Tasks marked `Delayed - do not execute now` must not be implemented until explicitly re-approved.
- If a task cannot be implemented safely inside `PickUpAndHaulCECarryFix`, document the blocker in this file before changing code.

## Tasks
- [x] Avoid copying and sorting all haulables on every scan.
  - [x] Replace full-list sort behavior with bounded candidate selection where feasible.
  - [x] Consider filtering by relevant radius before sorting.
- [x] Replace repeated nearest search over shrinking lists.
  - [x] Avoid repeated O(n) scans for every selected candidate.
  - [x] Use radius-filtered candidates sorted once, or bounded top-K selection.
- [x] Cap opportunistic pickup candidates per job.
  - [x] Start with a conservative cap such as 128 candidates.
  - [x] Preserve the first direct haul target.
  - [x] Apply cap only to additional opportunistic targets.
- [x] Add an in-game disable switch for the performance behavior.
  - [x] Default the performance patch to enabled.
  - [x] Expose the opportunistic candidate cap in the same settings window.

## Suggested Technical Approaches
- [ ] Lowest risk approach.
  - [ ] Patch `GetClosestAndRemove` with a prefix that searches only a bounded list.
  - [ ] Keep method signature and return behavior identical.
  - [ ] This reduces repeated work if the candidate list has already been trimmed elsewhere.
- [x] Medium risk approach.
  - [x] Patch `JobOnThing` to replace the full `haulables` list with a radius-filtered and capped candidate list before the existing loop uses it.
  - [x] Preserve urgent-haul filtering and existing validator behavior.
- [x] Highest risk approach.
  - [x] Fully replace the opportunistic planning loop with a prefix for `JobOnThing`.
  - [x] Only choose this if transpiling the list construction is too brittle.

## Candidate Selection Rules To Preserve
- [x] First requested haul target must still be considered even if it would not be in a capped opportunistic list.
- [x] Urgent haul mode must continue requiring urgent designation for extra candidates.
- [x] Existing `GoodThingToHaul`, `PawnCanAutomaticallyHaulFast`, reachability, reservation, and storage validation must still apply.
- [x] CE `CountThatFits`, CE bulk/weight planning, and storage capacity constraints must remain active.

## Risk Status
- This whole run is behavior-changing and compatibility-sensitive.
- Execution was explicitly re-approved on 2026-06-19.
- Implemented behind `performancePatchEnabled`, exposed as "Enable candidate planning performance patch" in the mod settings UI.
- Candidate cap defaults to 128 and is configurable from 32 to 512.
- Build verification passed with `dotnet build`.
- Still needs in-game validation with Run 2 instrumentation before treating the behavior as proven.

## Planning Questions For Next Agent
- Should candidate cap be fixed at 128, configurable, or derived from map item count?
- Should radius filtering happen before or after checking reachability?
- Should the patch optimize only opportunistic extras, or also `PotentialWorkThingsGlobal` ordering?
- Is a transpiler acceptable here, or should the fix mod use a prefix that reimplements `JobOnThing` for maintainability?

## Known Pitfalls
- Replacing `JobOnThing` wholesale risks drifting from PUAH storage/container compatibility.
- A simple cap before filtering could miss nearby valid haulables if the raw list ordering is arbitrary.
- Reachability checks are expensive, but skipping them can create invalid jobs.
- Sorting by distance once is usually cheaper than repeated nearest scans, but sorting too many items still hurts.

## Acceptance Criteria
- Maps with hundreds or thousands of loose stacks spend less time in PUAH planning.
- Pawns still pick nearby useful extra haul targets.
- Candidate cap does not prevent normal single-target hauling.
- CE capacity checks still prevent over-planning by weight or bulk.

## Validation
- Use Run 2 instrumentation before and after.
- Compare candidate counts and planning time.
- Test dense stockpile cleanup.
- Test scattered map cleanup.
- Test pawn with low CE bulk capacity.

## Notes
- This is the highest-risk performance run because it can change which nearby items are chosen.
- Keep the cap conservative unless measurements show it should be lower.
