using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Roguelike.Tests
{
    /// <summary>真实 YooAsset 与独立 ECS 世界的可操作入口验收，不以离线选帧代替相机画面。</summary>
    [PrebuildSetup(typeof(CombatRuntimeTests))]
    [PostBuildCleanup(typeof(CombatRuntimeTests))]
    public sealed class CombatRuntimeTests : IPrebuildSetup, IPostBuildCleanup
    {
        /// <summary>Unity Test Runner 进入运行模式前标记隔离资源环境，避免命令行战斗入口与测试同时拥有 YooAsset。</summary>
        /// <remarks>只设置编辑器会话标记，不写配置、场景或项目设置；测试完成后清除。</remarks>
        public void Setup()
        {
#if UNITY_EDITOR
            UnityEditor.SessionState.SetBool("Roguelike.CombatRuntimeTests.Isolated", true);
#endif
        }

        /// <summary>Unity Test Runner 完成后清除隔离标记，后续人工 Play 仍正常启动游戏。</summary>
        public void Cleanup()
        {
#if UNITY_EDITOR
            UnityEditor.SessionState.EraseBool("Roguelike.CombatRuntimeTests.Isolated");
#endif
        }

        /// <summary>测试程序集加载后、自动入口安装前抑制本次测试的自动启动；仅隔离测试自行创建资源服务的场合。</summary>
        /// <remarks>SubsystemRegistration 已重置入口，测试只改当前域中的重复安装标记。下次普通 Play 自动重置，不改变正式运行代码。</remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void IsolateAutomaticBootstrap()
        {
#if UNITY_EDITOR
            if (!UnityEditor.SessionState.GetBool("Roguelike.CombatRuntimeTests.Isolated", false)) return;
            typeof(Roguelike.App.GameApplicationBootstrap).GetField("installed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, true);
#endif
        }

        /// <summary>共享美术加载后、会话 ANI 尚未返回时销毁入口，验证晚到句柄与部分世界均回收。</summary>
        /// <returns>等待真实 YooAsset 与受控晚到资源的协程。</returns>
        /// <remarks>资源服务保持存活直到启动任务结束；不依赖底层供应商立即取消 IO。</remarks>
        [UnityTest]
        public IEnumerator DestroyDuringLoadingReleasesPartialWorld()
        {
            using var initializer = new YooAssetPackageInitializer();
            yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(initializer); yield return Wait(config.InitializeAsync(CancellationToken.None));
            var source = new DeferredResources(new YooAssetResourceService(initializer, config));
            int worldCount = World.All.Count;
            var root = new GameObject("Combat cancellation test");
            var runner = root.AddComponent<CombatRuntimeRunner>();
            Task startup = runner.InitializeAsync(config.Tables.TbPerformanceScenario.Get(4), source, new TestInput(), CancellationToken.None);
            try
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 60;
                while (!source.Blocked && !startup.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.That(source.Blocked, Is.True, "Must reach the session load after shared visuals are prepared.");
                // Unity SceneSystem 可能为私有世界建立辅助 streaming worlds，不假定创建数量恰好为一。
                var ownedWorld = FindCombatWorld();
                Assert.That(ownedWorld.IsCreated, Is.True);
                runner.Close(); UnityEngine.Object.Destroy(root); yield return null;
                source.Complete();
                while (!startup.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.That(startup.IsCompleted, Is.True);
                Assert.Throws<OperationCanceledException>(() => startup.GetAwaiter().GetResult());
                Assert.That(source.Handle.Disposed, Is.True);
                Assert.That(ownedWorld.IsCreated, Is.False);
                Assert.That(World.All.Count, Is.EqualTo(worldCount));
            }
            finally { source.Complete(); if (root != null) { runner.Close(); UnityEngine.Object.Destroy(root); } }
        }

        /// <summary>连续进入两次，验证移动自动攻击、暂停、重开、死亡冻结，以及正常退出和 World 提前销毁后的完整清理。</summary>
        /// <returns>等待真实资源和 Unity 帧的测试协程。</returns>
        /// <remarks>测试组件改动只在隔离 World 内；输出真实相机截图，不保存 Scene/Prefab。</remarks>
        [UnityTest]
        public IEnumerator RealResourcesInputCameraAndRepeatedExit()
        {
            using var initializer = new YooAssetPackageInitializer();
            var packageTask = initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None);
            yield return Wait(packageTask);
            var config = new LubanConfigService(initializer); yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources = new YooAssetResourceService(initializer, config);
            var scenario = config.Tables.TbPerformanceScenario.Get(4);
            // 本用例验证输入/死亡/退出，保留出生即有目标的隔离夹具；圆周刷怪由专用用例验收。
            foreach (string field in new[] { "SpawnRadiusPixelsMilli", "SpawnIntervalSecondsMilli", "SpawnBatchCount", "SpawnUnitIntervalSecondsMilli" })
                typeof(cfg.PerformanceScenarioConfig).GetField(field).SetValue(scenario, null);
            int originalWorlds = World.All.Count;
            int originalFrameRate = Application.targetFrameRate, originalVSync = QualitySettings.vSyncCount;
            bool originalBackground = Application.runInBackground;
            for (int run = 0; run < 2; run++)
            {
                var root = new GameObject("Combat runtime test");
                var runner = root.AddComponent<CombatRuntimeRunner>(); var input = new TestInput();
                try
                {
                    yield return Wait(runner.InitializeAsync(scenario, resources, input, CancellationToken.None));
                    Assert.That(runner.Ready, Is.True); Assert.That(runner.ViewCamera, Is.Not.Null);
                    // 固定阵列夹具原点有怪物；移至玩家左侧，避免移动圆柱把本输入用例锁在出生点。
                    var inputWorld = FindCombatWorld();
                    for (int slot = 1; slot < runner.Session.UnitCount; slot++)
                    {
                        var unit = runner.Session.ReadUnit(slot);
                        unit.Position = new float2(-3 - (slot - 1) % 5, (slot - 1) / 5);
                        inputWorld.EntityManager.SetComponentData(runner.Session.UnitEntity(slot), unit);
                    }
                    var start = runner.Session.ReadUnit(0).Position;
                    input.Frame = new CombatInputFrame { Movement = new float2(1, 0) };
                    for (int frame = 0; frame < 12; frame++) yield return null;
                    Assert.That(runner.Session.ReadUnit(0).Position.x, Is.GreaterThan(start.x));
                    input.Frame = new CombatInputFrame { PausePressed = true };
                    yield return null;
                    Assert.That(runner.Session.Paused, Is.True);
                    ulong tick = runner.Session.Statistics.Tick;
                    for (int frame = 0; frame < 5; frame++) yield return null;
                    Assert.That(runner.Session.Statistics.Tick, Is.EqualTo(tick));
                    ulong lifetime = runner.Session.ReadUnit(0).Target.Lifetime;
                    input.Frame = new CombatInputFrame { RestartPressed = true };
                    yield return null;
                    Assert.That(runner.Session.Paused, Is.False);
                    Assert.That(runner.Session.ReadUnit(0).Target.Lifetime, Is.GreaterThan(lifetime));
                    for (int frame = 0; frame < 15; frame++) yield return null;
                    Assert.That(runner.Session.Statistics.ProjectileSpawns, Is.GreaterThan(0), "Auto fire must continue through the runtime input path.");
                    if (run == 0)
                    {
                        runner.Restart(); runner.TogglePause();
                        yield return CaptureCamera(runner.ViewCamera, "ecs-camera.png");
                    }
                    runner.Restart();
                    var world = FindCombatWorld();
                    var player = runner.Session.ReadUnit(0); player.Target.Health = 1; player.Cooldown = 100;
                    player.Target.Evasion = 0; player.Target.ImmunityCharges = 0; player.Target.InvulnerableUntil = 0;
                    world.EntityManager.SetComponentData(runner.Session.UnitEntity(0), player);
                    var monster = runner.Session.ReadUnit(1); monster.Position = player.Position; monster.AttackActive = false;
                    monster.Target.Health = monster.Target.MaxHealth; monster.Cooldown = 0; monster.Attack.Hit = 1;
                    world.EntityManager.SetComponentData(runner.Session.UnitEntity(1), monster);
                    for (int frame = 0; frame < 180 && !runner.Session.Statistics.PlayerDead; frame++)
                    {
                        runner.AdvanceFrame(runner.Session.StepSeconds, default);
                        yield return null;
                    }
                    Assert.That(runner.Session.Statistics.PlayerDead, Is.True);
                    tick = runner.Session.Statistics.Tick;
                    input.Frame = new CombatInputFrame { Movement = new float2(1), PausePressed = true };
                    for (int frame = 0; frame < 5; frame++) yield return null;
                    Assert.That(runner.Session.Statistics.Tick, Is.EqualTo(tick));
                    runner.Restart(); Assert.That(runner.Session.Statistics.PlayerDead, Is.False);
                    if (run == 1) FindCombatWorld().Dispose();
                    Assert.DoesNotThrow(() => runner.Close());
                }
                finally { runner.Close(); UnityEngine.Object.Destroy(root); }
                yield return null;
                Assert.That(World.All.Count, Is.EqualTo(originalWorlds));
                Assert.That(Application.targetFrameRate, Is.EqualTo(originalFrameRate));
                Assert.That(QualitySettings.vSyncCount, Is.EqualTo(originalVSync));
                Assert.That(Application.runInBackground, Is.EqualTo(originalBackground));
            }
        }

        /// <summary>用正式 TbStage 1 加载五个 UI Prefab，完成升级选择、18 分钟 Boss 边界、胜利和按钮重开。</summary>
        /// <returns>等待真实 YooAsset、渲染帧和加速战斗状态的协程。</returns>
        /// <remarks>时间线在纯模型中快进到 Boss 前一个固定 tick，再由真实协调器跨过边界；不等待 18 分钟墙钟。</remarks>
        [UnityTest]
        public IEnumerator FormalStagePrefabUiAndAcceleratedRunLoop()
        {
            using var initializer = new YooAssetPackageInitializer();
            yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(initializer);
            yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources = new YooAssetResourceService(initializer, config);
            CombatRunDefinition definition = CombatRunDefinition.Create(config.Tables, 1);
            Assert.DoesNotThrow(definition.RequireUiReady);
            int originalWorlds = World.All.Count;
            var root = new GameObject("Formal stage runtime test");
            var runner = root.AddComponent<CombatRuntimeRunner>();
            try
            {
                yield return Wait(runner.InitializeAsync(definition, resources, new TestInput(), CancellationToken.None));
                Assert.That(runner.Ready, Is.True);
                Assert.That(runner.FormalUiRoot, Is.Not.Null);
                Assert.That(runner.FormalUiRoot.GetComponent<CombatHudView>(), Is.Not.Null);
                Assert.That(runner.RunModel.State, Is.EqualTo(CombatRunState.Playing));
                Assert.That(runner.Coordinator, Is.Not.Null);
                yield return CaptureCamera(runner.ViewCamera, "formal-stage.png");

                int required = definition.ExperienceLevels[runner.RunModel.Level - 1].RequiredExperience;
                runner.RunModel.GrantExperience(required);
                runner.AdvanceFrame(0, default);
                Assert.That(runner.RunModel.State, Is.EqualTo(CombatRunState.UpgradeChoice));
                UpgradeChoicePanelView upgrade = runner.FormalUiRoot.GetComponentInChildren<UpgradeChoicePanelView>(true);
                Assert.That(upgrade.gameObject.activeSelf, Is.True);
                Assert.That(upgrade.CardContainer.childCount, Is.EqualTo(definition.UpgradePool.DrawCount));
                upgrade.CardContainer.GetChild(0).GetComponent<UpgradeCardView>().SelectButton.onClick.Invoke();
                Assert.That(runner.RunModel.State, Is.EqualTo(CombatRunState.Playing));
                Assert.That(upgrade.gameObject.activeSelf, Is.False);

                int oneTickMilli = 1000 / definition.CombatRules.SimulationHz;
                runner.RunModel.Advance(checked((int)(definition.Rule.BossTimeMilli - runner.RunModel.ElapsedMilli - oneTickMilli)));
                runner.AdvanceFrame(runner.Session.StepSeconds, default);
                Assert.That(runner.RunModel.State, Is.EqualTo(CombatRunState.Boss));
                BossHealthBarView bossView = runner.FormalUiRoot.GetComponentInChildren<BossHealthBarView>(true);
                Assert.That(bossView.gameObject.activeSelf, Is.True);

                int bossSlot = Enumerable.Range(1, runner.Session.UnitCount - 1)
                    .Single(slot => runner.Session.ReadUnit(slot).IsBoss && runner.Session.ReadUnit(slot).Target.Health > 0);
                World combatWorld = FindCombatWorld();
                CombatUnit player = runner.Session.ReadUnit(0);
                player.Cooldown = 0;
                player.Attack.Attack = 1000000;
                player.Attack.Hit = 1;
                combatWorld.EntityManager.SetComponentData(runner.Session.UnitEntity(0), player);
                CombatUnit boss = runner.Session.ReadUnit(bossSlot);
                boss.Position = player.Position + new float2(1, 0);
                boss.PreviousPosition = boss.Position;
                boss.MoveSpeed = 0;
                boss.Cooldown = 1000;
                boss.Target.Health = 1;
                boss.Target.Evasion = 0;
                combatWorld.EntityManager.SetComponentData(runner.Session.UnitEntity(bossSlot), boss);
                for (int tick = 0; tick < 180 && runner.RunModel.State != CombatRunState.VictorySettlement; tick++)
                    runner.AdvanceFrame(runner.Session.StepSeconds, default);
                Assert.That(runner.RunModel.State, Is.EqualTo(CombatRunState.VictorySettlement));
                SettlementPanelView settlement = runner.FormalUiRoot.GetComponentInChildren<SettlementPanelView>(true);
                Assert.That(settlement.gameObject.activeSelf, Is.True);
                settlement.RestartButton.onClick.Invoke();
                Assert.That(runner.RunModel.State, Is.EqualTo(CombatRunState.Playing));
                Assert.That(runner.RunModel.ElapsedMilli, Is.Zero);
                Assert.That(settlement.gameObject.activeSelf, Is.False);
                Assert.That(Enumerable.Range(0, runner.Session.PlayerWeaponCount)
                    .Count(index => runner.Session.ReadPlayerWeapon(index).Active), Is.EqualTo(1));
            }
            finally
            {
                runner.Close();
                UnityEngine.Object.Destroy(root);
            }
            yield return null;
            Assert.That(World.All.Count, Is.EqualTo(originalWorlds));
        }

        /// <summary>测试读取专属世界；使用具体枚举器，Entities 禁止将 World.All 装箱给 LINQ。</summary>
        /// <returns>唯一已启动的战斗世界。</returns>
        /// <exception cref="InvalidOperationException">世界缺失或重复。</exception>
        private static World FindCombatWorld()
        {
            World found = null;
            foreach (var candidate in World.All)
                if (candidate.Name == "Roguelike Combat")
                {
                    if (found != null) throw new InvalidOperationException("Duplicate combat worlds.");
                    found = candidate;
                }
            return found ?? throw new InvalidOperationException("Combat world not found.");
        }

        /// <summary>GPU 读回真实战斗相机，检查内容不同于背景后保存证据。</summary>
        /// <param name="camera">运行器创建的相机。</param>
        /// <param name="fileName">输出到 runtime-review 的证据文件名。</param>
        /// <returns>等待世界 LateUpdate 和首次 Shader/GPU 准备的协程，超时仍严格判失败。</returns>
        /// <remarks>临时目标和 CPU 纹理在 finally 销毁，恢复相机原目标。</remarks>
        private static IEnumerator CaptureCamera(Camera camera, string fileName)
        {
            var target = new RenderTexture(1280, 720, 24); var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
            try
            {
                target.Create(); camera.targetTexture = target;
                int different = 0;
                double deadline = Time.realtimeSinceStartupAsDouble + 10;
                do
                {
                    // 初始 GPU 注册与 Shader 编译可能跨帧；暂停出生画面以免等待期间单位被消灭。
                    yield return null;
                    camera.Render(); RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    var colors = pixels.GetPixels32(); var background = colors[0]; different = 0;
                    foreach (var color in colors)
                        if (Math.Abs(color.r - background.r) + Math.Abs(color.g - background.g) + Math.Abs(color.b - background.b) > 40) different++;
                    RenderTexture.active = oldActive;
                } while (different <= 500 && Time.realtimeSinceStartupAsDouble < deadline);
                Directory.CreateDirectory("outputs/combat-config/runtime-review");
                File.WriteAllBytes(Path.Combine("outputs/combat-config/runtime-review", fileName), pixels.EncodeToPNG());
                Assert.That(different, Is.GreaterThan(500), "Actual ECS camera must show rendered combat units.");
            }
            finally
            {
                camera.targetTexture = oldTarget; RenderTexture.active = oldActive; target.Release();
                UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(pixels);
            }
        }

        /// <summary>限定资源等待时长并原样抛出失败，避免加载异常导致测试无限等待。</summary>
        /// <param name="task">启动任务。</param>
        /// <returns>逐帧等待协程。</returns>
        private static IEnumerator Wait(Task task)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 60;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Timed out waiting for real resource startup.");
            task.GetAwaiter().GetResult();
        }

        /// <summary>只替代设备采样，仍走生产运行器 Update 的所有控制逻辑。</summary>
        private sealed class TestInput : ICombatInput
        {
            public CombatInputFrame Frame;
            /// <summary>读取持续移动，并在一次采样后消费按下事件。</summary>
            /// <returns>当前测试输入快照。</returns>
            public CombatInputFrame Read()
            {
                var result = Frame; Frame.PausePressed = Frame.RestartPressed = false; return result;
            }
        }

        /// <summary>共享美术首次读取正常完成，会话再次读取攻击 ANI 时模拟不能立即取消的供应商 IO。</summary>
        private sealed class DeferredResources : IResourceService
        {
            private readonly IResourceService source;
            private readonly TaskCompletionSource<IRawResourceHandle> completion = new TaskCompletionSource<IRawResourceHandle>();
            private readonly HashSet<int> loadedResourceIds = new HashSet<int>();
            public bool Blocked { get; private set; }
            public readonly LateHandle Handle = new LateHandle();
            /// <summary>构造测试资源代理，不转移底层服务所有权。</summary>
            /// <param name="source">真实 YooAsset 服务。</param>
            public DeferredResources(IResourceService source) { this.source = source; }
            /// <summary>转发非原始资源请求，本测试不改变其加载行为。</summary>
            /// <typeparam name="TAsset">资源类型。</typeparam>
            /// <param name="resourceId">表资源 ID。</param>
            /// <param name="cancellationToken">调用者令牌。</param>
            /// <returns>真实服务的加载任务。</returns>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class
                => source.LoadAssetAsync<TAsset>(resourceId, cancellationToken);
            /// <summary>同一资源 ID 首次读取走真实服务，首次重复读取作为会话加载边界挂起。</summary>
            /// <param name="resourceId">表资源 ID。</param>
            /// <param name="cancellationToken">调用者令牌。</param>
            /// <returns>真实加载或受控晚到句柄。</returns>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                if (!loadedResourceIds.Add(resourceId)) { Blocked = true; return completion.Task; }
                return source.LoadRawFileAsync(resourceId, cancellationToken);
            }
            /// <summary>测试关闭入口后放行晚到结果，允许 finally 重复调用。</summary>
            public void Complete() { completion.TrySetResult(Handle); }
        }

        /// <summary>取消后不得被解析的晚到句柄，记录所有权是否已归还。</summary>
        private sealed class LateHandle : IRawResourceHandle
        {
            public byte[] Data => Array.Empty<byte>();
            public bool Disposed { get; private set; }
            /// <summary>加载取消处理归还资源时记录释放状态。</summary>
            public void Dispose() { Disposed = true; }
        }
    }
}
