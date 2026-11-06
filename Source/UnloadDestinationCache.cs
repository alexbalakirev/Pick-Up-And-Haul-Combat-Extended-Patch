using System;
using System.Collections.Generic;

namespace PickUpAndHaulCECarryFix
{
    /// <summary>Result of a storage search for one unloadable item, expressed in plain cell coordinates.</summary>
    internal readonly struct UnloadDestination
    {
        public readonly bool Found;
        public readonly int X;
        public readonly int Z;

        public UnloadDestination(bool found, int x, int z)
        {
            Found = found;
            X = x;
            Z = z;
        }

        public static UnloadDestination None => new UnloadDestination(false, 0, 0);
    }

    /// <summary>
    /// Per-unload-job ranking cache. One storage search per distinct item kind (instead of one per carried item per
    /// selection), then all carried items are ranked by the cached destination distance.
    /// </summary>
    internal sealed class UnloadDestinationCache<TKey>
    {
        private struct Entry
        {
            public UnloadDestination Destination;
            public int Tick;
        }

        private readonly Dictionary<TKey, Entry> entries = new Dictionary<TKey, Entry>();
        private readonly int maxAgeTicks;
        private int searches;

        public UnloadDestinationCache(int maxAgeTicks)
        {
            this.maxAgeTicks = maxAgeTicks;
        }

        public int Searches => searches;
        public int Count => entries.Count;

        public void Clear()
        {
            entries.Clear();
        }

        public void Refresh(TKey key, UnloadDestination destination, int tick)
        {
            entries[key] = new Entry { Destination = destination, Tick = tick };
        }

        public bool TryGet(TKey key, int tick, out UnloadDestination destination)
        {
            if (entries.TryGetValue(key, out var entry) && tick >= entry.Tick && tick - entry.Tick <= maxAgeTicks)
            {
                destination = entry.Destination;
                return true;
            }

            destination = default;
            return false;
        }

        /// <summary>
        /// Returns the index of the item with the nearest destination, or -1 when no item has one.
        /// <paramref name="search"/> runs at most once per distinct key whose cache entry is missing or expired.
        /// </summary>
        public int SelectNearest<TItem>(
            IReadOnlyList<TItem> items,
            Func<TItem, TKey> keyOf,
            Func<TItem, int> stableIdOf,
            Func<TItem, UnloadDestination> search,
            int originX,
            int originZ,
            int tick)
        {
            if (items == null || items.Count == 0)
            {
                return -1;
            }

            var bestIndex = -1;
            var bestScore = default(RouteScore);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var key = keyOf(item);
                if (!TryGet(key, tick, out var destination))
                {
                    searches++;
                    destination = search(item);
                    Refresh(key, destination, tick);
                }

                if (!destination.Found)
                {
                    continue;
                }

                var dx = originX - destination.X;
                var dz = originZ - destination.Z;
                var score = new RouteScore(dx * dx + dz * dz, 0, stableIdOf(item));
                if (bestIndex < 0 || RouteOrdering.IsBetter(score, bestScore))
                {
                    bestIndex = i;
                    bestScore = score;
                }
            }

            return bestIndex;
        }
    }
}
