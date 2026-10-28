using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using Unity.Entities;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>交互战斗入口：测试场景与正式 TbStage 共用 World、会话、表现和相机生命周期。</summary>
    public sealed partial class CombatRuntimeRunner : MonoBehaviour
    {
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private PerformanceScenarioConfig scenario;
        private CombatRunDefinition runDefinition;
        private CombatPresentationConfig presentation;
        private CombatVisualResources assets;
        private CombatEntityVisuals visuals;
        private CombatFeedbackVisuals feedback;
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
        private CombatCollisionDebugVisuals collisionDebug;
#endif
        private CombatTilemapBackground mapBackground;
        private CombatUiRuntime ui;
        private CombatBuildFeedbackUi buildFeedbackUi;
        private ICombatInput input;
        private World world;
        private bool started, closed, settingsApplied;
        private int oldFrameRate, oldVSync;
        private bool oldBackground;
        private GUIStyle labelStyle, buttonStyle;
        private string status;

        public bool Ready { get; private set; }
        public CombatSession Session { get; private set; }
        public CombatRunModel RunModel { get; private set; }
        public CombatRunCoordinator Coordinator { get; private set; }
        public Camera ViewCamera { get; private set; }
        public string StatusText => status;
        public CombatEntityVisuals Visuals => visuals;
        public CombatTilemapBackground MapBackground => mapBackground;
        public GameObject FormalUiRoot => ui?.Root;

        /// <summary>配置和资源服务就绪后加载 TbPerformanceScenario 战斗测试入口。</summary>
        /// <param name="scenario">显式请求的 TbPerformanceScenario Combat 行。</param>
        /// <param name="resources">生命周期长于本入口的统一资源服务。</param>
        /// <param name="input">从表现表构造的设备输入适配器。</param>
        /// <param name="token">组合根退出令牌。</param>
        /// <returns>资源、会话、渲染 World 和相机全部就绪的任务。</returns>
        /// <remarks>保留 OnGUI 调试界面和测试场景重开行为；不创建正式 Prefab UI。</remarks>
        /// <exception cref="InvalidOperationException">重复初始化或配置不合法。</exception>
        /// <exception cref="OperationCanceledException">初始化被取消。</exception>
        public async Task InitializeAsync(PerformanceScenarioConfig scenario, IResourceService resources,
            ICombatInput input, CancellationToken token)
        {
            BeginInitialization();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try
            {
                if (scenario == null || scenario.Kind != EPerformanceKind.Combat ||
                    scenario.PresentationId_Ref == null || input == null)
                    throw new InvalidOperationException("Combat launch requires an explicit Combat scenario and input adapter.");
                this.scenario = scenario;
                presentation = scenario.PresentationId_Ref;
                this.input = input;
                ValidateScenarioPresentation();
                assets = await CombatVisualResources.LoadAsync(scenario, resources, linked.Token);
                CombatMapDefinition mapDefinition = CombatMapDefinition.Create(scenario.MapId_Ref)
                    .FitViewport((double)Screen.width / Screen.height);
                mapBackground = await CombatTilemapBackground.CreateAsync(mapDefinition, scenario.CombatRulesId_Ref,
                    resources, transform, linked.Token);
                CreateWorld();
                Session = await CombatSessionFactory.CreateAsync(world, scenario, resources, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                visuals = new CombatEntityVisuals(world, Session, scenario, assets);
                feedback = await CombatFeedbackVisuals.CreateAsync(world, Session, scenario, resources, linked.Token);
                CreateCamera();
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
                collisionDebug = new CombatCollisionDebugVisuals(Session, scenario.CombatRulesId_Ref, transform);
                collisionDebug.Synchronize();
#endif
                ApplyScenarioSettings();
                RefreshStatus();
                Ready = true;
            }
            catch
            {
                ReleaseOwnedObjects();
                throw;
            }
        }

        /// <summary>配置和资源服务就绪后加载正式 TbStage 单局、协调器和全部 UI Prefab。</summary>
        /// <param name="definition">由 TbStage 聚合并验证的正式单局定义。</param>
        /// <param name="resources">生命周期长于本入口的统一资源服务。</param>
        /// <param name="input">由 TbCombatPresentation 构造的设备输入适配器。</param>
        /// <param name="token">组合根退出令牌。</param>
        /// <param name="profileStore">组合根显式提供的玩家或隔离Profile；为空时仅演示且不访问任何存档。</param>
        /// <returns>战斗、美术、相机及正式 UI 全部就绪的任务。</returns>
        /// <remarks>失败或取消时按 UI、表现、会话、World、共享美术顺序回收，不保存场景。</remarks>
        /// <exception cref="InvalidOperationException">重复初始化、UI缺配或Prefab无效。</exception>
        /// <exception cref="ArgumentNullException">正式定义或输入适配器为空。</exception>
        /// <exception cref="OperationCanceledException">初始化被取消。</exception>
        public async Task InitializeAsync(CombatRunDefinition definition, IResourceService resources,
            ICombatInput input, CancellationToken token, BuildFeedbackProfileStore profileStore = null)
        {
            BeginInitialization();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try
            {
                runDefinition = definition ?? throw new ArgumentNullException(nameof(definition));
                this.input = input ?? throw new ArgumentNullException(nameof(input));
                presentation = definition.Presentation;
                definition.RequireUiReady();
                ValidateCameraPresentation();
                assets = await CombatVisualResources.LoadAsync(definition, resources, linked.Token);
                mapBackground = await CombatTilemapBackground.CreateAsync(
                    definition.MapLayout.FitViewport((double)Screen.width / Screen.height), definition.CombatRules,
                    resources, transform, linked.Token);
                CreateWorld();
                Session = await CombatSessionFactory.CreateAsync(world, definition, resources, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                RunModel = new CombatRunModel(definition, profileStore?.EnabledIds);
                Coordinator = new CombatRunCoordinator(RunModel, Session);
                visuals = new CombatEntityVisuals(world, Session, definition, assets);
                feedback = await CombatFeedbackVisuals.CreateAsync(world, Session, definition, resources, linked.Token);
                CreateCamera();
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
                collisionDebug = new CombatCollisionDebugVisuals(Session, definition.CombatRules, transform);
                collisionDebug.Synchronize();
#endif
                ui = await CombatUiRuntime.CreateAsync(definition, resources, transform, ViewCamera,
                    ChooseUpgrade, Restart, linked.Token, ClaimChest);
                if (definition.DraftPolicy != null)
                    buildFeedbackUi = await CombatBuildFeedbackUi.CreateAsync(Coordinator, profileStore, resources, ViewCamera, linked.Token);
                if (definition.Tribulation != null)
                {
                    tribulationUi = await CombatTribulationUi.CreateAsync(Coordinator, resources, transform, ViewCamera, ReturnToLobby, linked.Token);
                    tribulationMarkers = new CombatTribulationMarkers(Coordinator, transform);
                }
                linked.Token.ThrowIfCancellationRequested();
                RefreshStatus();
                Ready = true;
            }
            catch
            {
                ReleaseOwnedObjects();
                throw;
            }
        }

        /// <summary>拒绝重复初始化并在任何资源创建前锁定本组件生命周期。</summary>
        /// <exception cref="InvalidOperationException">组件已开始初始化或已关闭。</exception>
        private void BeginInitialization()
        {
            if (started || closed) throw new InvalidOperationException("Combat runner cannot be initialized twice.");
            started = true;
        }

        /// <summary>通过战斗系统目录创建本入口独占的 ECS World。</summary>
        /// <remarks>World 不追加到 PlayerLoop，由 LateUpdate 手动推进一次；项目业务系统不会通过 Default 自动发现进入。</remarks>
        private void CreateWorld()
        {
            lifetime.Token.ThrowIfCancellationRequested();
            world = CombatWorldFactory.Create();
        }

        /// <summary>创建只属于本局的正交相机，并从 TbCombatPresentation 应用渲染参数。</summary>
        /// <remarks>不接管、修改或保存原场景相机。</remarks>
        private void CreateCamera()
        {
            CombatRulesConfig rules = scenario != null ? scenario.CombatRulesId_Ref : runDefinition.CombatRules;
            float worldUnitsPerPixel = rules.WorldUnitsPerPixel;
            var cameraObject = new GameObject("Combat Camera");
            cameraObject.transform.SetParent(transform, false);
            ViewCamera = cameraObject.AddComponent<Camera>();
            ViewCamera.orthographic = true;
            ViewCamera.clearFlags = CameraClearFlags.SolidColor;
            ViewCamera.backgroundColor = new Color(presentation.BackgroundR, presentation.BackgroundG, presentation.BackgroundB);
            ViewCamera.nearClipPlane = presentation.CameraNearPixels * worldUnitsPerPixel;
            ViewCamera.farClipPlane = presentation.CameraFarPixels * worldUnitsPerPixel;
            ViewCamera.transform.localPosition = new Vector3(0, 0, -presentation.CameraDepthPixels * worldUnitsPerPixel);
            UpdateCameraFit();
            UpdateCameraFollow();
            InitializeArenaBoundary();
        }

        /// <summary>初始化测试入口时检查 OnGUI、输入帮助与相机参数。</summary>
        /// <exception cref="InvalidOperationException">尺寸、相机、文本或格式不合法。</exception>
        private void ValidateScenarioPresentation()
        {
            ValidateCameraPresentation();
            if (scenario.MapId_Ref == null || scenario.CombatRulesId_Ref == null)
                throw new InvalidOperationException("Combat scenario requires TbMap and TbCombatRules references.");
            _ = CombatMapDefinition.Create(scenario.MapId_Ref);
            CombatMath.Positive(presentation.PanelWidthPixels);
            CombatMath.Positive(presentation.PanelHeightPixels);
            CombatMath.Positive(presentation.FontSize);
            CombatMath.NonNegative(scenario.CameraPadding);
            if (string.IsNullOrWhiteSpace(presentation.Title) || string.IsNullOrWhiteSpace(presentation.StatusFormat) ||
                string.IsNullOrWhiteSpace(presentation.PauseText) || string.IsNullOrWhiteSpace(presentation.RestartText) ||
                string.IsNullOrWhiteSpace(presentation.DeadText) || string.IsNullOrWhiteSpace(presentation.HelpText))
                throw new InvalidOperationException("TbCombatPresentation: incomplete diagnostic HUD configuration.");
            _ = string.Format(CultureInfo.InvariantCulture, presentation.StatusFormat, 0, 0, 0, 0d);
        }

        /// <summary>检查正式与测试入口共用的相机裁剪参数。</summary>
        /// <exception cref="InvalidOperationException">深度或裁剪面非法。</exception>
        private void ValidateCameraPresentation()
        {
            CombatMath.Positive(presentation.CameraDepthPixels);
            CombatMath.Positive(presentation.CameraNearPixels);
            CombatMath.Positive(presentation.CameraFarPixels);
            if (presentation.CameraNearPixels >= presentation.CameraDepthPixels || presentation.CameraFarPixels <= presentation.CameraDepthPixels)
                throw new InvalidOperationException("TbCombatPresentation: invalid camera clipping configuration.");
        }

        /// <summary>仅对 TbPerformanceScenario 应用显式帧率、垂直同步及后台运行测试设置。</summary>
        /// <remarks>正式关卡不改全局质量设置；释放入口时恢复此前值。</remarks>
        private void ApplyScenarioSettings()
        {
            oldFrameRate = Application.targetFrameRate;
            oldVSync = QualitySettings.vSyncCount;
            oldBackground = Application.runInBackground;
            settingsApplied = true;
            Application.targetFrameRate = scenario.TargetFrameRate;
            QualitySettings.vSyncCount = scenario.VSyncCount;
            Application.runInBackground = scenario.RunInBackground;
        }

        /// <summary>Unity 每渲染帧采样一次设备输入，资源未就绪时不运行。</summary>
        /// <remarks>不更改 Unity timeScale；异常时关闭本局，避免每帧重复报错。</remarks>
        private void Update()
        {
            if (!Ready) return;
            try
            {
                RequireLiveFrameState();
                AdvanceFrame(Time.unscaledDeltaTime, input.Read());
            }
            catch (Exception exception)
            {
                Close();
                Debug.LogException(exception, this);
            }
        }

        /// <summary>每帧采样输入前确认非序列化战斗对象仍属于当前运行域。</summary>
        /// <remarks>Unity Editor 若配置为 Play 中重编译并继续，会保留可序列化状态却丢失输入、ECS World 和会话；此处将其转换为一次明确故障并由 Update 统一关闭，避免连续空引用。</remarks>
        /// <exception cref="InvalidOperationException">脚本域重载或异常生命周期导致运行对象不完整。</exception>
        private void RequireLiveFrameState()
        {
            bool formalStateReady = Coordinator == null || RunModel != null && ui != null;
            if (input != null && Session != null && visuals != null && world != null && world.IsCreated &&
                ViewCamera != null && formalStateReady)
                return;
            throw new InvalidOperationException(
                "Combat runtime state was invalidated, usually because scripts recompiled while Play Mode continued. Restart Play Mode.");
        }

        /// <summary>输入采样后处理重开和暂停，再推进测试会话或正式协调器。</summary>
        /// <param name="elapsedSeconds">本渲染帧经过的未缩放时间。</param>
        /// <param name="frame">输入适配器或测试提供的同协议快照。</param>
        /// <remarks>正式升级与结算由协调器冻结；模拟完成后一次采集会话拥有的权威快照，主体与反馈只在本次同步中借用。刷新渲染组件和UI，不重复执行伤害。</remarks>
        public void AdvanceFrame(double elapsedSeconds, CombatInputFrame frame)
        {
            if (!Ready) return;
            if (frame.RestartPressed && Restart()) return;
            if (frame.PausePressed) TogglePause();
            if (Coordinator != null) Coordinator.Advance(elapsedSeconds, frame.Movement);
            else Session.Advance(elapsedSeconds, frame.Movement);
            var presentationUnits = Session.CapturePresentationUnits();
            visuals.Synchronize(presentationUnits);
            feedback?.Synchronize(presentationUnits);
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
            collisionDebug?.Synchronize();
#endif
            RefreshStatus();
        }

        /// <summary>Unity LateUpdate 在模拟和选帧后推进自有 World 的变换与 Presentation。</summary>
        /// <remarks>暂停时仍绘制冻结画面；所有战斗入口同步来自会话实际移动边界的通用红框，由运行器退出回收。World 从不注册到全局 PlayerLoop。</remarks>
        private void LateUpdate()
        {
            if (!Ready) return;
            try
            {
                UpdateCameraFit();
                UpdateCameraFollow();
                arenaBoundary?.Synchronize(ViewCamera);
                world.Update();
            }
            catch (Exception exception)
            {
                Close();
                Debug.LogException(exception, this);
            }
        }

        /// <summary>按 TbMap 逻辑像素高度计算正交视野，并同步行走范围外半个视野的 Ground 覆盖。</summary>
        /// <remarks>视口高度来自 TbMap.cameraViewportHeightPixels，比例来自 TbCombatRules.worldUnitsPerPixel；Camera.aspect 变化仅在地块范围改变时更新 Tilemap，不重新加载资源。</remarks>
        private void UpdateCameraFit()
        {
            MapConfig map = scenario != null ? scenario.MapId_Ref : runDefinition.Map;
            CombatRulesConfig rules = scenario != null ? scenario.CombatRulesId_Ref : runDefinition.CombatRules;
            ViewCamera.orthographicSize = map.CameraViewportHeightPixels * rules.WorldUnitsPerPixel * 0.5f;
            mapBackground.FitViewport(ViewCamera.aspect);
        }

        /// <summary>在每帧模拟结束后将相机中心移动到玩家位置，并限制在程序化地图边界内。</summary>
        /// <remarks>玩家位置来自 CombatSession；地面边界来自 TbMap 与实际视野派生的 MapBackground.Layout，比例来自 TbCombatRules。只移动本局相机，不修改 Scene、Prefab 或 ECS 数据。</remarks>
        private void UpdateCameraFollow()
        {
            CombatRulesConfig rules = scenario != null ? scenario.CombatRulesId_Ref : runDefinition.CombatRules;
            CombatUnit player = Session.ReadUnit(0);
            float mapHalfWidth = mapBackground.Layout.WidthPixels * rules.WorldUnitsPerPixel * 0.5f;
            float mapHalfHeight = mapBackground.Layout.HeightPixels * rules.WorldUnitsPerPixel * 0.5f;
            float viewportHalfWidth = ViewCamera.orthographicSize * ViewCamera.aspect;
            float viewportHalfHeight = ViewCamera.orthographicSize;
            float x = ClampCameraAxis(player.Position.x, mapHalfWidth, viewportHalfWidth);
            float y = ClampCameraAxis(player.Position.y, mapHalfHeight, viewportHalfHeight);
            ViewCamera.transform.localPosition = new Vector3(x, y, -presentation.CameraDepthPixels * rules.WorldUnitsPerPixel);
        }

        /// <summary>将单轴相机中心限制在地图内；当视口大于地图时固定在地图中心。</summary>
        /// <param name="target">玩家在该轴上的世界坐标。</param>
        /// <param name="mapHalfExtent">由 TbMap 像素尺寸换算的地图半尺寸。</param>
        /// <param name="viewportHalfExtent">当前正交相机在该轴上的半视野。</param>
        /// <returns>不会使视口露出地图外的相机中心坐标。</returns>
        private static float ClampCameraAxis(float target, float mapHalfExtent, float viewportHalfExtent)
        {
            float limit = mapHalfExtent - viewportHalfExtent;
            return limit > 0f ? Mathf.Clamp(target, -limit, limit) : 0f;
        }

        /// <summary>按键或测试调用时切换当前入口的主动暂停。</summary>
        public void TogglePause()
        {
            if (!Ready) return;
            if (Coordinator != null) Coordinator.TogglePause();
            else if (!Session.Statistics.PlayerDead) Session.Paused = !Session.Paused;
            RefreshStatus();
        }

        /// <summary>测试入口随时重置会话；正式入口仅在胜负结算时开始下一代单局。</summary>
        /// <returns>本次输入实际完成重开时为真。</returns>
        /// <remarks>复用已加载 ANI、Prefab 和实体池，不重新加载资源；会话重置后重新采集权威快照并刷新主体、反馈和UI，不保留上一代借用。</remarks>
        public bool Restart()
        {
            if (!Ready) return false;
            if (!TryFinalizeProfile()) return false;
            if (Coordinator != null)
            {
                if (!Coordinator.Restart()) return false;
            }
            else
            {
                Session.Restart();
            }
            var presentationUnits = Session.CapturePresentationUnits();
            visuals.Synchronize(presentationUnits);
            feedback?.Synchronize(presentationUnits);
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
            collisionDebug?.Synchronize();
#endif
            RefreshStatus();
            return true;
        }

        /// <summary>升级卡提交当前面板代次与 TbUpgradeOption.id，并立即刷新表现和界面。</summary>
        /// <param name="panelGeneration">卡片创建时记录的模型面板代次。</param>
        /// <param name="optionId">当前候选升级选项 ID。</param>
        /// <returns>协调器接受并完成 ECS 同步时为真。</returns>
        /// <remarks>协调器消费TbUpgradeOption后重新采集本次权威状态，主体与反馈共享读取；刷新运行时组件和UI，不保存配置或资源。</remarks>
        public bool ChooseUpgrade(ulong panelGeneration, int optionId)
        {
            if (!Ready || Coordinator == null || !Coordinator.ChooseUpgrade(panelGeneration, optionId)) return false;
            var presentationUnits = Session.CapturePresentationUnits();
            visuals.Synchronize(presentationUnits);
            feedback?.Synchronize(presentationUnits);
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
            collisionDebug?.Synchronize();
#endif
            RefreshStatus();
            return true;
        }

        /// <summary>宝箱面板领取后立即同步超武、属性和正式 UI，不在表现层生成奖励。</summary>
        /// <param name="panelGeneration">创建本箱按钮时的代次。</param>
        /// <param name="optionId">普通奖励 ID；进化或耗尽箱为零。</param>
        /// <returns>协调器接受领取并完成 ECS 同步时为真。</returns>
        /// <remarks>刷新运行时表现和面板；旧按钮、双击和重开前回调由模型拒绝。</remarks>
        public bool ClaimChest(ulong panelGeneration, int optionId)
        {
            if (!Ready || Coordinator == null || !Coordinator.ClaimChest(panelGeneration, optionId)) return false;
            var presentationUnits = Session.CapturePresentationUnits();
            visuals.Synchronize(presentationUnits);
            feedback?.Synchronize(presentationUnits);
            RefreshStatus();
            return true;
        }

        /// <summary>模拟更新后刷新测试状态文本或正式Prefab UI，以及TbTribulationUi职业/试炼状态和内存预警；不推进模拟。</summary>
        private void RefreshStatus()
        {
            if (Coordinator != null)
            {
                status = RunModel.State.ToString();
                ui?.Refresh(RunModel, Coordinator, Session);
                tribulationUi?.Refresh();
                buildFeedbackUi?.Refresh(Time.unscaledTimeAsDouble);
                tribulationMarkers?.Synchronize();
                return;
            }
            CombatUnit unit = Session.ReadUnit(0);
            CombatCounters stats = Session.Statistics;
            status = string.Format(CultureInfo.InvariantCulture, presentation.StatusFormat,
                unit.Target.Health, unit.Target.MaxHealth, stats.AliveMonsters, stats.Tick * Session.StepSeconds);
        }

        /// <summary>仅为 TbPerformanceScenario 绘制表驱动调试界面。</summary>
        /// <remarks>正式 TbStage 完全使用可编辑 Prefab，OnGUI 不参与正式入口。</remarks>
        private void OnGUI()
        {
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
            if (Ready) collisionDebug?.DrawLegend();
#endif
            if (!Ready || Coordinator != null) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = presentation.FontSize, wordWrap = true };
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = presentation.FontSize };
            }
            GUILayout.BeginArea(new Rect(0, 0, presentation.PanelWidthPixels, presentation.PanelHeightPixels), GUI.skin.box);
            GUILayout.Label(presentation.Title, labelStyle);
            GUILayout.Label(status, labelStyle);
            GUILayout.Label(Session.Statistics.PlayerDead ? presentation.DeadText : presentation.HelpText, labelStyle);
            GUILayout.BeginHorizontal();
            bool enabledBefore = GUI.enabled;
            GUI.enabled = enabledBefore && !Session.Statistics.PlayerDead;
            bool paused = GUILayout.Toggle(Session.Paused, presentation.PauseText, buttonStyle);
            if (paused != Session.Paused) TogglePause();
            GUI.enabled = enabledBefore;
            if (GUILayout.Button(presentation.RestartText, buttonStyle)) Restart();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>应用退出或测试结束时取消加载并释放本入口全部所有权。</summary>
        /// <remarks>幂等；不释放调用者资源服务，不销毁默认 World，不保存源场景。</remarks>
        public void Close()
        {
            if (closed) return;
            closed = true;
            lifetime.Cancel();
            ReleaseOwnedObjects();
        }

        /// <summary>初始化失败及关闭时按 UI、表现、会话、World、共享资源顺序回收。</summary>
        /// <remarks>Unity 可能先销毁 World；仅在对象仍存活时访问，字段释放后立即清空。</remarks>
        private void ReleaseOwnedObjects()
        {
            Ready = false;
            arenaBoundary?.Dispose(); arenaBoundary = null;
            buildFeedbackUi?.Dispose(); buildFeedbackUi = null;
            Coordinator?.TerminateTribulation();
            tribulationMarkers?.Dispose(); tribulationMarkers = null;
            tribulationUi?.Dispose(); tribulationUi = null;
            ui?.Dispose();
            ui = null;
#if ROGUELIKE_COMBAT_COLLISION_DEBUG
            collisionDebug?.Dispose();
            collisionDebug = null;
#endif
            if (ViewCamera != null)
            {
                ViewCamera.enabled = false;
                Destroy(ViewCamera.gameObject);
                ViewCamera = null;
            }
            mapBackground?.Dispose();
            mapBackground = null;
            visuals?.Dispose();
            visuals = null;
            feedback?.Dispose();
            feedback = null;
            Session?.Dispose();
            Session = null;
            Coordinator = null;
            RunModel = null;
            if (world != null && world.IsCreated) world.Dispose();
            world = null;
            assets?.Dispose();
            assets = null;
            if (settingsApplied)
            {
                Application.targetFrameRate = oldFrameRate;
                QualitySettings.vSyncCount = oldVSync;
                Application.runInBackground = oldBackground;
                settingsApplied = false;
            }
        }

        /// <summary>Unity 销毁组件时取消加载、关闭已创建战斗并释放取消源。</summary>
        /// <remarks>组合根应在 YooAsset 之前主动 Close；本回调重复安全。</remarks>
        private void OnDestroy()
        {
            Close();
            lifetime.Dispose();
        }
    }
}
