using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Roguelike.Core.Startup
{
    /// <summary>
    /// Represents one ordered, independently testable application initialization stage.
    /// </summary>
    public interface IStartupStep
    {
        /// <summary>Gets the diagnostic name reported by the startup pipeline.</summary>
        string Name { get; }

        /// <summary>
        /// Executes this stage after all preceding stages have completed successfully.
        /// </summary>
        /// <param name="cancellationToken">Token that cancels application startup.</param>
        /// <returns>A task representing the stage lifetime.</returns>
        Task ExecuteAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Runs startup stages sequentially so configuration and features cannot race resource initialization.
    /// </summary>
    public sealed class StartupPipeline
    {
        private readonly IReadOnlyList<IStartupStep> steps;

        /// <summary>
        /// Creates a startup pipeline with the order supplied by the application composition root.
        /// </summary>
        /// <param name="steps">Ordered startup stages; the sequence is copied for stable execution.</param>
        /// <exception cref="ArgumentNullException">Thrown when the stage sequence is missing.</exception>
        public StartupPipeline(IEnumerable<IStartupStep> steps)
        {
            if (steps == null)
            {
                throw new ArgumentNullException(nameof(steps));
            }

            this.steps = new List<IStartupStep>(steps);
        }

        /// <summary>
        /// Executes each stage once in declaration order and stops immediately on cancellation or failure.
        /// </summary>
        /// <param name="cancellationToken">Token checked before every stage and passed into the stage.</param>
        /// <returns>A task that completes after the final stage succeeds.</returns>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            foreach (IStartupStep step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await step.ExecuteAsync(cancellationToken);
            }
        }
    }
}
