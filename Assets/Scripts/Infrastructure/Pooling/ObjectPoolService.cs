using System;
using System.Collections.Generic;
using Roguelike.Core.Pooling;

namespace Roguelike.Infrastructure.Pooling
{
    /// <summary>
    /// Stores feature-scoped plain objects by pool identifier and exact runtime type.
    /// </summary>
    public sealed class ObjectPoolService : IObjectPoolService
    {
        private readonly Dictionary<PoolKey, Stack<object>> pools = new Dictionary<PoolKey, Stack<object>>();

        /// <summary>
        /// Returns the most recently pooled object or creates a new one through the caller-owned factory.
        /// </summary>
        /// <typeparam name="TObject">Exact reference type stored by this pool channel.</typeparam>
        /// <param name="poolId">Feature-scoped channel identifier.</param>
        /// <param name="factory">Factory invoked only when no cached instance exists.</param>
        /// <returns>An instance whose ownership transfers to the caller.</returns>
        /// <exception cref="ArgumentException">Thrown when the pool identifier is empty.</exception>
        /// <exception cref="ArgumentNullException">Thrown when the factory is missing.</exception>
        public TObject Rent<TObject>(string poolId, Func<TObject> factory) where TObject : class
        {
            ValidatePoolId(poolId);
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            var key = new PoolKey(poolId, typeof(TObject));
            return pools.TryGetValue(key, out Stack<object> pool) && pool.Count > 0
                ? (TObject)pool.Pop()
                : factory();
        }

        /// <summary>
        /// Returns a non-null object to the channel matching its declared generic type.
        /// </summary>
        /// <typeparam name="TObject">Exact reference type used during rent.</typeparam>
        /// <param name="poolId">Feature-scoped channel identifier.</param>
        /// <param name="instance">Instance whose ownership transfers to the pool.</param>
        /// <exception cref="ArgumentException">Thrown when the pool identifier is empty.</exception>
        /// <exception cref="ArgumentNullException">Thrown when the returned instance is missing.</exception>
        public void Return<TObject>(string poolId, TObject instance) where TObject : class
        {
            ValidatePoolId(poolId);
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            var key = new PoolKey(poolId, typeof(TObject));
            if (!pools.TryGetValue(key, out Stack<object> pool))
            {
                pool = new Stack<object>();
                pools.Add(key, pool);
            }

            pool.Push(instance);
        }

        /// <summary>
        /// Releases every cached object reference; feature-owned rented objects are unaffected.
        /// </summary>
        public void Clear()
        {
            pools.Clear();
        }

        /// <summary>
        /// Enforces explicit, non-empty pool ownership identifiers at the service boundary.
        /// </summary>
        /// <param name="poolId">Identifier supplied by a feature.</param>
        /// <exception cref="ArgumentException">Thrown when the identifier is empty or whitespace.</exception>
        private static void ValidatePoolId(string poolId)
        {
            if (string.IsNullOrWhiteSpace(poolId))
            {
                throw new ArgumentException("Pool identifier cannot be empty.", nameof(poolId));
            }
        }

        /// <summary>
        /// Prevents channels with the same text but different object types from sharing unsafe instances.
        /// </summary>
        private readonly struct PoolKey : IEquatable<PoolKey>
        {
            private readonly string id;
            private readonly Type type;

            /// <summary>
            /// Creates a typed pool dictionary key.
            /// </summary>
            /// <param name="id">Feature-scoped pool identifier.</param>
            /// <param name="type">Exact stored object type.</param>
            public PoolKey(string id, Type type)
            {
                this.id = id;
                this.type = type;
            }

            /// <summary>
            /// Compares both channel text and exact stored type.
            /// </summary>
            /// <param name="other">Candidate pool key.</param>
            /// <returns>True when both key components match.</returns>
            public bool Equals(PoolKey other)
            {
                return string.Equals(id, other.id, StringComparison.Ordinal) && type == other.type;
            }

            /// <summary>
            /// Compares an arbitrary object to this typed pool key.
            /// </summary>
            /// <param name="obj">Candidate object.</param>
            /// <returns>True only for an equal <see cref="PoolKey"/>.</returns>
            public override bool Equals(object obj)
            {
                return obj is PoolKey other && Equals(other);
            }

            /// <summary>
            /// Combines the ordinal pool identifier and exact type for dictionary lookup.
            /// </summary>
            /// <returns>A stable hash code for this process.</returns>
            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(id) * 397) ^ type.GetHashCode();
                }
            }
        }
    }
}
