using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Configuration;
using Roguelike.Core.Resources;
using YooAsset;

namespace Roguelike.Infrastructure.Resources
{
    /// <summary>
    /// Resolves Luban TbResource IDs to provider locations and owns YooAsset handles behind Core contracts.
    /// </summary>
    public sealed class YooAssetResourceService : IResourceService
    {
        private readonly YooAssetPackageInitializer packageInitializer;
        private readonly IConfigService configService;

        /// <summary>
        /// Creates the provider adapter without retaining any feature-owned resource address.
        /// </summary>
        /// <param name="packageInitializer">Initialized YooAsset package owner.</param>
        /// <param name="configService">Luban table entry point containing TbResource.</param>
        /// <exception cref="ArgumentNullException">Thrown when a dependency is missing.</exception>
        public YooAssetResourceService(YooAssetPackageInitializer packageInitializer, IConfigService configService)
        {
            this.packageInitializer = packageInitializer ?? throw new ArgumentNullException(nameof(packageInitializer));
            this.configService = configService ?? throw new ArgumentNullException(nameof(configService));
        }

        /// <summary>
        /// Resolves TbResource.path, loads a Unity asset, validates its type, and returns a provider-neutral handle.
        /// </summary>
        /// <typeparam name="TAsset">Expected Unity asset type.</typeparam>
        /// <param name="resourceId">Identifier from Luban TbResource.id.</param>
        /// <param name="cancellationToken">Token observed around the provider operation.</param>
        /// <returns>A handle that keeps the YooAsset provider reference alive.</returns>
        /// <remarks>The caller must dispose the returned handle; failed or cancelled operations release their provider handle internally.</remarks>
        public async Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken)
            where TAsset : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!typeof(UnityEngine.Object).IsAssignableFrom(typeof(TAsset)))
            {
                throw new ArgumentException($"Resource type '{typeof(TAsset).FullName}' is not a Unity asset.", nameof(TAsset));
            }

            ResourceConfig resource = ResolveResource(resourceId);
            RequirePackage(resource, EResourcePackage.GameAsset, nameof(LoadAssetAsync));
            AssetHandle handle = packageInitializer.GameAssetPackage.LoadAssetAsync(resource.Path, typeof(TAsset));
            try
            {
                await handle.Task;
                cancellationToken.ThrowIfCancellationRequested();
                if (handle.Status != EOperationStatus.Succeed)
                {
                    throw new InvalidOperationException($"Failed to load resource ID {resourceId}: {handle.LastError}");
                }

                TAsset asset = handle.AssetObject as TAsset;
                if (asset == null)
                {
                    throw new InvalidCastException($"Resource ID {resourceId} is not a {typeof(TAsset).FullName}.");
                }

                return new YooAssetObjectHandle<TAsset>(handle, asset);
            }
            catch
            {
                handle.Release();
                throw;
            }
        }

        /// <summary>
        /// Resolves TbResource.path, loads a raw file, and returns a provider-neutral byte handle.
        /// </summary>
        /// <param name="resourceId">Identifier from Luban TbResource.id.</param>
        /// <param name="cancellationToken">Token observed around the provider operation.</param>
        /// <returns>A handle that owns both the raw bytes and the YooAsset provider reference.</returns>
        /// <remarks>The caller must dispose the returned handle; failed or cancelled operations release their provider handle internally.</remarks>
        public async Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceConfig resource = ResolveResource(resourceId);
            RequirePackage(resource, EResourcePackage.Raw, nameof(LoadRawFileAsync));
            RawFileHandle handle = packageInitializer.RawPackage.LoadRawFileAsync(resource.Path);
            try
            {
                await handle.Task;
                cancellationToken.ThrowIfCancellationRequested();
                if (handle.Status != EOperationStatus.Succeed)
                {
                    throw new InvalidOperationException($"Failed to load raw resource ID {resourceId}: {handle.LastError}");
                }

                return new YooAssetRawHandle(handle, handle.GetRawFileData());
            }
            catch
            {
                handle.Release();
                throw;
            }
        }

        /// <summary>
        /// Resolves the provider address and package exclusively from the generated TbResource row.
        /// </summary>
        /// <param name="resourceId">Identifier from Luban TbResource.id.</param>
        /// <returns>The validated Luban TbResource row.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the ID or its path is missing.</exception>
        private ResourceConfig ResolveResource(int resourceId)
        {
            ResourceConfig resource = configService.Tables.TbResource.GetOrDefault(resourceId);
            if (resource == null || string.IsNullOrWhiteSpace(resource.Path))
            {
                throw new KeyNotFoundException($"Luban TbResource does not contain a valid path for ID {resourceId}.");
            }

            return resource;
        }

        /// <summary>
        /// Rejects a caller that requests a provider API incompatible with the Luban-declared package.
        /// </summary>
        /// <param name="resource">Resolved row from Luban TbResource.</param>
        /// <param name="expectedPackage">Package required by the selected resource API.</param>
        /// <param name="operation">Calling operation name included in diagnostics.</param>
        /// <exception cref="InvalidOperationException">Thrown when the row belongs to another package.</exception>
        private static void RequirePackage(
            ResourceConfig resource,
            EResourcePackage expectedPackage,
            string operation)
        {
            if (resource.Package != expectedPackage)
            {
                throw new InvalidOperationException(
                    $"Resource ID {resource.Id} uses package '{resource.Package}' and cannot be loaded by {operation}; " +
                    $"expected package '{expectedPackage}'.");
            }
        }

        /// <summary>
        /// Keeps an asset and its YooAsset reference count together until the feature releases ownership.
        /// </summary>
        /// <typeparam name="TAsset">Loaded Unity asset type.</typeparam>
        private sealed class YooAssetObjectHandle<TAsset> : IResourceHandle<TAsset> where TAsset : class
        {
            private AssetHandle handle;

            /// <summary>
            /// Creates a Core handle around a successfully completed YooAsset handle.
            /// </summary>
            /// <param name="handle">Provider handle whose reference count is owned here.</param>
            /// <param name="asset">Validated loaded asset.</param>
            public YooAssetObjectHandle(AssetHandle handle, TAsset asset)
            {
                this.handle = handle;
                Asset = asset;
            }

            /// <summary>Gets the loaded asset while this handle remains alive.</summary>
            public TAsset Asset { get; }

            /// <summary>
            /// Releases the YooAsset reference exactly once; the asset must not be used afterwards.
            /// </summary>
            public void Dispose()
            {
                handle?.Release();
                handle = null;
            }
        }

        /// <summary>
        /// Keeps raw bytes and their YooAsset reference count together until the feature releases ownership.
        /// </summary>
        private sealed class YooAssetRawHandle : IRawResourceHandle
        {
            private RawFileHandle handle;

            /// <summary>
            /// Creates a Core raw-file handle around a successfully completed YooAsset operation.
            /// </summary>
            /// <param name="handle">Provider handle whose reference count is owned here.</param>
            /// <param name="data">Raw bytes returned by the package.</param>
            public YooAssetRawHandle(RawFileHandle handle, byte[] data)
            {
                this.handle = handle;
                Data = data;
            }

            /// <summary>Gets the loaded raw bytes while this handle remains alive.</summary>
            public byte[] Data { get; }

            /// <summary>
            /// Releases the YooAsset reference exactly once; cached bytes remain caller-readable but are no longer provider-owned.
            /// </summary>
            public void Dispose()
            {
                handle?.Release();
                handle = null;
            }
        }
    }
}
