using System;
using System.Collections.Generic;

namespace PickUpAndHaulCECarryFix
{
    /// <summary>Memoizes a per-<see cref="Type"/> predicate, such as walking a type's base chain by name.</summary>
    internal sealed class TypeMemo
    {
        private readonly Func<Type, bool> compute;
        private readonly Dictionary<Type, bool> cache = new Dictionary<Type, bool>();
        private readonly object gate = new object();

        public TypeMemo(Func<Type, bool> compute)
        {
            this.compute = compute ?? throw new ArgumentNullException(nameof(compute));
        }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return cache.Count;
                }
            }
        }

        public bool Get(Type type)
        {
            if (type == null)
            {
                return compute(null);
            }

            lock (gate)
            {
                if (cache.TryGetValue(type, out var value))
                {
                    return value;
                }

                value = compute(type);
                cache[type] = value;
                return value;
            }
        }
    }
}
