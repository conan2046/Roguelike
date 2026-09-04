using System;
using Roguelike.Core.Configuration;
using Roguelike.Core.Events;
using Roguelike.Core.Pooling;
using Roguelike.Core.Resources;

namespace Roguelike.Core
{
    /// <summary>
    /// Carries explicit service contracts from the composition root into startup steps and feature modules.
    /// </summary>
    public sealed class ApplicationServices
    {
        /// <summary>
        /// Creates an immutable service set without exposing infrastructure implementations or a global locator.
        /// </summary>
        /// <param name="config">Unified Luban configuration service.</param>
        /// <param name="resources">ID-based resource service.</param>
        /// <param name="events">Typed application event bus.</param>
        /// <param name="pools">Plain-object pooling service.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required service is missing.</exception>
        public ApplicationServices(
            IConfigService config,
            IResourceService resources,
            IEventBus events,
            IObjectPoolService pools)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Resources = resources ?? throw new ArgumentNullException(nameof(resources));
            Events = events ?? throw new ArgumentNullException(nameof(events));
            Pools = pools ?? throw new ArgumentNullException(nameof(pools));
        }

        /// <summary>Gets the unified Luban configuration service.</summary>
        public IConfigService Config { get; }

        /// <summary>Gets the resource service that accepts only TbResource IDs.</summary>
        public IResourceService Resources { get; }

        /// <summary>Gets the typed application event bus.</summary>
        public IEventBus Events { get; }

        /// <summary>Gets the plain-object pool service.</summary>
        public IObjectPoolService Pools { get; }
    }
}
