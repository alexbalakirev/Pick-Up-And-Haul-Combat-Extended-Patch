namespace PickUpAndHaulCECarryFix
{
    /// <summary>Pure decisions about a planned storage-limited partial pickup. Contains no game types.</summary>
    internal static class PartialPickupPolicy
    {
        /// <summary>
        /// True when the pawn took exactly the planned partial amount of a larger stack: the remainder should stay where it is
        /// and the trip continues, instead of ending the trip to hand-haul the leftover.
        /// </summary>
        public static bool LeaveRemainder(int plannedCount, int pickedCount, int stackCount)
            => pickedCount > 0 && pickedCount == plannedCount && plannedCount < stackCount;
    }
}
