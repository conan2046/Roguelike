using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Roguelike.App;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

namespace Roguelike.Tests
{
    /// <summary>
    /// Exercises the real editor-simulation package, Luban bootstrap, and ID-only raw resource path end to end.
    /// </summary>
    public sealed class YooAssetStartupTests
    {
        /// <summary>
        /// Initializes the local package, validates the generated performance scenario, then requests the first configured resource by TbResource.id.
        /// </summary>
        /// <returns>Unity coroutine that yields while the real YooAsset operations complete.</returns>
        /// <remarks>Destroys the automatic composition root first so this isolated test owns YooAsset global state.</remarks>
        [UnityTest]
        public IEnumerator EditorSimulationLoadsLubanAndRawResourceById()
        {
            GameApplicationBootstrap automaticBootstrap = Object.FindObjectOfType<GameApplicationBootstrap>();
            if (automaticBootstrap != null)
            {
                Object.DestroyImmediate(automaticBootstrap.gameObject);
            }

            if (YooAssets.Initialized)
            {
                YooAssets.Destroy();
            }

            using (var initializer = new YooAssetPackageInitializer())
            {
                Task initializePackage = initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None);
                yield return WaitForTask(initializePackage);
                RethrowTaskFailure(initializePackage);

                var config = new LubanConfigService(initializer);
                Task initializeConfig = config.InitializeAsync(CancellationToken.None);
                yield return WaitForTask(initializeConfig);
                RethrowTaskFailure(initializeConfig);

                Assert.IsTrue(config.IsInitialized);
                Assert.IsNotEmpty(config.Tables.TbResource.DataList);
                Assert.AreEqual(1000, config.Tables.TbPerformanceScenario.Get(1).EntityCount);
                Assert.GreaterOrEqual(config.Tables.TbPerformanceScenario.Get(1).TargetAverageFps, 60f);
                Assert.Greater(config.Tables.TbPerformanceScenario.Get(1).SampleSeconds, 0f);

                int resourceId = config.Tables.TbResource.DataList[0].Id;
                var resources = new YooAssetResourceService(initializer, config);
                Task<Roguelike.Core.Resources.IRawResourceHandle> loadResource =
                    resources.LoadRawFileAsync(resourceId, CancellationToken.None);
                yield return WaitForTask(loadResource);
                RethrowTaskFailure(loadResource);

                using (Roguelike.Core.Resources.IRawResourceHandle handle = loadResource.Result)
                {
                    Assert.IsNotNull(handle.Data);
                    Assert.IsNotEmpty(handle.Data);
                }
            }
        }

        /// <summary>
        /// Produces a Unity yield instruction that completes when a standard task reaches any terminal state.
        /// </summary>
        /// <param name="task">Task driven by YooAsset's player-loop operation system.</param>
        /// <returns>A wait instruction suitable for a UnityTest coroutine.</returns>
        private static WaitUntil WaitForTask(Task task)
        {
            return new WaitUntil(() => task.IsCompleted);
        }

        /// <summary>
        /// Unwraps an asynchronous failure on the Unity test thread so the runner records the original cause.
        /// </summary>
        /// <param name="task">Completed task to inspect.</param>
        /// <exception cref="System.AggregateException">Thrown when the asynchronous operation failed.</exception>
        /// <exception cref="System.Threading.Tasks.TaskCanceledException">Thrown when the asynchronous operation was cancelled.</exception>
        private static void RethrowTaskFailure(Task task)
        {
            if (task.IsCanceled)
            {
                throw new TaskCanceledException(task);
            }

            if (task.Exception != null)
            {
                throw task.Exception;
            }
        }
    }
}
