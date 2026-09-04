using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Roguelike.Core.Startup;
using Roguelike.Infrastructure.Events;
using Roguelike.Infrastructure.Pooling;

namespace Roguelike.Tests
{
    /// <summary>
    /// Verifies the provider-neutral startup, event, and object-pool infrastructure contracts.
    /// </summary>
    public sealed class ArchitectureServiceTests
    {
        /// <summary>
        /// Confirms startup steps execute sequentially in the composition root's declared order.
        /// </summary>
        [Test]
        public void StartupPipelineRunsInOrder()
        {
            var calls = new List<int>();
            var pipeline = new StartupPipeline(new IStartupStep[]
            {
                new RecordingStep(1, calls),
                new RecordingStep(2, calls),
                new RecordingStep(3, calls)
            });

            pipeline.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, calls);
        }

        /// <summary>
        /// Confirms disposing a subscription removes exactly that typed event listener.
        /// </summary>
        [Test]
        public void EventSubscriptionOwnsListenerLifetime()
        {
            var bus = new EventBus();
            int total = 0;
            IDisposable subscription = bus.Subscribe<int>(value => total += value);

            bus.Publish(2);
            subscription.Dispose();
            bus.Publish(4);

            Assert.AreEqual(2, total);
        }

        /// <summary>
        /// Confirms pool channels reuse returned objects and do not invoke the factory unnecessarily.
        /// </summary>
        [Test]
        public void ObjectPoolReusesReturnedInstance()
        {
            var pools = new ObjectPoolService();
            var expected = new object();
            pools.Return("test", expected);

            object actual = pools.Rent("test", () => new object());

            Assert.AreSame(expected, actual);
        }

        /// <summary>
        /// Test startup step that records execution without depending on Unity or infrastructure implementations.
        /// </summary>
        private sealed class RecordingStep : IStartupStep
        {
            private readonly int value;
            private readonly ICollection<int> calls;

            /// <summary>
            /// Creates a step that appends one value to the shared assertion collection.
            /// </summary>
            /// <param name="value">Value identifying this stage's expected order.</param>
            /// <param name="calls">Shared call-order collection.</param>
            public RecordingStep(int value, ICollection<int> calls)
            {
                this.value = value;
                this.calls = calls;
            }

            /// <summary>Gets the diagnostic test step name.</summary>
            public string Name => value.ToString();

            /// <summary>
            /// Records this step after observing cancellation.
            /// </summary>
            /// <param name="cancellationToken">Token checked before recording.</param>
            /// <returns>A completed task after the value is appended.</returns>
            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls.Add(value);
                return Task.CompletedTask;
            }
        }
    }
}
