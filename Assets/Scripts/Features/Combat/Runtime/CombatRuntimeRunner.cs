using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Unity.Entities;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式交互入口：整局拥有 World、会话、表现、相机，读取表驱动输入和状态界面。</summary>
    public sealed class CombatRuntimeRunner : MonoBehaviour
    {
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private PerformanceScenarioConfig scenario;
        private CombatPresentationConfig presentation;
        private CombatVisualResources assets;
        private CombatEntityVisuals visuals;
        private ICombatInput input;
        private World world;
        private bool started, closed, settingsApplied;
        private int oldFrameRate, oldVSync;
        private bool oldBackground;
        private GUIStyle labelStyle, buttonStyle;
        private string status;
        public bool Ready { get; private set; }
        public CombatSession Session { get; private set; }
        public Camera ViewCamera { get; private set; }
        public string StatusText => status;
        public CombatEntityVisuals Visuals => visuals;

        /// <summary>配置和资源服务就绪后异步加载整局资源，成功才开放 Update 和界面。</summary>
        /// <param name="scenario">显式请求的 TbPerformanceScenario Combat 行。</param>
        /// <param name="resources">生命周期长于本入口的统一资源服务。</param>
        /// <param name="input">从表现表构造的设备输入适配器。</param>
        /// <param name="token">组合根退出令牌。</param>
        /// <returns>资源、会话、渲染世界和相机全部就绪的任务。</returns>
        /// <remarks>必须 Unity 主线程调用并保留同步上下文；失败/取消清理全部部分对象，调用方仍负责销毁根 GameObject。</remarks>
        /// <exception cref="InvalidOperationException">重复初始化或配置不合法。</exception>
        /// <exception cref="OperationCanceledException">初始化被取消。</exception>
        public async Task InitializeAsync(PerformanceScenarioConfig scenario, IResourceService resources, ICombatInput input, CancellationToken token)
        {
            if (started || closed) throw new InvalidOperationException("Combat runner cannot be initialized twice.");
            started = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try
            {
                if (scenario == null || scenario.Kind != EPerformanceKind.Combat || scenario.PresentationId_Ref == null || input == null)
                    throw new InvalidOperationException("Combat launch requires an explicit Combat scenario and input adapter.");
                this.scenario = scenario; presentation = scenario.PresentationId_Ref; this.input = input;
                ValidatePresentation();
                assets = await CombatVisualResources.LoadAsync(scenario, resources, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                world = new World("Roguelike Combat");
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world,
                    DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default));
                Session = await CombatSessionFactory.CreateAsync(world, scenario, resources, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                visuals = new CombatEntityVisuals(world, Session, scenario, assets);
                var cameraObject = new GameObject("Combat Camera"); cameraObject.transform.SetParent(transform, false);
                ViewCamera = cameraObject.AddComponent<Camera>();
                ViewCamera.orthographic = true; ViewCamera.clearFlags = CameraClearFlags.SolidColor;
                ViewCamera.backgroundColor = new Color(presentation.BackgroundR, presentation.BackgroundG, presentation.BackgroundB);
                ViewCamera.nearClipPlane = presentation.CameraNear; ViewCamera.farClipPlane = presentation.CameraFar;
                ViewCamera.transform.localPosition = new Vector3(0, 0, -presentation.CameraDepth);
                UpdateCameraFit();
                oldFrameRate = Application.targetFrameRate; oldVSync = QualitySettings.vSyncCount; oldBackground = Application.runInBackground;
                settingsApplied = true;
                Application.targetFrameRate = scenario.TargetFrameRate; QualitySettings.vSyncCount = scenario.VSyncCount;
                Application.runInBackground = scenario.RunInBackground;
                RefreshStatus(); Ready = true;
            }
            catch { ReleaseOwnedObjects(); throw; }
        }

        /// <summary>初始化时检查已有表现参数，缺配不使用硬编码界面或相机兜底。</summary>
        /// <exception cref="InvalidOperationException">尺寸、相机、文本或格式不合法。</exception>
        private void ValidatePresentation()
        {
            CombatMath.Positive(presentation.PanelWidth); CombatMath.Positive(presentation.PanelHeight);
            CombatMath.Positive(presentation.FontSize); CombatMath.Positive(presentation.CameraDepth);
            CombatMath.Positive(presentation.CameraNear); CombatMath.Positive(presentation.CameraFar);
            CombatMath.NonNegative(scenario.CameraPadding);
            if (presentation.CameraNear >= presentation.CameraDepth || presentation.CameraFar <= presentation.CameraDepth ||
                string.IsNullOrWhiteSpace(presentation.Title) || string.IsNullOrWhiteSpace(presentation.StatusFormat) ||
                string.IsNullOrWhiteSpace(presentation.PauseText) || string.IsNullOrWhiteSpace(presentation.RestartText) ||
                string.IsNullOrWhiteSpace(presentation.DeadText) || string.IsNullOrWhiteSpace(presentation.HelpText))
                throw new InvalidOperationException("TbCombatPresentation: incomplete HUD or camera configuration.");
            _ = string.Format(CultureInfo.InvariantCulture, presentation.StatusFormat, 0, 0, 0, 0d);
        }

        /// <summary>Unity 每渲染帧采样一次设备输入，资源未就绪时不运行。</summary>
        /// <remarks>不更改 Unity timeScale；暂停只影响本会话。异常时释放本局，避免每帧重复报错。</remarks>
        private void Update()
        {
            if (!Ready) return;
            try { AdvanceFrame(Time.unscaledDeltaTime, input.Read()); }
            catch (Exception exception) { Close(); Debug.LogException(exception, this); }
        }

        /// <summary>输入采样后处理重开/暂停，再把有效渲染时间交给会话；重开同帧不额外推进。</summary>
        /// <param name="elapsedSeconds">本渲染帧经过时间。</param>
        /// <param name="frame">输入适配器或测试提供的同协议快照。</param>
        /// <remarks>统一供键盘与自动化测试调用；移动技能投递仍由原会话执行，绝不因 UI 再扣血。</remarks>
        public void AdvanceFrame(double elapsedSeconds, CombatInputFrame frame)
        {
            if (!Ready) return;
            if (frame.RestartPressed) { Restart(); return; }
            if (frame.PausePressed) TogglePause();
            Session.Advance(elapsedSeconds, frame.Movement);
            visuals.Synchronize(); RefreshStatus();
        }

        /// <summary>Unity LateUpdate 在本帧模拟/选帧后推进自有 World 的变换与 Presentation，确保同帧绘制。</summary>
        /// <remarks>此 World 不追加到 PlayerLoop，避免自动和手动双更新；暂停时仍绘制冻结画面。</remarks>
        private void LateUpdate()
        {
            if (!Ready) return;
            try { UpdateCameraFit(); world.Update(); }
            catch (Exception exception) { Close(); Debug.LogException(exception, this); }
        }

        /// <summary>按场地半宽/半高、留白和当前画面比例计算固定全场正交视野。</summary>
        /// <remarks>仅调整本入口创建的相机，不接管或改写原场景相机。</remarks>
        private void UpdateCameraFit()
        {
            ViewCamera.orthographicSize = Mathf.Max(scenario.ArenaHalfHeight.Value + scenario.CameraPadding,
                (scenario.ArenaHalfWidth.Value + scenario.CameraPadding) / ViewCamera.aspect);
        }

        /// <summary>按键或按钮调用时切换本局暂停；死亡后仍保持冻结，只能重开。</summary>
        public void TogglePause()
        {
            if (Ready && !Session.Statistics.PlayerDead) Session.Paused = !Session.Paused;
        }

        /// <summary>按键或按钮触发重开，复用现有实体和共享资源，同时更新出生画面及状态。</summary>
        /// <remarks>会话恢复生命/冷却/代际，播放器随代际自动复位，不重新加载美术。</remarks>
        public void Restart()
        {
            if (!Ready) return;
            Session.Restart(); visuals.Synchronize(); RefreshStatus();
        }

        /// <summary>模拟更新后从真实生命与统计生成状态文本，OnGUI 多次重绘复用此结果。</summary>
        private void RefreshStatus()
        {
            var unit = Session.ReadUnit(0); var stats = Session.Statistics;
            status = string.Format(CultureInfo.InvariantCulture, presentation.StatusFormat,
                unit.Target.Health, unit.Target.MaxHealth, stats.AliveMonsters, stats.Tick * Session.StepSeconds);
        }

        /// <summary>Unity GUI 事件绘制表驱动调试界面，尺寸/字号/文本来自 TbCombatPresentation。</summary>
        /// <remarks>布局使用 Unity 内置样式；不持有源场景 UI。按钮与键盘复用同一暂停/重开方法。</remarks>
        private void OnGUI()
        {
            if (!Ready) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = presentation.FontSize, wordWrap = true };
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = presentation.FontSize };
            }
            GUILayout.BeginArea(new Rect(0, 0, presentation.PanelWidth, presentation.PanelHeight), GUI.skin.box);
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
            GUILayout.EndHorizontal(); GUILayout.EndArea();
        }

        /// <summary>应用退出或测试结束时先取消加载，再停止和释放本入口所有对象。</summary>
        /// <remarks>幂等；不释放调用者资源服务，不销毁默认世界，不修改源场景。</remarks>
        public void Close()
        {
            if (closed) return;
            closed = true; lifetime.Cancel(); ReleaseOwnedObjects();
        }

        /// <summary>初始化失败及关闭时按表现、会话、世界、资源顺序回收；世界先释放已登记的 GPU 引用。</summary>
        /// <remarks>可能由加载失败再次调用；每个字段释放后清空，不保留半就绪状态。</remarks>
        private void ReleaseOwnedObjects()
        {
            Ready = false;
            if (ViewCamera != null) { ViewCamera.enabled = false; Destroy(ViewCamera.gameObject); ViewCamera = null; }
            visuals?.Dispose(); visuals = null;
            Session?.Dispose(); Session = null;
            world?.Dispose(); world = null;
            assets?.Dispose(); assets = null;
            if (settingsApplied)
            {
                Application.targetFrameRate = oldFrameRate; QualitySettings.vSyncCount = oldVSync; Application.runInBackground = oldBackground;
                settingsApplied = false;
            }
        }

        /// <summary>Unity 销毁组件时取消未完成加载并关闭已创建战斗，最后释放取消源。</summary>
        /// <remarks>组合根应在销毁 YooAsset 之前主动 Close；本回调是重复安全的生命周期保障。</remarks>
        private void OnDestroy() { Close(); lifetime.Dispose(); }
    }
}
