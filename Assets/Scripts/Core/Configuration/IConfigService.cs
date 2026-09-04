using System.Threading;
using System.Threading.Tasks;
using cfg;

namespace Roguelike.Core.Configuration
{
    /// <summary>
    /// Provides the single runtime entry point for all Luban-generated configuration tables.
    /// </summary>
    public interface IConfigService
    {
        /// <summary>
        /// Gets whether every generated table has been loaded and cross references have been resolved.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Gets the Luban table container after successful initialization.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">Thrown when configuration has not finished loading.</exception>
        Tables Tables { get; }

        /// <summary>
        /// Loads all Luban binary tables from the configured data source and resolves table references.
        /// </summary>
        /// <param name="cancellationToken">Token observed before and after each asynchronous file load.</param>
        /// <returns>A task that completes when the generated <see cref="Tables"/> container is ready.</returns>
        Task InitializeAsync(CancellationToken cancellationToken);
    }
}
