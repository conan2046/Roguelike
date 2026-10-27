using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Roguelike.Tests
{
    /// <summary>经 MCP 单独启动的编辑器战斗/ANI桥性能探针；不写正式配置，不随普通回归运行。</summary>
    [PrebuildSetup(typeof(CombatRuntimeTests))]
    [PostBuildCleanup(typeof(CombatRuntimeTests))]
    public sealed class CombatPerformanceProbeTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Sample> samples = new List<Sample>(50000);
        private readonly List<Result> results = new List<Result>();
        private string output;
        private PerformanceScenarioConfig limits;
        private CombatRunDefinition definition;
        private Tables tables;
        private IResourceService resources;
        private World world;
        private CombatSession session;
        private CombatVisualResources assets;
        private CombatEntityVisuals visuals;
        private CombatFeedbackVisuals feedback;
        private CombatTilemapBackground map;
        private GameObject root;
        private Camera camera;
        private RenderTexture target;
        private float2 testArena;
        private int initialOutside, initialOverlaps;
        private bool diagnosticOnly;
        private bool compareHealthBarPrototype;
        private bool compareStableHealthBars;
        private OrderedHealthBarBatch activeStableBatch;
        private GameObject stableReferenceRoot;
        private Action restoreProbeSettings;
        private YooAssetPackageInitializer probeInitializer;
        private BarInstancePrototype activeBarPrototype;

        /// <summary>NUnit在成功、断言失败或协程中断后统一释放专项所有对象，并恢复采样前编辑器设置。</summary>
        /// <remarks>也由RunProbe的finally调用；重复释放安全。独立TearDown避免嵌套IEnumerator失败时父协程finally没有执行。</remarks>
        [TearDown]
        public void TearDownProbe()
        {
            try
            {
                activeBarPrototype?.Dispose(); activeBarPrototype = null;
                activeStableBatch?.Dispose(); activeStableBatch = null;
                if (stableReferenceRoot != null) { Object.Destroy(stableReferenceRoot); stableReferenceRoot = null; }
                ReleaseCase(); map?.Dispose(); map = null; assets?.Dispose(); assets = null;
                if (camera != null) camera.targetTexture = null;
                if (target != null) { target.Release(); Object.Destroy(target); target = null; }
                if (root != null) { Object.Destroy(root); root = null; }
                camera = null;
                probeInitializer?.Dispose(); probeInitializer = null;
            }
            finally { restoreProbeSettings?.Invoke(); restoreProbeSettings = null; }
        }

        /// <summary>性能专项按500/1000/2000逐档运行基础武器与通用六超武上界，每档用TbPerformanceScenario预热/采样时长。</summary>
        /// <returns>逐渲染帧让出主线程，MCP心跳和取消始终有机会执行。</returns>
        /// <remarks>只在测试副本改变同屏数量、补怪/恢复策略与构筑；复用正式战斗、ANI、反馈、地图。finally恢复Burst/帧设置并释放所有内存对象。</remarks>
        [UnityTest, Timeout(900000), Category("Stress"), Explicit("500/1000/2000渲染性能专项，仅在授权的独占窗口经MCP运行。")]
        public IEnumerator ThreeTiersMeasureCombatAndSixEvolutionEnvelope() => RunProbe(new[] { 500, 1000, 2000 }, new[] { false, true }, false);

        /// <summary>三档结果发现模拟瓶颈后，用一档较短诊断收集原生六阶段计时，不替代完整容量样本。</summary>
        /// <returns>真实资源与Burst计时的协程。</returns><remarks>仍只改测试会话；退出恢复全局设置并释放资源。</remarks>
        [UnityTest, Timeout(180000), Category("Stress"), Explicit("短时CPU阶段定位，仅在性能专项中单独运行。")]
        public IEnumerator ProfileFiveHundredSimulationPhases() => RunProbe(new[] { 500 }, new[] { false }, true);

        /// <summary>按合法固定阵列捕获2000档两负载的表现细分与实际绘制；只用于定位，不替代完整六组预算。</summary>
        /// <returns>沿用正式采集外壳并恢复编辑器设置的协程。</returns>
        /// <remarks>预热时长沿用TbPerformanceScenario；短采样只属于测试协议。</remarks>
        [UnityTest, Timeout(180000), Category("Stress"), Explicit("表现桥与绘制专项，仅精确选择名称运行。")]
        public IEnumerator ProfileTwoThousandPresentationPhases() => RunProbe(new[] { 2000 }, new[] { false, true }, true);

        /// <summary>正式反馈状态冻结后，对比原血条与复用原资源的实例提交，不接受任何RGBA像素差异。</summary>
        /// <returns>真实资源、隔离World及像素读回的专项协程。</returns>
        /// <remarks>只切换测试World中的血条提交；对象仅驻留内存，所有状态与资源在finally恢复或释放。</remarks>
        [UnityTest, Timeout(180000), Category("Stress"), Explicit("血条批次原型像素门禁，单独精确选择名称运行。")]
        public IEnumerator CompareHealthBarInstancePrototype()
        {
            compareHealthBarPrototype = true;
            try { yield return RunProbe(new[] { 500 }, new[] { false }, true); }
            finally { compareHealthBarPrototype = false; }
        }

        /// <summary>建立独立的逐条槽位顺序参考，再与同矩阵合批结果逐RGBA零容差对照。</summary>
        /// <returns>真实资源、死亡复用和相机移动的专项协程。</returns>
        /// <remarks>保留旧原型失败门禁；新基线仅固定同深度槽序，不改资源、Alpha、尺寸或深度。</remarks>
        [UnityTest, Timeout(240000), Category("Stress"), Explicit("已批准稳定排序的新基线与正式资源合批门禁。")]
        public IEnumerator CompareStableHealthBarBatch()
        {
            compareStableHealthBars = true;
            try { yield return RunProbe(new[] { 2000 }, new[] { false }, true); }
            finally { compareStableHealthBars = false; }
        }

        /// <summary>冻结后按原实体矩阵建立逐条MeshRenderer参考，并比较同序批次；覆盖全部重叠和大容量。</summary>
        /// <returns>逐帧等待剔除与读回。</returns>
        /// <remarks>参考和批次借用相同正式资源；临时对象由独立TearDown清理，JobWorkers保持实际值。</remarks>
        private IEnumerator CompareStableBars()
        {
            var type = typeof(CombatFeedbackVisuals);
            var bars = (System.Collections.IList)type.GetField("hpBars", PrivateInstance).GetValue(feedback);
            var meshes = (Mesh[])type.GetField("meshes", PrivateInstance).GetValue(feedback);
            var material = ((Material[])type.GetField("materials", PrivateInstance).GetValue(feedback))[0];
            activeStableBatch = feedback.HealthBarBatch;
            stableReferenceRoot = new GameObject("Stable bar independent reference") { hideFlags = HideFlags.HideAndDontSave };
            var renderers = new MeshRenderer[bars.Count * 2];
            var frames = new Matrix4x4[bars.Count]; var fills = new Matrix4x4[bars.Count];
            var records = new List<BarPixelResult>();
            for (int slot = 0; slot < bars.Count; slot++)
            for (int layer = 0; layer < 2; layer++)
            {
                var go = new GameObject("Bar reference") { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(stableReferenceRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = meshes[layer];
                var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                renderer.sortingOrder = 1 + slot + layer * bars.Count;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderers[slot + layer * bars.Count] = renderer;
            }
            Vector3 oldPosition = camera.transform.position; float oldSize = camera.orthographicSize;
            for (int variant = 0; variant < 7; variant++)
            {
                if (variant > 0)
                {
                    camera.orthographicSize = 8f;
                    camera.transform.position = new Vector3(variant == 4 ? 1.25f : 0f, variant == 4 ? -.75f : 0f, oldPosition.z);
                    for (int slot = 0; slot < session.UnitCount; slot++)
                    {
                        CombatUnit unit = session.ReadUnit(slot);
                        float x = variant == 1 ? 0 : (slot % 9 - 4) * .15f;
                        float y = variant == 1 ? 0 : (slot / 9 % 7 - 3) * .1f;
                        if (variant == 5) { x += (slot % 2 == 0 ? -1f : 1f) * camera.orthographicSize * camera.aspect; y += camera.orthographicSize; }
                        unit.Position = new float2(x, y); unit.PreviousPosition = unit.Position;
                        unit.Target.Health = variant == 2 && slot % 7 == 0 || variant == 6 ? 0 : Math.Max(1, unit.Target.MaxHealth * (slot % 4 + 1) / 4);
                        if (variant == 3) { unit.Target.Lifetime++; unit.Target.Health = unit.Target.MaxHealth; }
                        world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
                    }
                    var units = session.CapturePresentationUnits(); visuals.Synchronize(units); feedback.Synchronize(units);
                }
                world.Update(); yield return null;
                // 独立参考不用被测合并网格；每条保留原矩阵，通过sortingOrder只固定层内槽序。
                activeStableBatch.Enabled = false;
                for (int slot = 0; slot < bars.Count; slot++)
                {
                    var state = feedback.ReadHealthBar(slot);
                    frames[slot] = state.Frame; fills[slot] = state.Fill;
                    bool alive = session.ReadUnit(slot).Target.Health > 0;
                    SetStableReference(renderers[slot], frames[slot], alive);
                    SetStableReference(renderers[slot + bars.Count], fills[slot], alive);
                }
                using (var query = world.EntityManager.CreateEntityQuery(typeof(Unity.Rendering.MaterialMeshInfo)))
                using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities)
                    if (!world.EntityManager.HasComponent<Unity.Rendering.DisableRendering>(entity)) world.EntityManager.AddComponent<Unity.Rendering.DisableRendering>(entity);
                stableReferenceRoot.SetActive(true);
                yield return null;
                Color32[] expected = ReadBarPixels(Path.Combine(output, "stable-" + variant + "-reference.png"));
                yield return null;
                Color32[] repeated = ReadBarPixels(Path.Combine(output, "stable-" + variant + "-reference-repeat.png"));
                int repeats = expected.Where((pixel, index) => !pixel.Equals(repeated[index])).Count();
                stableReferenceRoot.SetActive(false); activeStableBatch.Enabled = true;
                yield return null;
                Color32[] actual = ReadBarPixels(Path.Combine(output, "stable-" + variant + "-batched.png"));
                int differences = 0, maximum = 0;
                for (int i = 0; i < expected.Length; i++)
                {
                    int delta = Math.Max(Math.Abs(expected[i].r - actual[i].r), Math.Max(Math.Abs(expected[i].g - actual[i].g),
                        Math.Max(Math.Abs(expected[i].b - actual[i].b), Math.Abs(expected[i].a - actual[i].a))));
                    if (delta > 0) differences++; maximum = Math.Max(maximum, delta);
                }
                records.Add(new BarPixelResult { variant = variant, differentPixels = differences, maxChannelDelta = maximum,
                    referenceRepeatDifferentPixels = repeats, instancedBars = activeStableBatch.Count });
                activeStableBatch.Enabled = false;
                // 恢复测试实体渲染，让下一种状态重新得到权威LocalToWorld；不会向正式Scene保存引用。
                using (var query = world.EntityManager.CreateEntityQuery(typeof(Unity.Rendering.DisableRendering)))
                using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities) world.EntityManager.RemoveComponent<Unity.Rendering.DisableRendering>(entity);
            }
            camera.transform.position = oldPosition; camera.orthographicSize = oldSize;
            File.WriteAllText(Path.Combine(output, "stable-bar-pixel-comparison.json"), JsonUtility.ToJson(new BarPixelReport { results = records.ToArray() }, true));
            Assert.That(records.All(item => item.referenceRepeatDifferentPixels == 0), Is.True, "Independent slot ordered reference must repeat exactly with normal workers.");
            Assert.That(records.All(item => item.differentPixels == 0), Is.True, "Every RGBA channel must match independent stable reference with zero tolerance.");
            Assert.That(records[1].instancedBars, Is.GreaterThan(1023), "Dense overlap must exercise the full capacity, not only nonintersecting bars.");
            Assert.That(records[6].instancedBars, Is.Zero, "All dead bars must stop rendering.");
        }

        /// <summary>给独立参考设置原始矩阵的平移与缩放，并按权威生命切换可见性。</summary>
        /// <param name="renderer">逐条参考Renderer。</param><param name="matrix">原实体LocalToWorld。</param><param name="visible">当前是否存活。</param>
        /// <remarks>血条原始矩阵无旋转；不会改变源资源或运行时配置。</remarks>
        private static void SetStableReference(MeshRenderer renderer, Matrix4x4 matrix, bool visible)
        {
            renderer.enabled = visible;
            renderer.transform.position = matrix.GetColumn(3);
            renderer.transform.localScale = new Vector3(matrix.m00, matrix.m11, matrix.m22);
        }

        /// <summary>专项共享的资源、设置与恢复外壳；所有参数都属于测试协议。</summary>
        /// <param name="counts">本次采集的目标数量。</param><param name="loads">基础或六超武上界。</param><param name="diagnostic">仅做短时阶段定位。</param>
        /// <returns>逐帧采集协程。</returns><remarks>负责设置恢复与所有临时对象清理，不写任何正式数据。</remarks>
        /// <exception cref="InvalidOperationException">当前环境缺少Burst编译选项。</exception>
        private IEnumerator RunProbe(int[] counts, bool[] loads, bool diagnostic)
        {
            results.Clear();
            diagnosticOnly = diagnostic;
            Type burstType = Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst");
            if (burstType == null) throw new InvalidOperationException("The performance probe requires the installed Burst assembly.");
            var burstOptions = burstType.GetField("Options")?.GetValue(null) ?? burstType.GetProperty("Options")?.GetValue(null);
            if (burstOptions == null) throw new InvalidOperationException("The performance probe requires Burst compiler options.");
            PropertyInfo burstEnabled = burstOptions.GetType().GetProperty("EnableBurstCompilation");
            PropertyInfo burstSynchronous = burstOptions.GetType().GetProperty("EnableBurstCompileSynchronously");
            object oldBurst = burstEnabled.GetValue(burstOptions), oldSynchronous = burstSynchronous.GetValue(burstOptions);
            int oldVsync = QualitySettings.vSyncCount, oldRate = Application.targetFrameRate;
            int oldJobWorkers = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
            bool oldBackground = Application.runInBackground;
            bool oldProfiler = UnityEngine.Profiling.Profiler.enabled;
            bool oldCpuArea = UnityEngine.Profiling.Profiler.GetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            restoreProbeSettings = () =>
            {
                burstEnabled.SetValue(burstOptions, oldBurst); burstSynchronous.SetValue(burstOptions, oldSynchronous);
                QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldRate; Application.runInBackground = oldBackground;
                UnityEngine.Profiling.Profiler.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, oldCpuArea);
                UnityEngine.Profiling.Profiler.enabled = oldProfiler;
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = oldJobWorkers;
            };
            probeInitializer = new YooAssetPackageInitializer();
            var initializer = probeInitializer;
            try
            {
                output = Path.GetFullPath(Path.Combine(Application.dataPath, "../.local/performance-20261025", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "../latest.json"), JsonUtility.ToJson(new Progress { output = output, phase = "initializing" }, true));
                burstEnabled.SetValue(burstOptions, true); burstSynchronous.SetValue(burstOptions, true);
                if (diagnosticOnly)
                {
                    UnityEngine.Profiling.Profiler.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true);
                    UnityEngine.Profiling.Profiler.enabled = true;
                }
                yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
                var config = new LubanConfigService(initializer);
                yield return Wait(config.InitializeAsync(CancellationToken.None));
                tables = config.Tables; limits = tables.TbPerformanceScenario.Get(5);
                definition = CombatRunDefinition.Create(tables, 1);
                // 通用六槽是正式数据已有的上界；这不是当前磐岳可自然持有六武器的声明。
                typeof(CombatRunDefinition).GetProperty("BuildConfig").SetValue(definition, tables.TbRunBuild.Get(1));
                resources = new YooAssetResourceService(initializer, config);
                QualitySettings.vSyncCount = limits.VSyncCount; Application.targetFrameRate = limits.TargetFrameRate;
                Application.runInBackground = limits.RunInBackground;
                root = new GameObject("MCP Combat Performance Probe");
                camera = new GameObject("MCP Performance Camera").AddComponent<Camera>();
                camera.transform.SetParent(root.transform, false); camera.orthographic = true;
                camera.transform.position = new Vector3(0, 0, -definition.Presentation.CameraDepthPixels * definition.CombatRules.WorldUnitsPerPixel);
                target = new RenderTexture(limits.ScreenWidth, limits.ScreenHeight, 24);
                target.Create(); camera.targetTexture = target; camera.aspect = (float)target.width / target.height;
                var assetTask = CombatVisualResources.LoadAsync(definition, resources, CancellationToken.None);
                yield return Wait(assetTask); assets = assetTask.GetAwaiter().GetResult();
                var mapTask = CombatTilemapBackground.CreateAsync(definition.MapLayout, definition.CombatRules, resources, root.transform, CancellationToken.None);
                yield return Wait(mapTask); map = mapTask.GetAwaiter().GetResult();
                foreach (int count in counts)
                foreach (bool evolved in loads)
                {
                    WriteProgress("case-begin", count, evolved);
                    yield return MeasureCase(count, evolved);
                    if (compareStableHealthBars) yield return CompareStableBars();
                    else if (compareHealthBarPrototype) yield return CompareBarPrototype();
                    else if (diagnosticOnly) yield return CaptureFrameEvents(count, evolved);
                    WriteProgress("case-returned", count, evolved);
                    ReleaseCase();
                    yield return null;
                }
                File.WriteAllText(Path.Combine(output, "summary.json"), JsonUtility.ToJson(new Report { results = results.ToArray(), diagnosticOnly = diagnosticOnly }, true));
                WriteProgress("complete", 0, false);
                Assert.That(results.Count, Is.EqualTo(counts.Length * loads.Length));
                Assert.That(results.All(item => item.usedBurst && item.minAlive == item.monsters && item.frameCount > 0), Is.True,
                    "The probe must retain its specified live workload and actually execute Burst. Budget failures are recorded individually, not hidden by this transport assertion.");
            }
            finally
            {
                TearDownProbe();
            }
        }

        /// <summary>创建一档真实原生战斗与渲染，预热后按墙钟采样；不使用同步长循环阻断编辑器。</summary>
        /// <param name="count">测试协议指定的存活怪物数量。</param><param name="evolved">是否使用源表通用六槽满星/满被动/全进化上界。</param>
        /// <returns>初始化、预热与采样协程。</returns>
        /// <remarks>关闭单局经验/箱/升级模型调度，仅测战斗、ANI选帧、反馈和Entities渲染；补怪与玩家恢复只存在于测试会话。</remarks>
        private IEnumerator MeasureCase(int count, bool evolved)
        {
            samples.Clear(); WriteProgress("loading", count, evolved);
            world = CombatWorldFactory.Create();
            var task = CombatSessionFactory.CreateAsync(world, definition, resources, CancellationToken.None);
            yield return Wait(task); session = task.GetAwaiter().GetResult(); WriteProgress("session-ready", count, evolved);
            var settings = (CombatSessionSettings)typeof(CombatSession).GetField("settings", PrivateInstance).GetValue(session);
            typeof(CombatSessionSettings).GetProperty("ReplenishOnDeath").SetValue(settings, true);
            typeof(CombatSessionSettings).GetProperty("RestorePlayerAfterDamage").SetValue(settings, true);
            var model = CreateBuild(evolved);
            CombatAttributeSnapshot attributes = model.AttributeSnapshot;
            var upgrades = model.Build.CaptureWeapons();
            session.SynchronizeRunPlayer(session.ReadUnit(0).Target.Lifetime, attributes, attributes.MaxHealth);
            session.SynchronizePlayerSkills(upgrades.Select(item => item.SkillId), upgrades);
            session.ApplyWeaponUpgrades(upgrades, attributes);
            SpawnGrid(count);
            ValidateGeometry();
            WriteProgress("grid-ready", count, evolved);
            visuals = new CombatEntityVisuals(world, session, definition, assets);
            var feedbackTask = CombatFeedbackVisuals.CreateAsync(world, session, definition, resources, CancellationToken.None);
            yield return Wait(feedbackTask); feedback = feedbackTask.GetAwaiter().GetResult();
            // 先执行一次真实tick，确保Burst及其技术Marker已经注册，随后再创建阶段Recorder。
            session.Advance(session.StepSeconds, float2.zero);
            var initialUnits = session.CapturePresentationUnits();
            visuals.Synchronize(initialUnits); feedback.Synchronize(initialUnits); world.Update();
            yield return null;
            using var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            using var main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
            using var draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            using var movement = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Combat.MoveAndIndex");
            using var attacks = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Combat.CollectAttacks");
            using var projectile = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Combat.SweepProjectiles");
            using var areas = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Combat.PulseAreas");
            using var damage = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Combat.ResolveDamage");
            using var recycle = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Combat.RecycleTransforms");
#if UNITY_EDITOR
            var graphics = world.GetExistingSystemManaged<Unity.Rendering.EntitiesGraphicsSystem>();
#endif
            using var unitRead = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Presentation.UnitRead", 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var aniSample = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Presentation.AniSample", 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var renderWrite = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Presentation.RenderWrite", 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var healthBars = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Presentation.HealthBars", 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var damageNumbers = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Presentation.DamageNumbers", 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var skills = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Roguelike.Presentation.Skills", 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
            double start = Time.realtimeSinceStartupAsDouble, previous = start;
            double sampleStart = 0, debtAtStart = 0;
            ulong ticksAtStart = 0;
            bool announced = false;
            int visibleAtStart = CountInView();
            WriteProgress("warmup", count, evolved);
            double sampleSeconds = diagnosticOnly ? Math.Min(limits.WarmupSeconds, limits.SampleSeconds) : limits.SampleSeconds;
            while (Time.realtimeSinceStartupAsDouble - start < limits.WarmupSeconds + sampleSeconds)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                bool recording = now - start >= limits.WarmupSeconds;
                if (recording && !announced)
                {
                    announced = true; sampleStart = now; ticksAtStart = session.Statistics.Tick; debtAtStart = session.BacklogSeconds;
                    WriteProgress("sampling", count, evolved);
                }
                ulong tickBefore = session.Statistics.Tick;
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long watch = Stopwatch.GetTimestamp();
                session.Advance(now - previous, float2.zero);
                double simulationMs = MillisecondsSince(watch);
                watch = Stopwatch.GetTimestamp();
                var presentationUnits = session.CapturePresentationUnits();
                visuals.Synchronize(presentationUnits); feedback.Synchronize(presentationUnits);
                double bridgeMs = MillisecondsSince(watch);
                watch = Stopwatch.GetTimestamp(); world.Update();
                double worldMs = MillisecondsSince(watch);
                long localGc = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                previous = now;
                yield return null;
                if (recording)
                    samples.Add(new Sample { frameMs = (Time.realtimeSinceStartupAsDouble - now) * 1000, simulationMs = simulationMs,
                        bridgeMs = bridgeMs, worldMs = worldMs, localGc = localGc, globalGc = gc.Valid ? gc.LastValue : -1,
                        mainMs = main.Valid ? main.LastValue / 1000000d : -1, drawCalls = draws.Valid ? draws.LastValue : -1,
                        batches = batches.Valid ? batches.LastValue : -1, triangles = triangles.Valid ? triangles.LastValue : -1,
                        unitReadMs = unitRead.Valid ? unitRead.LastValue / 1000000d : -1,
                        aniSampleMs = aniSample.Valid ? aniSample.LastValue / 1000000d : -1,
                        renderWriteMs = renderWrite.Valid ? renderWrite.LastValue / 1000000d : -1,
                        healthBarsMs = healthBars.Valid ? healthBars.LastValue / 1000000d : -1,
                        damageNumbersMs = damageNumbers.Valid ? damageNumbers.LastValue / 1000000d : -1,
                        skillsMs = skills.Valid ? skills.LastValue / 1000000d : -1,
#if UNITY_EDITOR
                        graphicsCommands = graphics.Stats.DrawCommandCount, graphicsInstances = graphics.Stats.RenderedInstanceCount,
#else
                        graphicsCommands = -1, graphicsInstances = -1,
#endif
                        ticks = (long)(session.Statistics.Tick - tickBefore), alive = session.Statistics.AliveMonsters,
                        movementMs = movement.Valid ? movement.LastValue / 1000000d : -1, attackMs = attacks.Valid ? attacks.LastValue / 1000000d : -1,
                        projectileMs = projectile.Valid ? projectile.LastValue / 1000000d : -1, areaMs = areas.Valid ? areas.LastValue / 1000000d : -1,
                        damageMs = damage.Valid ? damage.LastValue / 1000000d : -1, recycleMs = recycle.Valid ? recycle.LastValue / 1000000d : -1,
                        projectiles = session.Statistics.ActiveProjectiles, backlog = session.BacklogSeconds });
            }
            var stats = session.Statistics;
            var result = new Result { monsters = count, load = evolved ? "six-evolution-envelope" : "initial-weapon", width = target.width, height = target.height,
                frameCount = samples.Count, warmupSeconds = limits.WarmupSeconds, measuredSeconds = Time.realtimeSinceStartupAsDouble - sampleStart,
                averageFps = samples.Count / (Time.realtimeSinceStartupAsDouble - sampleStart), frameP95Ms = Percentile(samples.Select(item => item.frameMs), .95),
                frameP99Ms = Percentile(samples.Select(item => item.frameMs), .99), mainP95Ms = Percentile(samples.Select(item => item.mainMs), .95),
                simulationP95Ms = Percentile(samples.Select(item => item.simulationMs), .95), bridgeP95Ms = Percentile(samples.Select(item => item.bridgeMs), .95),
                worldP95Ms = Percentile(samples.Select(item => item.worldMs), .95), maxLocalGcBytes = samples.Max(item => item.localGc),
                maxEditorGcBytes = samples.Max(item => item.globalGc), medianDrawCalls = Percentile(samples.Select(item => (double)item.drawCalls), .5),
                maxDrawCalls = samples.Max(item => item.drawCalls), medianBatches = Percentile(samples.Select(item => (double)item.batches), .5),
                minAlive = samples.Min(item => item.alive), visibleAtStart = visibleAtStart, visibleAtEnd = CountInView(), usedBurst = stats.UsedBurst,
                sampleTicks = (long)(stats.Tick - ticksAtStart), debtAtStart = debtAtStart, debtAtEnd = session.BacklogSeconds,
                totalTicks = (long)stats.Tick,
                attacks = stats.Attacks, damageEvents = stats.DamageEvents, deaths = stats.Deaths, replenished = stats.Replenished,
                movementCandidates = stats.MovementCandidates, movementSweeps = stats.MovementSweeps, movementContacts = stats.MovementContacts,
                movementRepeatedContacts = stats.MovementRepeatedContacts, movementZeroProgress = stats.MovementZeroProgress, movementLimitHits = stats.MovementLimitHits,
                movementConverged = stats.MovementConverged,
                peakProjectiles = stats.PeakProjectiles, weaponSkills = upgrades.Select(item => item.SkillId).ToArray(),
                gcCounterValid = gc.Valid, mainCounterValid = main.Valid, drawCounterValid = draws.Valid,
                cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName,
                memoryMb = SystemInfo.systemMemorySize, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                arenaHalfWidth = testArena.x, arenaHalfHeight = testArena.y, orthographicSize = camera.orthographicSize,
                initialOutsideArena = initialOutside, initialOverlaps = initialOverlaps,
                diagnosticOnly = diagnosticOnly, phaseCountersValid = movement.Valid && attacks.Valid && projectile.Valid && areas.Valid && damage.Valid && recycle.Valid,
                movementP95Ms = Percentile(samples.Select(item => item.movementMs), .95), attackP95Ms = Percentile(samples.Select(item => item.attackMs), .95),
                projectileP95Ms = Percentile(samples.Select(item => item.projectileMs), .95), areaP95Ms = Percentile(samples.Select(item => item.areaMs), .95),
                damageP95Ms = Percentile(samples.Select(item => item.damageMs), .95), recycleP95Ms = Percentile(samples.Select(item => item.recycleMs), .95),
                unitReadP95Ms = Percentile(samples.Select(item => item.unitReadMs), .95),
                aniSampleP95Ms = Percentile(samples.Select(item => item.aniSampleMs), .95),
                renderWriteP95Ms = Percentile(samples.Select(item => item.renderWriteMs), .95),
                healthBarsP95Ms = Percentile(samples.Select(item => item.healthBarsMs), .95),
                damageNumbersP95Ms = Percentile(samples.Select(item => item.damageNumbersMs), .95),
                skillsP95Ms = Percentile(samples.Select(item => item.skillsMs), .95),
                presentationCountersValid = unitRead.Valid && aniSample.Valid && renderWrite.Valid && healthBars.Valid && damageNumbers.Valid && skills.Valid,
                medianGraphicsCommands = Percentile(samples.Select(item => (double)item.graphicsCommands), .5),
                medianGraphicsInstances = Percentile(samples.Select(item => (double)item.graphicsInstances), .5),
                targetFps = limits.TargetAverageFps, frameBudgetMs = limits.MaxP95FrameTimeMs, gcBudgetBytes = limits.MaxGcAllocBytesPerFrame };
            result.budgetPassed = !diagnosticOnly && result.averageFps >= result.targetFps && result.frameP95Ms <= result.frameBudgetMs &&
                result.maxLocalGcBytes <= result.gcBudgetBytes && result.debtAtEnd <= result.debtAtStart + session.StepSeconds && result.usedBurst && result.frameCount >= 20;
            string name = count + "-" + result.load;
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonUtility.ToJson(result, true));
            WriteSamples(Path.Combine(output, name + ".csv"));
            Capture(Path.Combine(output, name + ".png"));
            results.Add(result); WriteProgress("case-complete", count, evolved);
        }

        /// <summary>在测试模型中复用正式成长消费者准备构筑，避免另外复制被动/星级的业务算法。</summary>
        /// <param name="evolved">是否准备通用六超武上界。</param><returns>持有源表派生属性与武器快照的模型。</returns>
        /// <remarks>反射只用于测试夹具到达上界，不作为自然进化证据，不修改正式表或正式入口。</remarks>
        private CombatRunModel CreateBuild(bool evolved)
        {
            var model = new CombatRunModel(definition);
            if (!evolved) return model;
            MethodInfo apply = typeof(CombatRunModel).GetMethod("ApplyUpgrade", PrivateInstance);
            foreach (UpgradeOptionConfig option in definition.UpgradeOptions)
            {
                if (option.Type == EUpgradeOptionType.RunWeapon)
                    while (model.Build.CanUpgrade(option.RunWeaponId_Ref)) apply.Invoke(model, new object[] { option, model.GetOptionRank(option) + 1 });
                if (option.Type == EUpgradeOptionType.RunPassive)
                    while (model.Build.CanUpgrade(option.RunPassiveId_Ref)) apply.Invoke(model, new object[] { option, model.GetOptionRank(option) + 1 });
            }
            MethodInfo evolve = typeof(CombatBuildState).GetMethod("TryEvolve", PrivateInstance);
            foreach (WeaponEvolutionConfig evolution in model.Build.EligibleEvolutions()) evolve.Invoke(model.Build, new object[] { evolution.Id });
            return model;
        }

        /// <summary>以相同冻结状态切换参考与实例路径，覆盖重叠、缺血、死亡复用、边缘与相机位置。</summary>
        /// <returns>逐帧等待World注册及渲染后读取RGBA像素。</returns>
        /// <remarks>只修改隔离测试单位；不推进模拟或保存资产。任何像素差异使门禁失败，原路径自动恢复。</remarks>
        private IEnumerator CompareBarPrototype()
        {
            var type = typeof(CombatFeedbackVisuals);
            var bars = (System.Collections.IList)type.GetField("hpBars", PrivateInstance).GetValue(feedback);
            var meshes = (Mesh[])type.GetField("meshes", PrivateInstance).GetValue(feedback);
            var material = ((Material[])type.GetField("materials", PrivateInstance).GetValue(feedback))[0];
            var frame = new Entity[bars.Count]; var fill = new Entity[bars.Count];
            feedback.HealthBarBatch.Enabled = false;
            for (int i = 0; i < bars.Count; i++)
            {
                frame[i] = CreateLegacyBarEntity(meshes[0], material);
                fill[i] = CreateLegacyBarEntity(meshes[1], material);
            }
            SynchronizeLegacyBars(frame, fill);
            var records = new List<BarPixelResult>();
            var barEntities = new HashSet<Entity>(frame.Concat(fill));
            var diagnosticHidden = new HashSet<Entity>();
            using var prototype = new BarInstancePrototype(camera, meshes[0], meshes[1], material, frame.Length);
            activeBarPrototype = prototype;
            Vector3 oldPosition = camera.transform.position;
            float oldSize = camera.orthographicSize;
            int oldWorkerCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
            try
            {
                // 像素门禁串行化剔除任务，固定原路径同深度实例的发出顺序；容量测试仍使用原Worker设置。
                world.EntityManager.CompleteAllTrackedJobs();
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = 0;
                for (int variant = 0; variant < 6; variant++)
                {
                    if (variant > 0)
                    {
                        camera.orthographicSize = 8f;
                        camera.transform.position = new Vector3(variant == 4 ? 1.25f : 0f, variant == 4 ? -.75f : 0f, oldPosition.z);
                        for (int slot = 0; slot < session.UnitCount; slot++)
                        {
                            CombatUnit unit = session.ReadUnit(slot);
                            float x = variant == 1 ? 0f : (slot % 9 - 4) * .15f;
                            float y = variant == 1 ? 0f : (slot / 9 % 7 - 3) * .1f;
                            if (variant == 5) { x += (slot % 2 == 0 ? -1f : 1f) * camera.orthographicSize * camera.aspect; y += camera.orthographicSize; }
                            unit.Position = new float2(x, y); unit.PreviousPosition = unit.Position;
                            unit.Target.Health = variant == 2 && slot % 7 == 0 ? 0 : Math.Max(1, unit.Target.MaxHealth * (slot % 4 + 1) / 4);
                            if (variant == 3) { unit.Target.Lifetime++; unit.Target.Health = unit.Target.MaxHealth; }
                            world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
                        }
                        var units = session.CapturePresentationUnits(); visuals.Synchronize(units); feedback.Synchronize(units);
                    }
                    SynchronizeLegacyBars(frame, fill);
                    // 同深度飘字在原路径本身会重排；该门禁只隔离血条提交，其他渲染另由普通Play与完整画面回归覆盖。
                    using (var query = world.EntityManager.CreateEntityQuery(typeof(Unity.Rendering.MaterialMeshInfo)))
                    using (var entities = query.ToEntityArray(Allocator.Temp))
                    foreach (Entity entity in entities)
                    {
                        if (barEntities.Contains(entity) || world.EntityManager.HasComponent<Unity.Rendering.DisableRendering>(entity)) continue;
                        diagnosticHidden.Add(entity);
                        world.EntityManager.AddComponent<Unity.Rendering.DisableRendering>(entity);
                    }
                    SetBarReference(frame, fill, true); prototype.Enabled = false;
                    world.Update(); yield return null; world.Update(); yield return null;
                    Color32[] expected = ReadBarPixels(Path.Combine(output, "bars-" + variant + "-reference.png"));
                    world.Update(); yield return null;
                    Color32[] repeated = ReadBarPixels(Path.Combine(output, "bars-" + variant + "-reference-repeat.png"));
                    int repeatDifferences = expected.Where((pixel, index) => !pixel.Equals(repeated[index])).Count();
                    var frameTransforms = new LocalTransform[frame.Length];
                    var fillTransforms = new LocalTransform[fill.Length];
                    var framesWorld = new Matrix4x4[frame.Length];
                    var fillsWorld = new Matrix4x4[fill.Length];
                    var eligible = new bool[frame.Length];
                    var active = new bool[frame.Length];
                    var rectangles = new Rect[frame.Length];
                    for (int slot = 0; slot < frame.Length; slot++)
                    {
                        framesWorld[slot] = world.EntityManager.GetComponentData<LocalToWorld>(frame[slot]).Value;
                        fillsWorld[slot] = world.EntityManager.GetComponentData<LocalToWorld>(fill[slot]).Value;
                        frameTransforms[slot] = world.EntityManager.GetComponentData<LocalTransform>(frame[slot]);
                        fillTransforms[slot] = world.EntityManager.GetComponentData<LocalTransform>(fill[slot]);
                        Matrix4x4 matrix = framesWorld[slot];
                        rectangles[slot] = new Rect(matrix.m03 - matrix.m00 * .5f, matrix.m13 - matrix.m11 * .5f, matrix.m00, matrix.m11);
                        Matrix4x4 fillMatrix = fillsWorld[slot];
                        Rect frameRect = rectangles[slot];
                        rectangles[slot] = Rect.MinMaxRect(Math.Min(frameRect.xMin, fillMatrix.m03 - fillMatrix.m00 * .5f),
                            Math.Min(frameRect.yMin, fillMatrix.m13 - fillMatrix.m11 * .5f),
                            Math.Max(frameRect.xMax, fillMatrix.m03 + fillMatrix.m00 * .5f),
                            Math.Max(frameRect.yMax, fillMatrix.m13 + fillMatrix.m11 * .5f));
                        active[slot] = eligible[slot] = session.ReadUnit(slot).Target.Health > 0;
                    }
                    for (int first = 0; first < eligible.Length; first++)
                    for (int second = first + 1; second < eligible.Length; second++)
                    {
                        if (!active[first] || !active[second]) continue;
                        Rect a = rectangles[first], b = rectangles[second];
                        if (a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin)
                        { eligible[first] = false; eligible[second] = false; }
                    }
                    prototype.Count = 0;
                    var geometry = new StringBuilder("slot,active,eligible,frameX,frameY,frameZ,frameW,frameH,fillX,fillY,fillZ,fillW,fillH\n");
                    for (int slot = 0; slot < eligible.Length; slot++)
                    {
                        Matrix4x4 f = framesWorld[slot], v = fillsWorld[slot];
                        geometry.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R}\n", slot, active[slot], eligible[slot], f.m03, f.m13, f.m23, f.m00, f.m11, v.m03, v.m13, v.m23, v.m00, v.m11);
                        if (!eligible[slot]) continue;
                        prototype.Frames[prototype.Count] = framesWorld[slot];
                        prototype.Fills[prototype.Count++] = fillsWorld[slot];
                        // 只隐藏相互不重叠的参考几何；保留实体chunk及全部重叠条的原始透明提交顺序。
                        world.EntityManager.SetComponentData(frame[slot], LocalTransform.FromPosition(new float3(1000000f, 1000000f, 0)));
                        world.EntityManager.SetComponentData(fill[slot], LocalTransform.FromPosition(new float3(1000000f, 1000000f, 0)));
                    }
                    File.WriteAllText(Path.Combine(output, "bars-" + variant + "-geometry.csv"), geometry.ToString());
                    prototype.Enabled = true;
                    world.Update(); yield return null; world.Update(); yield return null;
                    Color32[] actual = ReadBarPixels(Path.Combine(output, "bars-" + variant + "-instanced.png"));
                    for (int slot = 0; slot < eligible.Length; slot++)
                    {
                        world.EntityManager.SetComponentData(frame[slot], frameTransforms[slot]);
                        world.EntityManager.SetComponentData(fill[slot], fillTransforms[slot]);
                    }
                    int differences = 0, maxChannel = 0;
                    for (int i = 0; i < expected.Length; i++)
                    {
                        int delta = Math.Max(Math.Abs(expected[i].r - actual[i].r), Math.Max(Math.Abs(expected[i].g - actual[i].g),
                            Math.Max(Math.Abs(expected[i].b - actual[i].b), Math.Abs(expected[i].a - actual[i].a))));
                        if (delta != 0) differences++;
                        maxChannel = Math.Max(maxChannel, delta);
                    }
                    records.Add(new BarPixelResult { variant = variant, differentPixels = differences, maxChannelDelta = maxChannel, referenceRepeatDifferentPixels = repeatDifferences, instancedBars = prototype.Count, legacyBars = active.Count(value => value) - prototype.Count });
                    prototype.Enabled = false;
                }
                File.WriteAllText(Path.Combine(output, "bar-pixel-comparison.json"), JsonUtility.ToJson(new BarPixelReport { results = records.ToArray() }, true));
                Assert.That(records.All(item => item.referenceRepeatDifferentPixels == 0), Is.True, "The frozen reference must be stable before attributing pixel changes to batching.");
                Assert.That(records.All(item => item.differentPixels == 0), Is.True, "Every RGBA channel must match the unchanged reference path.");
                Assert.That(records[0].instancedBars, Is.GreaterThan(0), "The distributed case must actually exercise batching.");
                Assert.That(records[1].legacyBars, Is.GreaterThan(0), "Intersecting bars must retain the original submission.");
            }
            finally
            {
                prototype.Enabled = false; SetBarReference(frame, fill, true);
                foreach (Entity entity in diagnosticHidden)
                    if (world.EntityManager.Exists(entity) && world.EntityManager.HasComponent<Unity.Rendering.DisableRendering>(entity))
                        world.EntityManager.RemoveComponent<Unity.Rendering.DisableRendering>(entity);
                camera.transform.position = oldPosition; camera.orthographicSize = oldSize;
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = oldWorkerCount;
            }
        }

        /// <summary>旧门禁在隔离World重建原EG血条实体，以继续保留旧未定义平局路径的拒绝证据。</summary>
        /// <param name="mesh">原底框或填充网格。</param><param name="material">原共享反馈材质。</param>
        /// <returns>由测试World销毁的原路径实体。</returns><remarks>不接入正式运行，不修改旧像素断言。</remarks>
        private Entity CreateLegacyBarEntity(Mesh mesh, Material material)
        {
            var manager = world.EntityManager;
            var entity = manager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld), typeof(PostTransformMatrix), typeof(BarReferenceOnlyTag));
            var array = new Unity.Rendering.RenderMeshArray(new[] { material }, new[] { mesh });
            Unity.Rendering.RenderMeshUtility.AddComponents(entity, manager,
                new Unity.Rendering.RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows: false), array,
                Unity.Rendering.MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            return entity;
        }

        /// <summary>每次冻结状态后把正式矩阵复制给测试拥有的旧EG参考；隐藏条保持原移出视野语义。</summary>
        /// <param name="frames">测试底框实体。</param><param name="fills">测试填充实体。</param><remarks>不修改正式批次或源配置。</remarks>
        private void SynchronizeLegacyBars(Entity[] frames, Entity[] fills)
        {
            for (int slot = 0; slot < frames.Length; slot++)
            {
                var state = feedback.ReadHealthBar(slot);
                for (int layer = 0; layer < 2; layer++)
                {
                    Entity entity = layer == 0 ? frames[slot] : fills[slot];
                    Matrix4x4 matrix = layer == 0 ? state.Frame : state.Fill;
                    var position = state.Visible ? (Vector3)matrix.GetColumn(3) : new Vector3(1000000f, 1000000f, 0f);
                    world.EntityManager.SetComponentData(entity, LocalTransform.FromPosition(position));
                    world.EntityManager.SetComponentData(entity, new PostTransformMatrix { Value = float4x4.Scale(matrix.m00, matrix.m11, matrix.m22) });
                }
            }
        }

        /// <summary>像素门禁切换隔离World的参考血条，不影响主体或数字。</summary>
        /// <param name="frames">底框实体。</param><param name="fills">填充实体。</param><param name="visible">是否绘制参考路径。</param>
        /// <remarks>只增删测试World中的DisableRendering，finally恢复原路径。</remarks>
        private void SetBarReference(Entity[] frames, Entity[] fills, bool visible)
        {
            foreach (Entity entity in frames.Concat(fills))
            {
                bool disabled = world.EntityManager.HasComponent<Unity.Rendering.DisableRendering>(entity);
                if (visible && disabled) world.EntityManager.RemoveComponent<Unity.Rendering.DisableRendering>(entity);
                else if (!visible && !disabled) world.EntityManager.AddComponent<Unity.Rendering.DisableRendering>(entity);
            }
        }

        /// <summary>像素门禁在同一目标读回RGBA，不创建或保存Unity资产。</summary>
        /// <param name="path">当前专项PNG证据路径。</param><returns>完整RGBA像素。</returns>
        /// <remarks>恢复RenderTexture.active并立即销毁临时读回纹理。</remarks>
        private Color32[] ReadBarPixels(string path)
        {
            var old = RenderTexture.active; var pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                File.WriteAllBytes(path, pixels.EncodeToPNG()); return pixels.GetPixels32();
            }
            finally { RenderTexture.active = old; Object.Destroy(pixels); }
        }

        /// <summary>只供原型门禁的原Mesh/Material实例提交；不创建任何图像或网格副本。</summary>
        private sealed class BarInstancePrototype : IDisposable
        {
            private readonly Camera camera;
            private readonly Mesh frame, fill;
            private readonly Material material;
            public readonly Matrix4x4[] Frames, Fills;
            public bool Enabled;
            public int Count;
            /// <summary>保存正式共享资源并注册相机绘制回调；原资源所有权仍属于反馈绑定。</summary>
            /// <param name="camera">本专项相机。</param><param name="frame">原底框网格。</param><param name="fill">原填充网格。</param>
            /// <param name="material">原反馈材质。</param><param name="count">实际血条槽数，本原型小于API单批上限。</param>
            /// <remarks>Dispose必须解除SRP事件订阅，不销毁共享资源。</remarks>
            public BarInstancePrototype(Camera camera, Mesh frame, Mesh fill, Material material, int count)
            {
                this.camera = camera; this.frame = frame; this.fill = fill; this.material = material;
                Frames = new Matrix4x4[count]; Fills = new Matrix4x4[count];
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += Draw;
            }
            /// <summary>SRP在目标相机剔除前以原矩阵提交两个有序实例批次；关闭时不提交。</summary>
            /// <param name="context">当前SRP上下文。</param><param name="current">本次绘制相机。</param>
            /// <remarks>不改Shader、资源或相机设置；矩阵来自参考实体实际LocalToWorld。</remarks>
            private void Draw(UnityEngine.Rendering.ScriptableRenderContext context, Camera current)
            {
                if (!Enabled || current != camera || Count == 0) return;
                Graphics.DrawMeshInstanced(frame, 0, material, Frames, Count, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0, current, UnityEngine.Rendering.LightProbeUsage.Off);
                Graphics.DrawMeshInstanced(fill, 0, material, Fills, Count, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0, current, UnityEngine.Rendering.LightProbeUsage.Off);
            }
            /// <summary>原型完成或异常时解除相机事件，不保存或销毁共享正式资源。</summary>
            public void Dispose() { UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= Draw; }
        }

        private struct BarReferenceOnlyTag : IComponentData { }
        [Serializable] private sealed class BarPixelResult { public int variant, differentPixels, maxChannelDelta, referenceRepeatDifferentPixels, instancedBars, legacyBars; }
        [Serializable] private sealed class BarPixelReport { public BarPixelResult[] results; }

        /// <summary>短诊断采样结束后通过编辑器实际Frame Debugger收集绘制类型及代表事件，不污染容量计时。</summary>
        /// <param name="count">本组怪物数量。</param><param name="evolved">是否六超武负载。</param>
        /// <returns>让出两帧等待原生调试器收集事件。</returns>
        /// <remarks>只读正式绘制；finally关闭调试器，不改透明排序、材质或管线。反射适配当前Editor内部API。</remarks>
        private IEnumerator CaptureFrameEvents(int count, bool evolved)
        {
#if UNITY_EDITOR
            Type utility = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
            Type eventDataType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");
            var report = new FrameReport();
            if (utility == null || eventDataType == null)
                report.error = "Frame Debugger reflection API unavailable";
            else
            {
                MethodInfo enable = utility.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);
                UnityEditor.EditorWindow frameWindow = null;
                bool ownsWindow = false;
                try
                {
                    Type windowType = typeof(UnityEditor.Editor).Assembly.GetTypes().FirstOrDefault(type => type.Name == "FrameDebuggerWindow");
                    if (windowType != null)
                    {
                        ownsWindow = !Resources.FindObjectsOfTypeAll<UnityEditor.EditorWindow>().Any(window => window.GetType() == windowType);
                        frameWindow = UnityEditor.EditorWindow.GetWindow(windowType);
                        frameWindow.Show();
                    }
                    enable.Invoke(null, new object[] { true, 0 });
                    yield return null;
                    camera.Render();
                    yield return null;
                    Array events = (Array)utility.GetMethod("GetFrameEvents").Invoke(null, null);
                    report.eventCount = events.Length;
                    if (events.Length == 0) report.error = "This Editor capture returned no Frame Debugger events; use measured Entities Graphics command/instance counters, not generic break-cause strings, as draw evidence.";
                    report.kinds = events.Cast<object>().GroupBy(item => item.GetType().GetField("m_Type").GetValue(item).ToString())
                        .Select(group => new FrameKind { kind = group.Key, count = group.Count() }).ToArray();
                    var details = new List<FrameDetail>();
                    int[] indices = Enumerable.Range(0, events.Length).Where(index =>
                        events.GetValue(index).GetType().GetField("m_Type").GetValue(events.GetValue(index)).ToString().Contains("Batch"))
                        .Where((index, ordinal) => ordinal < 8 || ordinal % Math.Max(1, events.Length / 12) == 0).Take(24).ToArray();
                    foreach (int index in indices)
                    {
                        object data = Activator.CreateInstance(eventDataType);
                        bool available = (bool)utility.GetMethod("GetFrameEventData").Invoke(null, new[] { (object)index, data });
                        details.Add(new FrameDetail { index = index, available = available,
                            instances = (int)eventDataType.GetField("m_InstanceCount").GetValue(data),
                            drawCalls = (int)eventDataType.GetField("m_DrawCallCount").GetValue(data),
                            shader = (string)eventDataType.GetField("m_RealShaderName").GetValue(data),
                            pass = (string)eventDataType.GetField("m_PassName").GetValue(data),
                            breakCause = (int)eventDataType.GetField("m_BatchBreakCause").GetValue(data) });
                    }
                    report.details = details.ToArray();
                    report.breakCauses = (string[])utility.GetMethod("GetBatchBreakCauseStrings").Invoke(null, null);
                    using var sorted = world.EntityManager.CreateEntityQuery(typeof(Unity.Rendering.DepthSorted_Tag));
                    report.depthSortedEntities = sorted.CalculateEntityCount();
                }
                finally
                {
                    enable.Invoke(null, new object[] { false, 0 });
                    if (ownsWindow && frameWindow != null) frameWindow.Close();
                }
            }
            File.WriteAllText(Path.Combine(output, count + (evolved ? "-six" : "-base") + "-frame-debugger.json"), JsonUtility.ToJson(report, true));
#else
            yield break;
#endif
        }

        /// <summary>按正式怪物八种循环填满目标阵列，并把出生模板同步到同一格，确保死亡补怪维持负载。</summary>
        /// <param name="count">目标怪物总量。</param>
        /// <remarks>仅测试夹具调整空间分布；单位属性、ANI、碰撞与技能全部来自TbStage闭包。视野扩大以容纳所有目标，非正式玩法相机。</remarks>
        private void SpawnGrid(int count)
        {
            MonsterConfig[] monsters = definition.Monsters.Where(item => !item.IsElite).ToArray();
            int columns = (int)Math.Ceiling(Math.Sqrt(count * camera.aspect)), rows = (int)Math.Ceiling((count + columns) / (double)columns);
            var shapes = monsters.Select(item => CombatCylinder.FromConfig(item.MoveRadiusPixels, item.MoveHeightPixels,
                item.MoveOffsetXPixels, item.MoveOffsetYPixels, item.MoveElevationPixels, definition.CombatRules.WorldUnitsPerPixel).AsVerticalCapsule2D()).ToArray();
            // 旧压测间距可能小于现行手调碰撞体。阵列需先合法，不能把出生重叠制造的退化当成普通容量结果。
            float spacingX = limits.HorizontalSpacing + shapes.Max(item => item.Offset.x + item.Radius) - shapes.Min(item => item.Offset.x - item.Radius);
            float spacingY = limits.VerticalSpacing + shapes.Max(item => item.Offset.y + item.Radius + item.HalfSegment2D) - shapes.Min(item => item.Offset.y - item.Radius - item.HalfSegment2D);
            float halfWidth = columns * spacingX * .5f, halfHeight = rows * spacingY * .5f;
            float padding = limits.CameraPaddingPixels * definition.CombatRules.WorldUnitsPerPixel;
            int maximumColumns = (int)Math.Ceiling(Math.Sqrt(2000 * camera.aspect)), maximumRows = (int)Math.Ceiling((2000 + maximumColumns) / (double)maximumColumns);
            testArena = math.max(new float2(definition.Map.ArenaHalfWidthPixels, definition.Map.ArenaHalfHeightPixels) * definition.CombatRules.WorldUnitsPerPixel,
                new float2(maximumColumns * spacingX, maximumRows * spacingY) * .5f + padding);
            typeof(CombatSessionSettings).GetProperty("Arena").SetValue(typeof(CombatSession).GetField("settings", PrivateInstance).GetValue(session), testArena);
            camera.orthographicSize = math.max(testArena.y, testArena.x / camera.aspect) + padding;
            float spawnRadius = math.length(new float2(halfWidth, halfHeight)) / definition.CombatRules.WorldUnitsPerPixel + definition.Map.SpawnRadiusPixels;
            var templates = (NativeList<CombatUnit>)typeof(CombatSession).GetField("templates", PrivateInstance).GetValue(session);
            CombatUnit player = session.ReadUnit(0);
            int cell = 0;
            for (int index = 0; index < count;)
            {
                float2 position = new float2((cell % columns - (columns - 1) * .5f) * spacingX, (cell / columns - (rows - 1) * .5f) * spacingY);
                cell++;
                Assert.That(cell, Is.LessThanOrEqualTo(columns * rows), "Reserved player space must fit the bounded test grid.");
                CombatCylinder shape = shapes[index % shapes.Length];
                if (CombatGeometry.WithinCapsuleReach(position + shape.Offset - player.Position - player.Movement.Offset,
                    shape.HalfSegment2D + player.Movement.HalfSegment2D, shape.Radius + player.Movement.Radius)) continue;
                Assert.That(session.TrySpawnMonster(monsters[index % monsters.Length], spawnRadius, (ulong)index + 1, false, out int slot), Is.True);
                var unit = session.ReadUnit(slot);
                unit.Position = position;
                unit.PreviousPosition = unit.SpawnPosition = unit.Position;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
                world.EntityManager.SetComponentData(session.UnitEntity(slot), LocalTransform.FromPosition(new float3(unit.Position, 0)));
                templates[slot] = unit;
                index++;
            }
        }

        /// <summary>热身前检查测试阵列无初始重叠且完全位于测试场地内，拒绝用非法夹具得出容量结论。</summary>
        /// <remarks>二次配对仅在采样前执行；不计入任何性能帧。</remarks>
        private void ValidateGeometry()
        {
            initialOutside = initialOverlaps = 0;
            var units = new CombatUnit[session.UnitCount];
            for (int index = 0; index < units.Length; index++) units[index] = session.ReadUnit(index);
            for (int index = 0; index < units.Length; index++)
            {
                CombatUnit unit = units[index];
                float2 center = unit.Position + unit.Movement.Offset;
                float2 extents = new float2(unit.Movement.Radius, unit.Movement.Radius + unit.Movement.HalfSegment2D);
                if (math.any(math.abs(center) + extents > testArena)) initialOutside++;
                for (int otherIndex = 0; otherIndex < index; otherIndex++)
                {
                    CombatUnit other = units[otherIndex];
                    if (!unit.Movement.HeightOverlaps(other.Movement)) continue;
                    float radius = unit.Movement.Radius + other.Movement.Radius;
                    if (CombatGeometry.VerticalSegmentDistanceSquared(center - other.Position - other.Movement.Offset,
                        unit.Movement.HalfSegment2D + other.Movement.HalfSegment2D) < radius * radius) initialOverlaps++;
                }
            }
            Assert.That(initialOutside, Is.Zero, "Benchmark units must start inside their own testing arena.");
            Assert.That(initialOverlaps, Is.Zero, "Benchmark units must start without overlaps.");
        }

        /// <summary>采样外统计相机视野内的活怪中心，避免把离屏数量包装为同屏数量。</summary>
        /// <returns>当前视野内存活怪物数。</returns>
        private int CountInView()
        {
            int count = 0;
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot); if (unit.Target.Health <= 0) continue;
                Vector3 point = camera.WorldToViewportPoint(new Vector3(unit.Position.x, unit.Position.y, 0));
                if (point.z > 0 && point.x >= 0 && point.x <= 1 && point.y >= 0 && point.y <= 1) count++;
            }
            return count;
        }

        /// <summary>每档退出、异常或测试取消时，按表现→会话→World逆序释放；重复调用安全。</summary>
        /// <remarks>共享ANI/地图/相机由专项外层finally释放，不销毁其他项目或测试的资源。</remarks>
        private void ReleaseCase()
        {
            feedback?.Dispose(); feedback = null; visuals?.Dispose(); visuals = null;
            session?.Dispose(); session = null;
            if (world != null && world.IsCreated) world.Dispose(); world = null;
        }

        /// <summary>保存分阶段进度供外部只读监视，采样帧内不写磁盘。</summary>
        /// <param name="phase">阶段名。</param><param name="count">目标数量。</param><param name="evolved">负载档。</param>
        /// <remarks>只写项目.local证据，绝不修改Assets。</remarks>
        private void WriteProgress(string phase, int count, bool evolved)
        {
            var progress = new Progress { output = output, phase = phase, monsters = count, evolved = evolved };
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonUtility.ToJson(progress, true));
            File.AppendAllText(Path.Combine(output, "progress.jsonl"), JsonUtility.ToJson(progress) + "\n");
        }

        /// <summary>把完整帧样本保存为CSV，保留Editor全局GC与测试自身分配两个口径。</summary>
        /// <param name="path">本次专项证据路径。</param><remarks>仅采样完成后分配字符串并写盘，避免污染采样。</remarks>
        private void WriteSamples(string path)
        {
            var text = new StringBuilder("frameMs,simulationMs,bridgeMs,worldMs,localGc,editorGc,mainMs,drawCalls,batches,triangles,ticks,alive,projectiles,backlog,movementMs,attackMs,projectileMs,areaMs,damageMs,recycleMs,unitReadMs,aniSampleMs,renderWriteMs,healthBarsMs,damageNumbersMs,skillsMs,graphicsCommands,graphicsInstances\n");
            foreach (Sample s in samples) text.AppendFormat(CultureInfo.InvariantCulture, "{0:F6},{1:F6},{2:F6},{3:F6},{4},{5},{6:F6},{7},{8},{9},{10},{11},{12},{13:F6},{14:F6},{15:F6},{16:F6},{17:F6},{18:F6},{19:F6},{20:F6},{21:F6},{22:F6},{23:F6},{24:F6},{25:F6},{26},{27}\n",
                s.frameMs,s.simulationMs,s.bridgeMs,s.worldMs,s.localGc,s.globalGc,s.mainMs,s.drawCalls,s.batches,s.triangles,s.ticks,s.alive,s.projectiles,s.backlog,
                s.movementMs,s.attackMs,s.projectileMs,s.areaMs,s.damageMs,s.recycleMs,s.unitReadMs,s.aniSampleMs,s.renderWriteMs,s.healthBarsMs,s.damageNumbersMs,s.skillsMs,s.graphicsCommands,s.graphicsInstances);
            File.WriteAllText(path, text.ToString());
        }

        /// <summary>采样结束后读取已在内存中的正式相机渲染目标作为证据。</summary>
        /// <param name="path">.local PNG输出位置。</param><remarks>临时读回纹理仅驻留内存，finally销毁，不创建预览资产。</remarks>
        private void Capture(string path)
        {
            RenderTexture old = RenderTexture.active;
            var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try { camera.Render(); RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply(); File.WriteAllBytes(path, pixels.EncodeToPNG()); }
            finally { RenderTexture.active = old; Object.Destroy(pixels); }
        }

        /// <summary>返回单调时钟经过的毫秒，测量任务同步等待也计入本段。</summary>
        /// <param name="start">Stopwatch起点。</param><returns>经过毫秒。</returns>
        private static double MillisecondsSince(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

        /// <summary>采样结束后计算最近秩百分位，不在热路径排序。</summary>
        /// <param name="values">单项样本。</param><param name="fraction">目标分位。</param><returns>分位值；没有样本则为负值以明确缺测。</returns>
        private static double Percentile(IEnumerable<double> values, double fraction)
        {
            double[] sorted = values.OrderBy(value => value).ToArray();
            return sorted.Length == 0 ? -1 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];
        }

        /// <summary>有界等待真实资源任务完成，失败原样传播。</summary>
        /// <param name="task">被等待任务。</param><returns>逐帧等待协程。</returns><exception cref="TimeoutException">资源等待超时。</exception>
        private static IEnumerator Wait(Task task)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 120;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            if (!task.IsCompleted) throw new TimeoutException("Performance resource load exceeded its bounded wait.");
            task.GetAwaiter().GetResult();
        }

        private struct Sample
        {
            public double frameMs, simulationMs, bridgeMs, worldMs, mainMs, backlog;
            public double unitReadMs, aniSampleMs, renderWriteMs, healthBarsMs, damageNumbersMs, skillsMs;
            public double movementMs, attackMs, projectileMs, areaMs, damageMs, recycleMs;
            public long localGc, globalGc, drawCalls, batches, triangles, ticks;
            public int alive, projectiles, graphicsCommands, graphicsInstances;
        }
        [Serializable] private sealed class FrameKind { public string kind; public int count; }
        [Serializable] private sealed class FrameDetail { public int index, instances, drawCalls, breakCause; public bool available; public string shader, pass; }
        [Serializable] private sealed class FrameReport { public int eventCount, depthSortedEntities; public string error; public FrameKind[] kinds; public FrameDetail[] details; public string[] breakCauses; }
        [Serializable] private sealed class Progress { public string output, phase; public int monsters; public bool evolved; }
        [Serializable] private sealed class Report { public Result[] results; public bool diagnosticOnly; }
        [Serializable] private sealed class Result
        {
            public string load, cpu, gpu, graphicsApi;
            public int monsters, width, height, frameCount, minAlive, visibleAtStart, visibleAtEnd, memoryMb;
            public int initialOutsideArena, initialOverlaps;
            public bool usedBurst, gcCounterValid, mainCounterValid, drawCounterValid, budgetPassed;
            public bool diagnosticOnly, phaseCountersValid, presentationCountersValid;
            public double unitReadP95Ms, aniSampleP95Ms, renderWriteP95Ms, healthBarsP95Ms, damageNumbersP95Ms, skillsP95Ms;
            public double warmupSeconds, measuredSeconds, averageFps, frameP95Ms, frameP99Ms, mainP95Ms, simulationP95Ms, bridgeP95Ms, worldP95Ms;
            public double medianDrawCalls, medianBatches, debtAtStart, debtAtEnd, targetFps, frameBudgetMs;
            public double medianGraphicsCommands, medianGraphicsInstances;
            public double arenaHalfWidth, arenaHalfHeight, orthographicSize;
            public double movementP95Ms, attackP95Ms, projectileP95Ms, areaP95Ms, damageP95Ms, recycleP95Ms;
            public long maxLocalGcBytes, maxEditorGcBytes, maxDrawCalls, sampleTicks, attacks, damageEvents, deaths, replenished, peakProjectiles, gcBudgetBytes;
            public long movementCandidates, movementSweeps, movementContacts, movementRepeatedContacts, movementZeroProgress, movementLimitHits;
            public long movementConverged;
            public long totalTicks;
            public int[] weaponSkills;
        }
    }
}
