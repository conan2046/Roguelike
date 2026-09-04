using System;
using System.Collections.Generic;
using System.Threading;
using Roguelike.Core;
using Roguelike.Core.Features;
using Roguelike.Core.Startup;
using Roguelike.Features.Gameplay;
using Roguelike.Features.Performance;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Events;
using Roguelike.Infrastructure.Pooling;
using Roguelike.Infrastructure.Resources;
using Roguelike.Presentation;
using UnityEngine;

namespace Roguelike.App
{
    /// <summary>
    /// Acts as the sole composition root and runs the instance-owned startup pipeline before scene logic consumes services.
    /// </summary>
    public sealed class GameApplicationBootstrap : MonoBehaviour
    {
        private static bool installed;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private YooAssetPackageInitializer packageInitializer;

        /// <summary>
        /// Resets only the bootstrap duplication guard when Unity begins a new play session or reloads the subsystem.
        /// </summary>
        /// <remarks>This does not expose application services globally and is safe when domain reload is disabled.</remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetBootstrapGuard()
        {
            installed = false;
        }

        /// <summary>
        /// Creates the persistent application root before the first scene loads, except in editor batch runs owned by tests or tooling.
        /// </summary>
        /// <remarks>Creates one persistent Unity GameObject for the player lifetime; editor batch mode must compose its own isolated services.</remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if UNITY_EDITOR
            if (Application.isBatchMode)
            {
                return;
            }
#endif

            if (installed)
            {
                return;
            }

            installed = true;
            var root = new GameObject("[Roguelike.Application]");
            DontDestroyOnLoad(root);
            root.AddComponent<GameApplicationBootstrap>();
        }

        /// <summary>
        /// Called by Unity on the first frame to compose local services and execute resources, configuration, features, then presentation.
        /// </summary>
        /// <remarks>Creates and owns YooAsset global state; failure is logged and the bootstrap object remains for diagnostics.</remarks>
        private async void Start()
        {
            packageInitializer = new YooAssetPackageInitializer();
            var config = new LubanConfigService(packageInitializer);
            var resources = new YooAssetResourceService(packageInitializer, config);
            var services = new ApplicationServices(
                config,
                resources,
                new EventBus(),
                new ObjectPoolService());

            var features = new List<IGameFeature>
            {
                new GameplayFeature(),
                new PerformanceFeature()
            };

            var pipeline = new StartupPipeline(new IStartupStep[]
            {
                new ResourceInitializationStep(packageInitializer, SelectPlayMode()),
                new ConfigurationInitializationStep(config),
                new FeatureStartupStep(services, features),
                new PresentationStartupStep(services)
            });

            try
            {
                await pipeline.RunAsync(shutdown.Token);
                Debug.Log("[Roguelike] Application startup completed.", this);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                Debug.Log("[Roguelike] Application startup cancelled during shutdown.", this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        /// <summary>
        /// Selects the confirmed local resource policy from the build environment before configuration is available.
        /// </summary>
        /// <returns>EditorSimulate in the editor and Offline in a built player.</returns>
        private static ResourcePlayMode SelectPlayMode()
        {
#if UNITY_EDITOR
            return ResourcePlayMode.EditorSimulate;
#else
            return ResourcePlayMode.Offline;
#endif
        }

        /// <summary>
        /// Called by Unity when the persistent root is destroyed to cancel startup and release YooAsset-owned Unity resources.
        /// </summary>
        /// <remarks>Cancels pending startup, destroys YooAsset global objects, and disposes the cancellation token source.</remarks>
        private void OnDestroy()
        {
            shutdown.Cancel();
            packageInitializer?.Dispose();
            shutdown.Dispose();
        }
    }
}
