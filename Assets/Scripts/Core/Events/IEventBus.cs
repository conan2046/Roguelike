using System;

namespace Roguelike.Core.Events
{
    /// <summary>
    /// Decouples feature and presentation communication through typed in-process messages.
    /// </summary>
    public interface IEventBus
    {
        /// <summary>
        /// Registers a typed listener and returns a subscription that removes it when disposed.
        /// </summary>
        /// <typeparam name="TEvent">Message type used as the dispatch channel.</typeparam>
        /// <param name="listener">Callback invoked synchronously when the message is published.</param>
        /// <returns>A disposable subscription owned by the caller.</returns>
        IDisposable Subscribe<TEvent>(Action<TEvent> listener);

        /// <summary>
        /// Publishes a typed message to a stable snapshot of current listeners.
        /// </summary>
        /// <typeparam name="TEvent">Message type used as the dispatch channel.</typeparam>
        /// <param name="message">Message value delivered to listeners.</param>
        /// <remarks>Invokes all current listeners synchronously on the publishing thread.</remarks>
        void Publish<TEvent>(TEvent message);
    }
}
