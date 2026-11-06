using System;
using System.Collections.Generic;

namespace PickUpAndHaulCECarryFix
{
    /// <summary>
    /// Holds every opportunistic haul candidate that is within range of the first thing and validates lazily.
    /// Scores are recomputed once per pick against the current route position, and only the picked entry is
    /// validated. The number of validations is bounded per plan by the validation budget.
    /// </summary>
    internal sealed class LazyCandidateSet<T> where T : class
    {
        private readonly List<T> items;
        private readonly List<RouteScore> scores;
        private int validationBudget;
        private int validationCalls;

        public LazyCandidateSet(int validationBudget, int capacity = 0)
        {
            this.validationBudget = Math.Max(0, validationBudget);
            items = new List<T>(Math.Max(0, capacity));
            scores = new List<RouteScore>(Math.Max(0, capacity));
        }

        public int Count => items.Count;
        public int ValidationCalls => validationCalls;
        public int RemainingValidationBudget => validationBudget;

        public void Add(T item)
        {
            if (item != null)
            {
                items.Add(item);
            }
        }

        /// <summary>
        /// Picks the best remaining entry that passes <paramref name="validate"/>.
        /// Entries rejected by <paramref name="isLive"/> are dropped without using validation budget.
        /// The search stops (returning false) when the best remaining entry is farther than
        /// <paramref name="maxCurrentDistanceSquared"/> or when the validation budget is spent.
        /// </summary>
        public bool TryPickNext(
            Func<T, RouteScore> score,
            float maxCurrentDistanceSquared,
            Func<T, bool> isLive,
            Func<T, bool> validate,
            out T picked,
            out int evaluated)
        {
            picked = null;
            evaluated = 0;
            if (items.Count == 0 || score == null)
            {
                return false;
            }

            scores.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                scores.Add(score(items[i]));
            }

            evaluated = scores.Count;

            while (items.Count > 0)
            {
                if (validationBudget <= 0)
                {
                    return false;
                }

                var bestIndex = RouteOrdering.FindBestIndex(scores);
                if (bestIndex < 0)
                {
                    return false;
                }

                var bestScore = scores[bestIndex];
                var best = items[bestIndex];
                SwapRemove(bestIndex);

                if (bestScore.CurrentDistanceSquared > maxCurrentDistanceSquared)
                {
                    return false;
                }

                if (isLive != null && !isLive(best))
                {
                    continue;
                }

                validationBudget--;
                validationCalls++;
                if (validate == null || validate(best))
                {
                    picked = best;
                    return true;
                }
            }

            return false;
        }

        private void SwapRemove(int index)
        {
            var last = items.Count - 1;
            if (index != last)
            {
                items[index] = items[last];
                scores[index] = scores[last];
            }

            items.RemoveAt(last);
            scores.RemoveAt(last);
        }
    }
}
