using System.Threading;
using System.Threading.Tasks;

namespace Roguelike.Core.Features
{
    /// <summary>
    /// Defines the only application-facing contract implemented by a feature module.
    /// </summary>
    public interface IGameFeature
    {
        /// <summary>Gets the diagnostic feature name.</summary>
        string Name { get; }

        /// <summary>
        /// Initializes the feature through service contracts after resources and configuration are ready.
        /// </summary>
        /// <param name="services">Explicit application services; no implementation locator is exposed.</param>
        /// <param name="cancellationToken">Token that cancels startup.</param>
        /// <returns>A task representing feature initialization.</returns>
        Task InitializeAsync(ApplicationServices services, CancellationToken cancellationToken);
    }
}
