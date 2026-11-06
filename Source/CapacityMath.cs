using System;

namespace PickUpAndHaulCECarryFix
{
    /// <summary>Raw per-unit mass and bulk of a thing, read once per candidate.</summary>
    internal readonly struct UnitCost
    {
        public readonly float MassPerUnit;
        public readonly float BulkPerUnit;

        public UnitCost(float massPerUnit, float bulkPerUnit)
        {
            MassPerUnit = massPerUnit;
            BulkPerUnit = bulkPerUnit;
        }
    }

    internal static class CapacityMath
    {
        private const float Epsilon = 0.0001f;

        /// <summary>Ratio added to a capacity dimension by <paramref name="count"/> units; zero when the dimension has no capacity.</summary>
        public static float AddedRatio(int count, float perUnit, float capacity)
        {
            if (capacity <= 0f)
            {
                return 0f;
            }

            return count * perUnit / capacity;
        }

        /// <summary>True when a planned ratio has reached 1 within float error, so an exact fill counts as full.</summary>
        public static bool IsFull(float ratio) => ratio >= 1f - Epsilon;

        /// <summary>How many units still fit in one capacity dimension without exceeding a ratio of 1.</summary>
        public static int CountByDimension(float currentRatio, float addedRatioPerUnit, int maxCount)
        {
            if (addedRatioPerUnit <= 0f)
            {
                return maxCount;
            }

            var remainingRatio = 1f - currentRatio;
            if (remainingRatio <= 0f)
            {
                return 0;
            }

            return Math.Max(0, Math.Min(maxCount, (int)Math.Floor((remainingRatio + Epsilon) / addedRatioPerUnit)));
        }
    }
}
