using System;

namespace Roguelike.Core.Pooling
{
    /// <summary>
    /// Reuses plain runtime objects without coupling features to a concrete pooling library.
    /// </summary>
    public interface IObjectPoolService
    {
        /// <summary>
        /// Rents an existing object from a named pool or creates one through the caller-supplied factory.
        /// </summary>
        /// <typeparam name="TObject">Reference type stored by the pool.</typeparam>
        /// <param name="poolId">Stable technical identifier scoped by the calling feature.</param>
        /// <param name="factory">Factory used only when the pool is empty.</param>
        /// <returns>A reusable object owned by the caller until returned.</returns>
        TObject Rent<TObject>(string poolId, Func<TObject> factory) where TObject : class;

        /// <summary>
        /// Returns an object to the named pool for later reuse.
        /// </summary>
        /// <typeparam name="TObject">Reference type stored by the pool.</typeparam>
        /// <param name="poolId">Stable technical identifier used during rent.</param>
        /// <param name="instance">Object whose ownership is transferred back to the pool.</param>
        void Return<TObject>(string poolId, TObject instance) where TObject : class;

        /// <summary>
        /// Drops every cached reference, normally during application shutdown or test isolation.
        /// </summary>
        void Clear();
    }
}
