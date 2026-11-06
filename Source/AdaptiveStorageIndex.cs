using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PickUpAndHaulCECarryFix
{
    /// <summary>
    /// Cheap Adaptive Storage gates shared by the planning fallback and the unload toil:
    /// a startup-cached "mod active" flag, a per-type memo of the Adaptive Storage type check, and a per-tick index of
    /// reserved cells grouped by their (Adaptive Storage) slot group.
    /// </summary>
    internal static class AdaptiveStorageIndex
    {
        /// <summary>ModsConfig.IsActive lower-cases and hashes on every call, so read it once.</summary>
        public static readonly bool Active = ModsConfig.IsActive("adaptive.storage.framework");

        private static readonly TypeMemo AdaptiveTypeMemo = new TypeMemo(AdaptiveStorageReservationPolicy.IsAdaptiveStorageType);

        private static Map indexedMap;
        private static int indexedTick = int.MinValue;
        private static int indexedReservationCount = -1;
        private static Dictionary<SlotGroup, List<IntVec3>> reservedCellsByGroup = new Dictionary<SlotGroup, List<IntVec3>>();
        private static readonly HashSet<IntVec3> SeenCells = new HashSet<IntVec3>();
        private static bool warnedIndexFailure;

        public static bool IsAdaptiveStorageType(Type type) => AdaptiveTypeMemo.Get(type);

        /// <summary>
        /// Returns reserved cells that belong to Adaptive Storage slot groups, grouped per slot group. The index is rebuilt at
        /// most once per map per tick (and when the reservation count changes). Returns false when the index is unavailable,
        /// in which case callers must scan the slot group's cells.
        /// </summary>
        public static bool TryGetReservedCells(Map map, out Dictionary<SlotGroup, List<IntVec3>> index)
        {
            index = null;
            if (map == null || Find.TickManager == null)
            {
                return false;
            }

            try
            {
                var reservations = map.reservationManager?.ReservationsReadOnly;
                if (reservations == null)
                {
                    return false;
                }

                var tick = Find.TickManager.TicksGame;
                if (!ReferenceEquals(indexedMap, map) || indexedTick != tick || indexedReservationCount != reservations.Count)
                {
                    Rebuild(map, reservations, tick);
                }

                index = reservedCellsByGroup;
                return true;
            }
            catch (Exception exception)
            {
                if (!warnedIndexFailure)
                {
                    warnedIndexFailure = true;
                    Log.Warning(
                        "[Pick Up And Haul CE Carry Fix] Reserved-cell index failed; scanning Adaptive Storage cells instead. "
                        + exception);
                }

                indexedMap = null;
                return false;
            }
        }

        private static void Rebuild(Map map, List<ReservationManager.Reservation> reservations, int tick)
        {
            reservedCellsByGroup.Clear();
            SeenCells.Clear();
            for (var i = 0; i < reservations.Count; i++)
            {
                var target = reservations[i].Target;
                if (target.HasThing || !target.IsValid)
                {
                    continue;
                }

                var cell = target.Cell;
                if (!cell.InBounds(map) || !SeenCells.Add(cell))
                {
                    continue;
                }

                var slotGroup = cell.GetSlotGroup(map);
                if (slotGroup == null || !IsAdaptiveStorageType(slotGroup.parent?.GetType()))
                {
                    continue;
                }

                if (!reservedCellsByGroup.TryGetValue(slotGroup, out var cells))
                {
                    cells = new List<IntVec3>();
                    reservedCellsByGroup[slotGroup] = cells;
                }

                cells.Add(cell);
            }

            indexedMap = map;
            indexedTick = tick;
            indexedReservationCount = reservations.Count;
        }
    }
}
