using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Features.Combat.Runtime;
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
        private ApplicationLaunchOptions launch;
        private CombatSceneIsolation sceneIsolation;
        private CombatRuntimeRunner combatRunner;
        private Task startupTask;

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

            // 先验证互斥参数，再创建对象或停用旧场景；命令行标识是启动协议，不是玩法默认值。
            var launch = ApplicationLaunchOptions.Parse(Environment.GetCommandLineArgs());
            installed = true;
            var root = new GameObject("[Roguelike.Application]");
            DontDestroyOnLoad(root);
            var bootstrap = root.AddComponent<GameApplicationBootstrap>();
            bootstrap.launch = launch;
            if (launch.Mode == ApplicationMode.Combat || launch.Mode == ApplicationMode.Stage)
                bootstrap.sceneIsolation = new CombatSceneIsolation(root);
        }

        /// <summary>
        /// Called by Unity on the first frame to execute resources, configuration, one explicitly selected feature mode, then presentation.
        /// </summary>
        /// <remarks>Owns YooAsset and the optional combat runner; failure closes combat and restores suspended scene roots without saving scene assets.</remarks>
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

            launch = launch ?? ApplicationLaunchOptions.Parse(Environment.GetCommandLineArgs());
            var features = new List<IGameFeature>();
            switch (launch.Mode)
            {
                case ApplicationMode.Stage:
                    throw new InvalidOperationException("Formal -stage runtime is not connected yet.");
                case ApplicationMode.Combat: features.Add(new CombatStartupFeature(this, launch.ScenarioId.Value)); break;
                case ApplicationMode.Performance: features.Add(new PerformanceFeature()); break;
                default: features.Add(new GameplayFeature()); break;
            }

            var pipeline = new StartupPipeline(new IStartupStep[]
            {
                new ResourceInitializationStep(packageInitializer, SelectPlayMode()),
                new ConfigurationInitializationStep(config),
                new FeatureStartupStep(services, features),
                new PresentationStartupStep(services)
            });

            try
            {
                startupTask = pipeline.RunAsync(shutdown.Token);
                await startupTask;
                Debug.Log("[Roguelike] Application startup completed.", this);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                combatRunner?.Close();
                Debug.Log("[Roguelike] Application startup cancelled during shutdown.", this);
            }
            catch (Exception exception)
            {
                combatRunner?.Close();
                sceneIsolation?.Dispose(); sceneIsolation = null;
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
        /// <remarks>Closes combat before package disposal. During a normal object teardown, waits for cancelled IO continuations to release their handles before destroying YooAsset; process exit may terminate this final continuation.</remarks>
        private async void OnDestroy()
        {
            shutdown.Cancel();
            combatRunner?.Close();
            sceneIsolation?.Dispose(); sceneIsolation = null;
            try
            {
                if (startupTask != null) await startupTask;
            }
            catch (Exception)
            {
                // Start owns reporting startup failures; teardown only drains provider handle cleanup.
            }
            finally
            {
                packageInitializer?.Dispose();
                shutdown.Dispose();
            }
        }

        /// <summary>组合根私有战斗启动步骤，在统一配置就绪后显式消费场景 ID。</summary>
        private sealed class CombatStartupFeature : IGameFeature
        {
            private readonly GameApplicationBootstrap owner;
            private readonly int scenarioId;
            public string Name => "Combat";

            /// <summary>构造互斥启动分支，不提前访问配置。</summary>
            /// <param name="owner">持有整个应用生命周期的组合根。</param>
            /// <param name="scenarioId">命令行显式指定的场景表 ID。</param>
            internal CombatStartupFeature(GameApplicationBootstrap owner, int scenarioId) { this.owner = owner; this.scenarioId = scenarioId; }

            /// <summary>Luban 就绪后验证 Combat 场景，创建并等待正式交互入口加载完成。</summary>
            /// <param name="services">已初始化的配置和资源服务。</param>
            /// <param name="cancellationToken">应用退出令牌。</param>
            /// <returns>整局加载完成任务，失败不会发布表现就绪事件。</returns>
            /// <remarks>运行器作为持久应用根的子对象，资源销毁前由组合根显式关闭。</remarks>
            /// <exception cref="InvalidOperationException">请求 ID 不存在或不是 Combat。</exception>
            public async Task InitializeAsync(ApplicationServices services, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scenario = services.Config.Tables.TbPerformanceScenario.GetOrDefault(scenarioId);
                if (scenario == null || scenario.Kind != EPerformanceKind.Combat)
                    throw new InvalidOperationException("Requested TbPerformanceScenario is not a Combat scenario: " + scenarioId);
                var root = new GameObject("[Roguelike.Combat]"); root.transform.SetParent(owner.transform, false);
                owner.combatRunner = root.AddComponent<CombatRuntimeRunner>();
                await owner.combatRunner.InitializeAsync(scenario, services.Resources, new UnityCombatInput(scenario.PresentationId_Ref), cancellationToken);
            }
        }
    }
}
