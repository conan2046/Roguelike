using System;
using System.Threading;
using System.Threading.Tasks;
using Roguelike.Core;
using Roguelike.Core.Startup;

namespace Roguelike.Presentation
{
    /// <summary>
    /// Marks the presentation boundary ready after features have initialized, without taking feature implementation references.
    /// </summary>
    public sealed class PresentationStartupStep : IStartupStep
    {
        private readonly ApplicationServices services;

        /// <summary>
        /// Creates the presentation stage from Core service contracts only.
        /// </summary>
        /// <param name="services">Explicit application services.</param>
        /// <exception cref="ArgumentNullException">Thrown when service contracts are missing.</exception>
        public PresentationStartupStep(ApplicationServices services)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
        }

        /// <summary>Gets the diagnostic startup stage name.</summary>
        public string Name => "Presentation";

        /// <summary>
        /// Publishes presentation readiness after confirming startup has not been cancelled.
        /// </summary>
        /// <param name="cancellationToken">Token checked immediately before publication.</param>
        /// <returns>A completed task after the readiness message is published.</returns>
        /// <remarks>Publishes <see cref="PresentationReady"/> synchronously through the application event bus.</remarks>
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            services.Events.Publish(new PresentationReady());
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Signals that presentation adapters may begin responding to game events.
    /// </summary>
    public readonly struct PresentationReady
    {
    }
}
