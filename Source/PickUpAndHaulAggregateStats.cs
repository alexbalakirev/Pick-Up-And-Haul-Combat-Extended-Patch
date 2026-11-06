using System.Diagnostics;
using Verse;

namespace PickUpAndHaulCECarryFix
{
    /// <summary>
    /// Aggregated hot-path counters. Values accumulate in static fields and one summary line is logged roughly every
    /// <see cref="FlushIntervalTicks"/> game ticks while diagnostics are enabled. Nothing is logged per call.
    /// </summary>
    public static class PickUpAndHaulAggregateStats
    {
        public const int FlushIntervalTicks = 2500;

        private static int lastFlushTick = int.MinValue;

        private static long hasJobCalls;
        private static long hasJobTimestamps;
        private static long adaptiveFallbackInvocations;
        private static long adaptiveCellsExamined;

        private static long unloadSelections;
        private static long unloadStorageSearches;
        private static long unloadRetryTicks;

        private static long haulMoreCalls;
        private static long haulMoreListSize;
        private static long haulMoreItemsPopped;
        private static long haulMoreValidatorCalls;

        private static long optimizedPlans;
        private static long optimizedCandidatesInRadius;
        private static long optimizedValidatorCalls;
        private static long optimizedStatEvaluations;

        public static long BeginTimer()
            => PickUpAndHaulDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0L;

        public static void EndHasJobOnThing(long startTimestamp)
        {
            if (startTimestamp == 0L)
            {
                return;
            }

            hasJobCalls++;
            hasJobTimestamps += Stopwatch.GetTimestamp() - startTimestamp;
            MaybeFlush();
        }

        public static void CountAdaptiveFallback(int cellsExamined)
        {
            if (!PickUpAndHaulDiagnostics.Enabled)
            {
                return;
            }

            adaptiveFallbackInvocations++;
            adaptiveCellsExamined += cellsExamined;
        }

        public static void CountUnloadSelection(int storageSearches)
        {
            if (!PickUpAndHaulDiagnostics.Enabled)
            {
                return;
            }

            unloadSelections++;
            unloadStorageSearches += storageSearches;
            MaybeFlush();
        }

        public static void CountUnloadRetryTick()
        {
            if (!PickUpAndHaulDiagnostics.Enabled)
            {
                return;
            }

            unloadRetryTicks++;
        }

        public static void CountHaulMoreToil(int listSize, int itemsPopped, int validatorCalls)
        {
            if (!PickUpAndHaulDiagnostics.Enabled)
            {
                return;
            }

            haulMoreCalls++;
            haulMoreListSize += listSize;
            haulMoreItemsPopped += itemsPopped;
            haulMoreValidatorCalls += validatorCalls;
            MaybeFlush();
        }

        public static void CountOptimizedPlan(int candidatesInRadius, int validatorCalls, int statEvaluations)
        {
            if (!PickUpAndHaulDiagnostics.Enabled)
            {
                return;
            }

            optimizedPlans++;
            optimizedCandidatesInRadius += candidatesInRadius;
            optimizedValidatorCalls += validatorCalls;
            optimizedStatEvaluations += statEvaluations;
            MaybeFlush();
        }

        private static void MaybeFlush()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            {
                return;
            }

            var tick = Find.TickManager.TicksGame;
            if (lastFlushTick == int.MinValue || tick < lastFlushTick)
            {
                lastFlushTick = tick;
                return;
            }

            if (tick - lastFlushTick < FlushIntervalTicks)
            {
                return;
            }

            lastFlushTick = tick;
            var hasJobMs = hasJobTimestamps * 1000.0 / Stopwatch.Frequency;
            Log.Message(
                "[Pick Up And Haul CE Carry Fix] Aggregate "
                + $"hasJobOnThing(calls={hasJobCalls}, totalMs={hasJobMs:F2}, adaptiveFallbacks={adaptiveFallbackInvocations}, adaptiveCellsExamined={adaptiveCellsExamined}); "
                + $"unload(selections={unloadSelections}, storageSearches={unloadStorageSearches}, retryTicks={unloadRetryTicks}); "
                + $"haulMore(calls={haulMoreCalls}, listSize={haulMoreListSize}, popped={haulMoreItemsPopped}, validatorCalls={haulMoreValidatorCalls}); "
                + $"optimizedPlans(plans={optimizedPlans}, candidatesInRadius={optimizedCandidatesInRadius}, validatorCalls={optimizedValidatorCalls}, statEvaluations={optimizedStatEvaluations})");

            hasJobCalls = 0;
            hasJobTimestamps = 0;
            adaptiveFallbackInvocations = 0;
            adaptiveCellsExamined = 0;
            unloadSelections = 0;
            unloadStorageSearches = 0;
            unloadRetryTicks = 0;
            haulMoreCalls = 0;
            haulMoreListSize = 0;
            haulMoreItemsPopped = 0;
            haulMoreValidatorCalls = 0;
            optimizedPlans = 0;
            optimizedCandidatesInRadius = 0;
            optimizedValidatorCalls = 0;
            optimizedStatEvaluations = 0;
        }
    }
}
