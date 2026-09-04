using System;
using System.Threading;
using System.Threading.Tasks;
using Roguelike.Core.Startup;

namespace Roguelike.Infrastructure.Resources
{
    /// <summary>
    /// Places YooAsset package initialization at the front of the ordered startup pipeline.
    /// </summary>
    public sealed class ResourceInitializationStep : IStartupStep
    {
        private readonly YooAssetPackageInitializer initializer;
        private readonly ResourcePlayMode playMode;

        /// <summary>
        /// Creates the resource stage with an explicit runtime policy.
        /// </summary>
        /// <param name="initializer">YooAsset lifecycle owner.</param>
        /// <param name="playMode">Local resource policy selected for this build environment.</param>
        /// <exception cref="ArgumentNullException">Thrown when the initializer is missing.</exception>
        public ResourceInitializationStep(YooAssetPackageInitializer initializer, ResourcePlayMode playMode)
        {
            this.initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
            this.playMode = playMode;
        }

        /// <summary>Gets the diagnostic startup stage name.</summary>
        public string Name => "Resources";

        /// <summary>
        /// Initializes the default YooAsset package before any table or game resource is requested.
        /// </summary>
        /// <param name="cancellationToken">Token forwarded to package initialization.</param>
        /// <returns>A task that completes when the package manifest is active.</returns>
        /// <remarks>Initializes application-owned YooAsset global state and must execute only once per application lifetime.</remarks>
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            return initializer.InitializeAsync(playMode, cancellationToken);
        }
    }
}
