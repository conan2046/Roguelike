using System;
using System.Collections.Generic;
using Roguelike.Core.Events;

namespace Roguelike.Infrastructure.Events
{
    /// <summary>
    /// Provides synchronous typed dispatch while isolating listener mutations from an active publish pass.
    /// </summary>
    public sealed class EventBus : IEventBus
    {
        private readonly object gate = new object();
        private readonly Dictionary<Type, List<Delegate>> listeners = new Dictionary<Type, List<Delegate>>();

        /// <summary>
        /// Registers a typed listener and returns an idempotent disposable subscription.
        /// </summary>
        /// <typeparam name="TEvent">Message channel type.</typeparam>
        /// <param name="listener">Callback invoked during synchronous publication.</param>
        /// <returns>A subscription that removes this exact callback.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the callback is missing.</exception>
        public IDisposable Subscribe<TEvent>(Action<TEvent> listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            lock (gate)
            {
                Type eventType = typeof(TEvent);
                if (!listeners.TryGetValue(eventType, out List<Delegate> channel))
                {
                    channel = new List<Delegate>();
                    listeners.Add(eventType, channel);
                }

                channel.Add(listener);
            }

            return new Subscription(() => Unsubscribe(listener));
        }

        /// <summary>
        /// Publishes to a copied listener array so callbacks may safely subscribe or unsubscribe during dispatch.
        /// </summary>
        /// <typeparam name="TEvent">Message channel type.</typeparam>
        /// <param name="message">Message delivered in current subscription order.</param>
        /// <remarks>Invokes the current listener snapshot synchronously; listener mutations affect only later publications.</remarks>
        public void Publish<TEvent>(TEvent message)
        {
            Delegate[] snapshot;
            lock (gate)
            {
                if (!listeners.TryGetValue(typeof(TEvent), out List<Delegate> channel) || channel.Count == 0)
                {
                    return;
                }

                snapshot = channel.ToArray();
            }

            foreach (Delegate listener in snapshot)
            {
                ((Action<TEvent>)listener).Invoke(message);
            }
        }

        /// <summary>
        /// Removes one typed listener and deletes its empty channel to avoid retaining feature types.
        /// </summary>
        /// <typeparam name="TEvent">Message channel type.</typeparam>
        /// <param name="listener">Exact callback registered earlier.</param>
        private void Unsubscribe<TEvent>(Action<TEvent> listener)
        {
            lock (gate)
            {
                Type eventType = typeof(TEvent);
                if (!listeners.TryGetValue(eventType, out List<Delegate> channel))
                {
                    return;
                }

                channel.Remove(listener);
                if (channel.Count == 0)
                {
                    listeners.Remove(eventType);
                }
            }
        }

        /// <summary>
        /// Wraps listener removal so feature shutdown can be expressed through normal ownership disposal.
        /// </summary>
        private sealed class Subscription : IDisposable
        {
            private Action dispose;

            /// <summary>
            /// Creates a subscription around the listener-specific removal callback.
            /// </summary>
            /// <param name="dispose">Callback that removes the listener from its typed channel.</param>
            public Subscription(Action dispose)
            {
                this.dispose = dispose;
            }

            /// <summary>
            /// Removes the listener once and ignores repeated disposal by the same owner.
            /// </summary>
            public void Dispose()
            {
                Action callback = dispose;
                dispose = null;
                callback?.Invoke();
            }
        }
    }
}
