using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Roguelike.Core.Startup;

namespace Roguelike.Core.Features
{
    /// <summary>
    /// Adapts independent feature modules into one ordered application startup stage.
    /// </summary>
    public sealed class FeatureStartupStep : IStartupStep
    {
        private readonly ApplicationServices services;
        private readonly IReadOnlyList<IGameFeature> features;

        /// <summary>
        /// Creates the feature stage and copies the feature sequence to prevent order changes during startup.
        /// </summary>
        /// <param name="services">Service contracts shared with every feature.</param>
        /// <param name="features">Feature modules in their intended initialization order.</param>
        /// <exception cref="ArgumentNullException">Thrown when services or the feature sequence is missing.</exception>
        public FeatureStartupStep(ApplicationServices services, IEnumerable<IGameFeature> features)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            this.features = features == null
                ? throw new ArgumentNullException(nameof(features))
                : new List<IGameFeature>(features);
        }

        /// <summary>Gets the diagnostic startup stage name.</summary>
        public string Name => "Features";

        /// <summary>
        /// Initializes features sequentially so a module can depend on contracts published by an earlier module.
        /// </summary>
        /// <param name="cancellationToken">Token checked before and passed into every feature.</param>
        /// <returns>A task that completes after all feature contracts are ready.</returns>
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            foreach (IGameFeature feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await feature.InitializeAsync(services, cancellationToken);
            }
        }
    }
}
