using System;
using System.Threading;
using System.Threading.Tasks;

namespace Roguelike.Core.Resources
{
    /// <summary>
    /// Owns the lifetime of an asynchronously loaded Unity asset without exposing the resource provider.
    /// </summary>
    /// <typeparam name="TAsset">Requested runtime asset type.</typeparam>
    public interface IResourceHandle<out TAsset> : IDisposable where TAsset : class
    {
        /// <summary>
        /// Gets the loaded asset while this handle remains undisposed.
        /// </summary>
        TAsset Asset { get; }
    }

    /// <summary>
    /// Owns the lifetime of an asynchronously loaded raw resource file.
    /// </summary>
    public interface IRawResourceHandle : IDisposable
    {
        /// <summary>
        /// Gets the loaded file bytes while this handle remains undisposed.
        /// </summary>
        byte[] Data { get; }
    }

    /// <summary>
    /// Loads game resources exclusively by the identifier declared in Luban TbResource.id.
    /// </summary>
    public interface IResourceService
    {
        /// <summary>
        /// Loads an imported Unity asset after resolving its provider address through Luban TbResource.path.
        /// </summary>
        /// <typeparam name="TAsset">Expected Unity asset type.</typeparam>
        /// <param name="resourceId">Identifier from Luban TbResource.id.</param>
        /// <param name="cancellationToken">Token observed around the provider operation.</param>
        /// <returns>A lifetime handle containing the loaded asset.</returns>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when the resource ID is absent.</exception>
        /// <remarks>The caller owns the returned handle and must dispose it to release the provider reference.</remarks>
        Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken)
            where TAsset : class;

        /// <summary>
        /// Loads a raw file after resolving its provider address through Luban TbResource.path.
        /// </summary>
        /// <param name="resourceId">Identifier from Luban TbResource.id.</param>
        /// <param name="cancellationToken">Token observed around the provider operation.</param>
        /// <returns>A lifetime handle containing the raw bytes.</returns>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when the resource ID is absent.</exception>
        /// <remarks>The caller owns the returned handle and must dispose it to release the provider reference.</remarks>
        Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken);
    }
}
