# Construction Batch Hauling Design

## Goal

Add an optional Build From Inventory integration to `PickUpAndHaulCECarryFix`. A pawn should be able to gather several construction-resource stacks, including different material types, into its inventory and then supply several nearby blueprints or frames. The integration must reuse the existing bounded routing and Combat Extended capacity logic and preserve RimWorld's normal construction delivery behavior.

## Scope

The feature applies only when all of the following mods are active:

- Pick Up And Haul
- Combat Extended
- Build From Inventory

The feature is controlled by a separate setting. Build From Inventory remains an optional compatibility dependency rather than a hard dependency.

Only automatically selected construction-resource jobs are eligible for batching. The integration does not replace forced or player-queued orders, installation jobs, blocker-removal jobs, existing-floor removal jobs, or zero-cost construction jobs.

## RimWorld Behavior That Must Be Preserved

RimWorld 1.6 already performs important batching inside `WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor` and `JobDriver_HaulToContainer`:

- It validates faction, reachability, forbidden state, construction eligibility, blockers, reservations, and available resources.
- It accounts for `IHaulEnroute` allocations made by other pawns.
- It groups compatible construction needers within an eight-cell radius.
- It can collect several source stacks of one `ThingDef` into the carry tracker.
- It distributes the carried stack across a primary destination and a destination queue.
- It can replace a blueprint with a frame while the delivery job is running.

The compatibility feature must call the real construction work givers and use their returned jobs as its authority. It must not recreate their destination placement logic.

## Architecture

### Entry Point

A narrow Harmony postfix observes successful `ResourceDeliverJobFor` results. It only considers ordinary `HaulToContainer` jobs produced for automatic work. A thread-static recursion guard prevents compatibility planning calls from recursively creating more batch jobs.

If batching is disabled, Build From Inventory is absent, the pawn is unsupported, capacity is unavailable, or planning does not find a useful batch, the postfix returns the original job unchanged.

### Construction Batch Planning

The planner begins with the original vanilla job and inspects a bounded set of nearby blueprints and frames. The search is centered on the original construction destination. Its radius uses the existing hauling rule: half the distance from the first source to the first destination, with a minimum of 12 cells. The existing opportunistic candidate cap limits the number of construction candidates retained. The planner calls the appropriate installed blueprint or frame work giver for each retained candidate while the recursion guard is active.

Only returned `HaulToContainer` jobs are valid demand signals. Jobs for blockers, floor removal, installation, or other special handling are ignored and allowed to run normally when selected by the game.

For every accepted job, the planner extracts:

- Vanilla-approved source things from `targetA` and `targetQueueA`
- The aggregate requested count from the job
- Construction anchor cells from `targetB`, `targetQueueB`, and `targetC`

Source allocations are deduplicated by `Thing` identity. A remaining-count table prevents two candidate jobs from allocating the same units from one source stack. Destination cells are also deduplicated.

The planner reuses the existing candidate cap, deterministic `RouteScore` ordering, reachability checks, and `CeInventoryUtility.PlannedCapacity`. It limits every allocation by both the remaining source count and CE weight/bulk capacity.

A batch replaces the original job only if it plans useful work beyond the job RimWorld already created. This means at least one of the following is true:

- The batch contains more than one material type.
- It combines more than one distinct vanilla delivery group and allocates more material than the original job.
- It can carry a same-material amount in inventory that exceeds what the original carry-tracker delivery can take.

Several source stacks already present in the original vanilla job are not by themselves considered a benefit because `JobDriver_HaulToContainer` already collects compatible same-material stacks. If the planned batch is equivalent to the original job, the original job is retained.

### Shared Bounded Routing

The reusable route and candidate operations currently embedded in `PickUpAndHaulPerformance` will move behind a focused `BoundedRoutePlanner` interface. Storage hauling and construction gathering will share:

- Candidate-cap clamping
- Deterministic current-distance and destination-distance scoring
- Stable tie-breaking by thing ID
- Removal of invalid or unreachable candidates
- Bounded nearest-candidate selection

Storage-specific validation and construction-specific validation remain outside the shared planner.

### Gathering Job

`JobDriver_GatherConstructionSupplies` receives source things in `targetQueueA`, aligned exact pickup counts in `countQueue`, and construction anchor cells in `targetQueueB`.

The job reserves only the planned source counts. It does not reserve construction destinations while gathering. At each source it revalidates the thing, its reservation, its remaining stack count, and current inventory capacity. It picks up no more than the smaller of the planned count and the count that still fits.

Gathered stacks are added to the pawn's inventory and registered with `CompHauledToInventory`. This retains Pick Up And Haul's normal fallback unloading behavior if the construction route is interrupted or becomes invalid. Combat Extended inventory state is updated after inventory changes.

Destroyed, forbidden, unreachable, or depleted sources are skipped when safe. If no material is gathered, the custom job ends without starting a delivery route.

### Delivery Continuation

Construction destinations are stored as cells rather than long-lived blueprint references because a successful delivery can replace a blueprint object with a frame object.

After gathering, `JobDriver_ContinueConstructionDelivery` resolves the current player-owned blueprint or frame at each saved anchor cell. For each candidate it calls the appropriate real work giver under the recursion guard.

The continuation accepts only a fresh ordinary `HaulToContainer` job whose selected `targetA` is currently held in the same pawn's inventory. It does not accept a job that would fetch another ground stack or take material from another pawn. This keeps the delivery phase bounded to the gathered inventory.

It queues one vanilla delivery job followed by another continuation job. Replanning one delivery at a time ensures that reservations, missing counts, blueprint-to-frame replacement, work-giver patches, and other pawns' progress are observed between deliveries.

The continuation job stores anchor cells in `targetQueueA` and carries a finite attempt budget in `count`. The initial budget is derived from the number of anchors, planned material types, and source allocations, then clamped to the existing candidate cap. Each queued vanilla delivery consumes one attempt. This permits revisiting a cell for its next material while preventing an unchanged failing delivery from creating an infinite job-queue loop.

The vanilla job and Build From Inventory remain responsible for taking the selected stack from inventory, creating or updating frames, and distributing one material type across the vanilla destination queue.

If no saved destination can use a material currently held by the pawn, the route ends. Any construction supplies still tracked by Pick Up And Haul use its existing unloading behavior.

## Compatibility and Failure Handling

The integration preserves the original job whenever it cannot prove that its replacement is useful and valid. It does not reserve destination objects ahead of time, avoiding stale reservations and conflicts with other builders. The trade-off is that another pawn may satisfy a destination during gathering; fresh revalidation handles this case and leftover material falls back to normal unloading.

Calling the installed work givers rather than reconstructing their rules preserves normal RimWorld checks and improves compatibility with active mods such as Build From Inventory, Smarter Construction, Replace Stuff, and QualityBuilder.

No Workshop mod files will be modified. The local mod metadata will use optional `loadAfter` entries where useful, without declaring Build From Inventory as a required dependency.

Diagnostics use the existing diagnostics setting and emit one compact summary per attempted batch. Expected invalidation is not logged as an error. Exceptions during planning or continuation are caught, logged once with context, and fall back to the original job or normal unloading.

## Settings

Add one checkbox to `PickUpAndHaulCECarryFixSettings`:

- `Enable construction batch hauling`

The setting defaults to enabled but has an effect only while Build From Inventory is active. It reuses the existing opportunistic candidate cap rather than adding another tuning control.

## Verification

Verification is intentionally limited to compilation and a small number of high-value automated checks:

- Deterministic bounded route ordering
- Source and destination deduplication with exact source-count allocation
- CE weight and bulk capacity limiting across multiple material types
- Returning the original job when no beneficial batch can be formed

The focused test harness and the Release mod project must compile and pass. Harmony target discovery will be checked against the installed RimWorld 1.6, Pick Up And Haul, Combat Extended, and Build From Inventory assemblies.

No in-game smoke test is required for this task. Runtime job transitions, save/load during a batch, and complete cross-mod behavior therefore remain documented verification gaps.

## Acceptance Criteria

- The feature is inactive when Build From Inventory is not loaded or the setting is disabled.
- Eligible pawns can gather more than one source stack and more than one material type when useful and within CE capacity.
- Each delivery is performed by a freshly generated vanilla `HaulToContainer` job.
- Vanilla same-material distribution to nearby needers remains intact.
- Forced and special construction jobs remain unchanged.
- Invalidated destinations or interrupted routes leave supplies eligible for Pick Up And Haul's normal unloading.
- Existing storage-hauling behavior continues to use the same results after route helpers are shared.
- Focused automated tests pass and the mod builds successfully in Release.
