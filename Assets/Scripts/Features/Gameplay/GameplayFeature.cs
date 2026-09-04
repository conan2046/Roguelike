using System;
using System.Threading;
using System.Threading.Tasks;
using Roguelike.Core;
using Roguelike.Core.Features;

namespace Roguelike.Features.Gameplay
{
    /// <summary>
    /// Announces that the gameplay feature boundary is ready without referencing another feature implementation.
    /// </summary>
    public sealed class GameplayFeature : IGameFeature
    {
        /// <summary>Gets the diagnostic feature name.</summary>
        public string Name => "Gameplay";

        /// <summary>
        /// Validates the supplied contracts and publishes readiness after configuration is available.
        /// </summary>
        /// <param name="services">Core service contracts supplied by the application composition root.</param>
        /// <param name="cancellationToken">Token checked before publishing readiness.</param>
        /// <returns>A completed task after the readiness message is published.</returns>
        /// <exception cref="ArgumentNullException">Thrown when service contracts are missing.</exception>
        /// <exception cref="InvalidOperationException">Thrown when startup ordering did not initialize Luban first.</exception>
        /// <remarks>Publishes <see cref="GameplayReady"/> synchronously through the application event bus.</remarks>
        public Task InitializeAsync(ApplicationServices services, CancellationToken cancellationToken)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!services.Config.IsInitialized)
            {
                throw new InvalidOperationException("Gameplay cannot initialize before Luban configuration.");
            }

            services.Events.Publish(new GameplayReady());
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Signals through Core's event contract that gameplay services may now be consumed by presentation.
    /// </summary>
    public readonly struct GameplayReady
    {
    }
}
