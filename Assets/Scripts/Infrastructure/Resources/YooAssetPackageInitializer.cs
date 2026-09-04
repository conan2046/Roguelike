using System;
using System.Threading;
using System.Threading.Tasks;
using YooAsset;

namespace Roguelike.Infrastructure.Resources
{
    /// <summary>
    /// Lists supported resource startup policies without leaking YooAsset play-mode values into feature modules.
    /// </summary>
    public enum ResourcePlayMode
    {
        /// <summary>Builds a virtual manifest from project assets inside the Unity Editor.</summary>
        EditorSimulate,

        /// <summary>Loads only package files embedded in the standalone player.</summary>
        Offline,

        /// <summary>Reserved extension point for a future remote package implementation.</summary>
        Host
    }

    /// <summary>
    /// Owns YooAsset global and package initialization for the application lifetime.
    /// </summary>
    public class YooAssetPackageInitializer : IDisposable
    {
        private ResourcePackage package;
        private bool disposed;

        /// <summary>
        /// Gets the initialized package used by configuration and resource services.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown before successful package initialization.</exception>
        public ResourcePackage Package => package != null && package.InitializeStatus == EOperationStatus.Succeed
            ? package
            : throw new InvalidOperationException("YooAsset package is not initialized.");

        /// <summary>
        /// Initializes YooAsset and the default package for EditorSimulate or Offline play.
        /// </summary>
        /// <param name="playMode">Provider policy selected by the application composition root.</param>
        /// <param name="cancellationToken">Token observed before and after YooAsset's operation.</param>
        /// <returns>A task that completes once the package manifest is active.</returns>
        /// <exception cref="NotSupportedException">Thrown for Host mode until a derived initializer supplies parameters.</exception>
        /// <exception cref="InvalidOperationException">Thrown when YooAsset reports initialization failure.</exception>
        /// <remarks>Creates or reuses YooAsset global state and keeps the package alive until this initializer is disposed.</remarks>
        public async Task InitializeAsync(ResourcePlayMode playMode, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            if (!YooAssets.Initialized)
            {
                YooAssets.Initialize();
            }

            package = YooAssets.TryGetPackage(YooAssetProjectConstants.PackageName)
                      ?? YooAssets.CreatePackage(YooAssetProjectConstants.PackageName);

            InitializeParameters parameters = CreateParameters(playMode);
            InitializationOperation operation = package.InitializeAsync(parameters);
            await operation.Task;
            cancellationToken.ThrowIfCancellationRequested();

            if (operation.Status != EOperationStatus.Succeed)
            {
                throw new InvalidOperationException($"YooAsset package initialization failed: {operation.Error}");
            }

            await ActivateLocalManifestAsync(cancellationToken);
        }

        /// <summary>
        /// Requests the version available in the selected local file system and activates its manifest before services can query assets.
        /// </summary>
        /// <param name="cancellationToken">Token observed between YooAsset's version and manifest operations.</param>
        /// <returns>A task that completes when the package exposes an active manifest.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the local version or manifest cannot be loaded.</exception>
        /// <remarks>Replaces the package's active manifest after both provider operations complete successfully.</remarks>
        private async Task ActivateLocalManifestAsync(CancellationToken cancellationToken)
        {
            RequestPackageVersionOperation versionOperation = package.RequestPackageVersionAsync(false);
            await versionOperation.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (versionOperation.Status != EOperationStatus.Succeed)
            {
                throw new InvalidOperationException($"YooAsset package version request failed: {versionOperation.Error}");
            }

            UpdatePackageManifestOperation manifestOperation =
                package.UpdatePackageManifestAsync(versionOperation.PackageVersion);
            await manifestOperation.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (manifestOperation.Status != EOperationStatus.Succeed)
            {
                throw new InvalidOperationException($"YooAsset package manifest activation failed: {manifestOperation.Error}");
            }
        }

        /// <summary>
        /// Creates provider parameters for the selected local play mode and routes Host mode to its future extension hook.
        /// </summary>
        /// <param name="playMode">Resource startup policy selected by the application.</param>
        /// <returns>YooAsset initialization parameters for the default package.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an unknown play mode.</exception>
        /// <remarks>EditorSimulate invokes YooAsset's simulate build and writes transient output under the ignored Bundles directory.</remarks>
        private InitializeParameters CreateParameters(ResourcePlayMode playMode)
        {
            switch (playMode)
            {
                case ResourcePlayMode.EditorSimulate:
#if UNITY_EDITOR
                    PackageInvokeBuildResult buildResult = EditorSimulateModeHelper.SimulateBuild(YooAssetProjectConstants.PackageName);
                    return new EditorSimulateModeParameters
                    {
                        EditorFileSystemParameters =
                            FileSystemParameters.CreateDefaultEditorFileSystemParameters(buildResult.PackageRootDirectory)
                    };
#else
                    throw new NotSupportedException("EditorSimulate mode is available only inside the Unity Editor.");
#endif
                case ResourcePlayMode.Offline:
                    return new OfflinePlayModeParameters
                    {
                        BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters()
                    };
                case ResourcePlayMode.Host:
                    return CreateHostPlayModeParameters();
                default:
                    throw new ArgumentOutOfRangeException(nameof(playMode), playMode, "Unknown resource play mode.");
            }
        }

        /// <summary>
        /// Supplies the deliberate future integration point for CDN and cache-file-system parameters.
        /// </summary>
        /// <returns>Host-mode parameters provided by a future single-player distribution requirement.</returns>
        /// <exception cref="NotSupportedException">Always thrown by the current offline-only implementation.</exception>
        protected virtual InitializeParameters CreateHostPlayModeParameters()
        {
            throw new NotSupportedException("HostPlayMode is reserved but CDN, download, and hot-update behavior are not implemented.");
        }

        /// <summary>
        /// Releases YooAsset global state owned by this composition root during application shutdown.
        /// </summary>
        /// <remarks>This destroys loaded resource handles and Unity objects managed by YooAsset.</remarks>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            package = null;
            if (YooAssets.Initialized)
            {
                YooAssets.Destroy();
            }
        }

        /// <summary>
        /// Prevents use after application shutdown, which would otherwise recreate global provider state unexpectedly.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(YooAssetPackageInitializer));
            }
        }
    }
}
