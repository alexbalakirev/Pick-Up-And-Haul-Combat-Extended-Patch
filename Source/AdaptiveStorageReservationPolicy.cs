using System;

namespace PickUpAndHaulCECarryFix
{
    public static class AdaptiveStorageReservationPolicy
    {
        private const string AdaptiveStorageThingClassName = "AdaptiveStorage.ThingClass";

        public static bool ShouldReserveDuringGather(Type storageParentType)
            => !IsAdaptiveStorageType(storageParentType);

        public static bool ShouldRetryUnload(bool isAdaptiveStorage, bool otherwiseEligible, bool canReserveNow)
            => isAdaptiveStorage && otherwiseEligible && !canReserveNow;

        public static bool IsAdaptiveStorageType(Type type)
        {
            while (type != null)
            {
                if (string.Equals(type.FullName, AdaptiveStorageThingClassName, StringComparison.Ordinal))
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }
    }
}
