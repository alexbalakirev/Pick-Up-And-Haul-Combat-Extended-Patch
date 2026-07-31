using System;
using System.Collections.Generic;

namespace PickUpAndHaulCECarryFix
{
    internal readonly struct RouteScore
    {
        public readonly int CurrentDistanceSquared;
        public readonly int DestinationDistanceSquared;
        public readonly int StableId;

        public RouteScore(int currentDistanceSquared, int destinationDistanceSquared, int stableId)
        {
            CurrentDistanceSquared = currentDistanceSquared;
            DestinationDistanceSquared = destinationDistanceSquared;
            StableId = stableId;
        }
    }

    internal static class RouteOrdering
    {
        public static int Compare(RouteScore left, RouteScore right)
        {
            if (left.CurrentDistanceSquared != right.CurrentDistanceSquared)
            {
                return left.CurrentDistanceSquared.CompareTo(right.CurrentDistanceSquared);
            }

            if (left.DestinationDistanceSquared != right.DestinationDistanceSquared)
            {
                return left.DestinationDistanceSquared.CompareTo(right.DestinationDistanceSquared);
            }

            return left.StableId.CompareTo(right.StableId);
        }

        public static bool IsBetter(RouteScore candidate, RouteScore currentBest)
            => Compare(candidate, currentBest) < 0;

        public static int FindBestIndex(IReadOnlyList<RouteScore> scores)
        {
            if (scores == null || scores.Count == 0)
            {
                return -1;
            }

            var bestIndex = 0;
            var bestScore = scores[0];
            for (var i = 1; i < scores.Count; i++)
            {
                if (IsBetter(scores[i], bestScore))
                {
                    bestIndex = i;
                    bestScore = scores[i];
                }
            }

            return bestIndex;
        }

        public static int FindWorstIndex(IReadOnlyList<RouteScore> scores)
        {
            if (scores == null || scores.Count == 0)
            {
                return -1;
            }

            var worstIndex = 0;
            var worstScore = scores[0];
            for (var i = 1; i < scores.Count; i++)
            {
                if (Compare(scores[i], worstScore) > 0)
                {
                    worstIndex = i;
                    worstScore = scores[i];
                }
            }

            return worstIndex;
        }

        public static int Clamp(int value, int minimum, int maximum)
            => Math.Max(minimum, Math.Min(maximum, value));
    }
}
