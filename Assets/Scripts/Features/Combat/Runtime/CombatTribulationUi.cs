using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式切片HUD及炼器面板所有者；只刷新源表文本、交互和可见性。</summary>
    public sealed class CombatTribulationUi : IDisposable
    {
        private readonly CombatRunCoordinator coordinator;
        private readonly IResourceHandle<GameObject> handle;
        private readonly GameObject root;
        private readonly TribulationHudView view;
        private readonly Action returnToLobby;
        private bool disposed;
        private ulong displayedRun, displayedReward;
        public GameObject Root => root;

        /// <summary>加载完成后接管本界面并注册按钮；后续每帧Refresh读取最新权威状态。</summary>
        /// <param name="coordinator">当前单局协调器。</param><param name="handle">正式Prefab资源所有权。</param>
        /// <param name="root">当前实例。</param><param name="returnToLobby">入口所有者提供的返回回调。</param>
        /// <remarks>注册实例监听；Dispose先销毁实例再释放句柄。</remarks>
        /// <exception cref="InvalidOperationException">正式HUD绑定缺失或候选数量与Luban不同。</exception>
        private CombatTribulationUi(CombatRunCoordinator coordinator, IResourceHandle<GameObject> handle, GameObject root, Action returnToLobby)
        {
            this.coordinator = coordinator; this.handle = handle; this.root = root; this.returnToLobby = returnToLobby;
            view = root.GetComponent<TribulationHudView>() ?? throw new InvalidOperationException("Tribulation HUD binding is missing.");
            var slice = coordinator.Tribulation.Config;
            if (view.RefinementButtons.Length != slice.RefinementIds.Count || view.RefinementNames.Length != slice.RefinementIds.Count ||
                view.RefinementDescriptions.Length != slice.RefinementIds.Count || view.RefinementButtons.Any(b => b == null))
                throw new InvalidOperationException("Tribulation HUD choice bindings do not match Luban candidates.");
            view.ActivateButton.onClick.AddListener(Activate);
            view.ReturnButton.onClick.AddListener(Return);
            for (int i = 0; i < slice.RefinementIds.Count; i++)
            {
                int index = i;
                view.RefinementButtons[i].onClick.AddListener(() => Choose(index));
                view.RefinementNames[i].text = slice.RefinementIds_Ref[i].Name;
                view.RefinementDescriptions[i].text = slice.RefinementIds_Ref[i].Description;
            }
            view.ReturnLabel.text = slice.UiId_Ref.ReturnText; view.ActivateLabel.text = slice.UiId_Ref.ActivateText;
            view.RewardTitleText.text = slice.UiId_Ref.RewardTitle; view.RewardIntroText.text = slice.UiId_Ref.RewardIntro;
            Refresh();
        }

        /// <summary>正式UI就绪后加载切片附加HUD；部分失败或取消释放全部已获得对象与句柄。</summary>
        /// <param name="coordinator">已初始化切片协调器。</param><param name="resources">统一资源服务。</param>
        /// <param name="parent">运行器所有者节点。</param><param name="camera">当前战斗相机。</param><param name="returnToLobby">入口回调。</param><param name="token">退出令牌。</param>
        /// <returns>由运行器释放的HUD所有者。</returns>
        /// <remarks>实例化仅使用TbTribulationUi.hudPrefabResourceId，不调整序列化布局。</remarks>
        /// <exception cref="InvalidOperationException">正式Prefab或绑定缺失。</exception><exception cref="OperationCanceledException">退出取消。</exception>
        public static async Task<CombatTribulationUi> CreateAsync(CombatRunCoordinator coordinator, IResourceService resources,
            Transform parent, Camera camera, Action returnToLobby, CancellationToken token)
        {
            IResourceHandle<GameObject> handle = null; GameObject root = null;
            try
            {
                handle = await resources.LoadAssetAsync<GameObject>(coordinator.Tribulation.Config.UiId_Ref.HudPrefabResourceId, token);
                token.ThrowIfCancellationRequested(); root = UnityEngine.Object.Instantiate(handle.Asset, parent, false);
                var canvas = root.GetComponent<Canvas>() ?? throw new InvalidOperationException("Tribulation HUD lacks Canvas.");
                canvas.worldCamera = camera;
                return new CombatTribulationUi(coordinator, handle, root, returnToLobby);
            }
            catch { if (root != null) UnityEngine.Object.Destroy(root); handle?.Dispose(); throw; }
        }

        /// <summary>读取正式模型刷新试炼、角色机制和炼器选择；升级或宝箱面板先完成，炼器奖励随后显示。</summary>
        /// <remarks>不推进时间、不改变配置或Transform；面板每次记录单局及奖励代次，旧回调会被协调器拒绝。</remarks>
public void Refresh()
        {
            if (disposed) return;
            var state = coordinator.Tribulation; var model = coordinator.Model; var config = state.Config; var ui = config.UiId_Ref;
            var player = coordinator.Session.ReadUnit(0); var signature = config.SignatureId_Ref;
            double time = coordinator.Session.Statistics.Tick * coordinator.Session.StepSeconds;
            view.TitleText.text = ui.Title + " · " + config.RoleName;
            view.MechanismText.text = signature.Kind == cfg.ETribulationSignature.Guard
                ? player.Target.Guard.IsActive(time) ? Format(ui.GuardActiveFormat, Math.Max(0, player.Target.Guard.UntilSeconds - time))
                    : Format(ui.GuardFormat, player.Target.Guard.Charge, signature.GuardChargeHits)
                : Format(ui.DomainFormat, coordinator.Domains.Domains.Count, signature.MaxDomains);
            view.BossText.text = state.BossPhase > 0 ? Format(ui.BossFormat, state.BossPhase) : string.Empty;
            view.RefinementText.text = state.CurrentRefinementId.HasValue
                ? Format(ui.SelectedFormat, config.RefinementIds_Ref.Single(r => r.Id == state.CurrentRefinementId.Value).Name) : string.Empty;
            bool active = model.State == CombatRunState.Playing || model.State == CombatRunState.Boss;
            bool reward = active && !coordinator.Paused && state.State == TribulationTrialState.RewardPending;
            view.RewardRoot.SetActive(reward);
            displayedRun = model.RunGeneration; displayedReward = state.RewardGeneration;
            for (int i = 0; i < view.RefinementButtons.Length; i++)
                view.RefinementButtons[i].interactable = reward && config.RefinementIds[i] != state.CurrentRefinementId;
            view.ReturnButton.interactable = returnToLobby != null;
            var rule = config.RuleId_Ref; float scale = model.Definition.CombatRules.WorldUnitsPerPixel;
            double x = player.Position.x / scale, y = player.Position.y / scale;
            var altar = state.NearestAvailableAltar(x, y);
            double distance = altar == null ? double.PositiveInfinity : Math.Sqrt(Math.Pow(x - altar.XPixels, 2) + Math.Pow(y - altar.YPixels, 2));
            bool near = altar != null && distance <= rule.ActivationRadiusPixels;
            view.ActivateButton.gameObject.SetActive(active && !coordinator.Paused && near && (state.State == TribulationTrialState.Idle || state.State == TribulationTrialState.Failed));
            string guidance;
            if (altar != null) guidance = near ? Format(ui.NearAltarFormat, altar.Id) : Format(ui.FarAltarFormat, altar.Id, distance * scale);
            else
            {
                var next = rule.Altars.Where(a => !state.SpentAltars.Contains(a.Id)).OrderBy(a => a.AvailableAfterMilli).FirstOrDefault();
                guidance = next == null ? ui.AllSpentText : Format(ui.WaitingFormat, Math.Max(0, next.AvailableAfterMilli - state.ElapsedMilli) / 1000d);
            }
            if (model.Result.HasValue) view.AltarText.text = string.Empty;
            else if (state.State == TribulationTrialState.Active) view.AltarText.text = Format(ui.TrialFormat, state.TrialRemainingMilli / 1000d, state.EnemyHits, rule.RequiredEnemyHits);
            else if (state.State == TribulationTrialState.RewardPending) view.AltarText.text = ui.SuccessText;
            else view.AltarText.text = (state.State == TribulationTrialState.Failed ? ui.FailureText + "\n" : string.Empty) + guidance;
        }

        /// <summary>点击时重新查找真实最近雷坛，由协调器再次校验位置和代次。</summary>
        /// <remarks>通过TbTribulationRule.altars消耗一次机会并更新本界面；不直接推进时间或修改生命。</remarks>
        private void Activate()
        {
            if (disposed) return;
            var player = coordinator.Session.ReadUnit(0); float scale = coordinator.Model.Definition.CombatRules.WorldUnitsPerPixel;
            var altar = coordinator.Tribulation.NearestAvailableAltar(player.Position.x / scale, player.Position.y / scale);
            if (altar != null) coordinator.TryStartTribulation(displayedRun, altar.Id);
            Refresh();
        }

        /// <summary>转交已展示候选和面板代次，领取结果后刷新可见性。</summary>
        /// <remarks>成功时协调器消费TbTribulationRefinement及Rule.successHealPermille，同步正式技能和整数生命；本界面不创建战斗对象。</remarks><param name="index">Prefab按钮对应的候选索引。</param>
        private void Choose(int index)
        {
            if (disposed) return;
            coordinator.ChooseRefinement(displayedRun, displayedReward, coordinator.Tribulation.Config.RefinementIds[index]); Refresh();
        }

        /// <summary>把返回请求交给拥有整个运行器生命周期的入口；不自行创建第二个World。</summary>
        /// <remarks>回调可关闭本UI、World及资源，调用后不再访问实例状态。</remarks>
        private void Return() { if (!disposed) returnToLobby?.Invoke(); }

        /// <summary>统一按不变文化格式化源表UI格式串，便于数据错误定位。</summary>
        /// <param name="format">TbTribulationUi格式串。</param><param name="values">当前权威状态。</param><returns>显示文本。</returns>
        /// <exception cref="FormatException">源表格式串与消费状态不匹配。</exception>
        private static string Format(string format, params object[] values) => string.Format(CultureInfo.InvariantCulture, format, values);

        /// <summary>运行器关闭时解除按钮并释放实例及资源；重复调用安全。</summary>
        /// <remarks>Unity销毁延后到帧尾，先禁用界面防止旧按钮继续响应。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (root != null) { root.SetActive(false); view.ActivateButton.onClick.RemoveAllListeners(); view.ReturnButton.onClick.RemoveAllListeners();
                foreach (var button in view.RefinementButtons) button.onClick.RemoveAllListeners(); UnityEngine.Object.Destroy(root); }
            handle.Dispose();
        }
    }
}
