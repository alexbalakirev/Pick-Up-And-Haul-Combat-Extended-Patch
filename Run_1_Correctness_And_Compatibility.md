# Run 1: Correctness And Compatibility

## Goal
Make the fix mod handle correctness and compatibility issues without editing the Workshop copy of Pick Up And Haul.

## Hard Constraint
- Implement all changes only in `D:\SteamLibrary\steamapps\common\RimWorld\Mods\PickUpAndHaulCECarryFix`.
- Do not edit or overwrite Workshop folders under `D:\SteamLibrary\steamapps\workshop\content\294100`.
- Do not edit or overwrite other local mods, including `ServitorNoSlaveRebellion`.
- Treat Pick Up And Haul and Combat Extended source folders as read-only references.
- If a task cannot be implemented safely from this fix mod with Harmony/runtime patches, stop and document why before changing anything.

## Current State
- Local fix mod root: `D:\SteamLibrary\steamapps\common\RimWorld\Mods\PickUpAndHaulCECarryFix`
- Main patch file: `Source\PickUpAndHaulCECarryFix.cs`
- Built DLL output: `Assemblies\PickUpAndHaulCECarryFix.dll`
- Target PUAH source reference only: `D:\SteamLibrary\steamapps\workshop\content\294100\1279012058\Source\PickUpAndHaul`
- Target PUAH assembly reference: `D:\SteamLibrary\steamapps\workshop\content\294100\1279012058\1.6\Assemblies\PickUpAndHaul.dll`
- Target CE assembly reference: `D:\SteamLibrary\steamapps\workshop\content\294100\2890901044\Assemblies\CombatExtended.dll`
- Project target framework is `net48`, because the installed PUAH 1.6 assembly targets .NET Framework 4.8.
- The fix mod already patches PUAH CE helper stubs and uses CE weight/bulk fullness for planning.

## Important References
- `PickUpAndHaulCECarryFix.cs`
  - `CeInventoryUtility.CountThatFits` around line 46: delegates final stack count to `CompInventory.CanFitInInventory`.
  - `CeInventoryUtility.OverAllowedCapacity` around line 63: currently uses `CapacityRatio`.
  - `CeInventoryUtility.EncumbrancePercent` around line 73: currently returns CE `CapacityRatio`.
  - `CeInventoryUtility.AddedEncumbrance` around line 83: currently returns CE added capacity ratio for CE pawns.
  - `CeInventoryUtility.WeightRatio`, `BulkRatio`, `CapacityRatio`, `AddedCapacityRatio` around lines 98-139.
  - `Patch_WorkGiver_CountPastCapacity` around line 246: overrides PUAH count semantics for CE pawns by returning `CountThatFits`.
  - `Patch_JobDriver_CheckForOverencumberedForCombatExtended` around line 289: patches the generated lambda method for PUAH's CE overencumbrance toil.
- `WorkGiver_HaulToInventory.cs`
  - `OverAllowedGearCapacity` line 53: vanilla mass-only gate in base PUAH.
  - `MassUtility.WillBeOverEncumberedAfterPickingUp` line 69: vanilla gate patched by fix mod transpiler.
  - `MassUtility.EncumbrancePercent` line 127: vanilla planning ratio patched by fix mod transpiler.
  - `AddedEncumberance` line 526 and `CountPastCapacity` line 529.
- `JobDriver_HaulToInventory.cs`
  - `TryMakePreToilReservations` line 6 assumes queue state.
  - `CountToPickUpUntilOverEncumbered` line 43 is overridden by PUAH CE path on line 48.
  - `CheckForOverencumberedForCombatExtended` line 138 creates a lambda; the actual `MassUtility.EncumbrancePercent` call is inside the generated nested method.

## Scope
- Keep all changes in `PickUpAndHaulCECarryFix`.
- Patch behavior with Harmony where possible.
- Avoid changing Pick Up And Haul source files under `workshop/content`.

## Task Status Rules
- Use checkboxes for every task and subtask.
- Mark a task or subtask as `[x]` immediately when it is completed and verified.
- Leave incomplete tasks as `[ ]`.
- Tasks marked `Delayed - do not execute now` must not be implemented until explicitly re-approved.
- If a task cannot be implemented safely inside `PickUpAndHaulCECarryFix`, document the blocker in this file before changing code.

## Tasks
- [x] Confirm CE bulk-aware planning remains implemented.
  - [x] `OverAllowedCapacity` uses max of CE weight ratio and bulk ratio.
  - [x] `EncumbrancePercent` uses max of CE weight ratio and bulk ratio.
  - [x] `AddedEncumbrance` uses max of added weight ratio and added bulk ratio.
- [x] Patch or compensate for `CountPastCapacity` semantics for CE pawns.
  - [x] Current suspicious behavior computes excess count, then uses it as haul count.
  - [x] For CE pawns, the fix mod returns `CompInventory.CanFitInInventory` count through `CeInventoryUtility.CountThatFits`.
  - [ ] For non-CE pawns, patch only if the bug is verified against PUAH behavior.
- [ ] Investigate whether `CountPastCapacity` should be fixed for non-CE pawns. Delayed - do not execute now.
  - [ ] If yes, implement a separate prefix that calculates allowed count, not excess count. Delayed - do not execute now.
  - [ ] If no, document why the existing PUAH behavior is acceptable or intentional.
- [ ] Patch mod detection reliability if needed.
  - [x] Fix mod startup already uses package IDs: `Mehni.PickUpAndHaul` and `CETeam.CombatExtended`.
  - [ ] Decide whether to patch PUAH internal display-name detection. Delayed - do not execute now unless a real compatibility bug is observed.
  - [ ] If approved later, target `CETeam.CombatExtended`, `Mehni.PickUpAndHaul`, and known package IDs.
  - [x] Base PUAH source has `ModCompatibilityCheck.CombatExtendedIsActive` checking display name `"Combat Extended"` documented here.
- [x] Add defensive guards around PUAH reservation queue assumptions.
  - [x] Avoid hard failures if `targetQueueA` is null or empty.
  - [x] Avoid indexing `targetQueueA[0]` unless it exists.
  - [x] Safe failure behavior is to return `false` from `TryMakePreToilReservations`, rejecting malformed jobs cleanly.

## Planning Questions For Next Agent
- Non-CE `CountPastCapacity` remains deferred; this run stays CE-only except defensive stability patches.
- Queue guards return `false` from reservations so malformed jobs fail cleanly.
- PUAH package-ID/display-name detection patch remains deferred; the fix mod startup gate is sufficient for this run.

## Known Pitfalls
- Do not patch the visible `CheckForOverencumberedForCombatExtended` method only; its `initAction` body is compiled into a generated nested method.
- `PickUpAndHaul.CompatHelper` is internal, so patches targeting it need `[HarmonyPatch]` plus `TargetMethod()` and `AccessTools.TypeByName("PickUpAndHaul.CompatHelper")`.
- Keep CE `CanFitInInventory` as the final authority for stack count; ratio helpers are only planning heuristics.

## Validation
- Build `PickUpAndHaulCECarryFix` in Release.
- Start RimWorld and verify the fix mod logs one load message.
- Test CE pawn near weight limit.
- Test CE pawn near bulk limit.
- Test pawn without CE inventory comp.
- Confirm no edits were made to Workshop mod files.

## Notes
- This run is correctness-first. Do not mix in large performance rewrites.
- Delayed non-CE count and PUAH internal mod-detection tasks remain intentionally deferred.


