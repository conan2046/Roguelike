using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>本局反馈UI与结算提交所有者，按配置节流聚合显示，不在伤害热区刷新文本。</summary>
    public sealed class CombatBuildFeedbackUi : IDisposable
    {
        private readonly CombatRunCoordinator coordinator;
        private readonly BuildFeedbackProfileStore profile;
        private readonly IResourceHandle<GameObject> prefab;
        private readonly IResourceHandle<Font> font;
        private readonly GameObject root;
        private readonly BuildFeedbackPanelView view;
        private readonly BuildFeedbackUiConfig config;
        private double nextRefresh, formedUntil;
        private ulong displayedOffer;
        private ERunResultType? displayedResult;
        private bool expanded, disposed, formedShown, saved;
        private string observedRun, attemptedSettlement;
        private int submittedFrame = -1;
        public GameObject Root => root;

        /// <summary>异步资源就绪后注册本局按钮，Profile为空时明确保持非持久演示。</summary>
        /// <param name="coordinator">当前权威协调器。</param><param name="profile">组合根显式注入的玩家或隔离存储。</param>
        /// <param name="prefab">已加载正式Prefab句柄。</param><param name="font">已加载正式字体句柄。</param><param name="root">本局运行器下的Overlay实例。</param>
        /// <remarks>拥有实例和句柄；Dispose必须先于相机和资源服务释放。</remarks>
        private CombatBuildFeedbackUi(CombatRunCoordinator coordinator, BuildFeedbackProfileStore profile,
            IResourceHandle<GameObject> prefab, IResourceHandle<Font> font, GameObject root)
        {
            this.coordinator = coordinator; this.profile = profile; this.prefab = prefab; this.font = font; this.root = root;
            config = coordinator.Model.Definition.DraftPolicy.UiId_Ref;
            view = root.GetComponent<BuildFeedbackPanelView>() ?? throw new InvalidOperationException("Feedback UI binding missing.");
            foreach (var text in root.GetComponentsInChildren<UnityEngine.UI.Text>(true)) text.font = font.Asset;
            view.RetryButton.onClick.AddListener(RetrySave);
            view.DetailsButton.onClick.AddListener(ToggleDetails);
        }

        /// <summary>正式关卡加载表内反馈Prefab和字体；取消或部分失败沿原资源所有权链立即释放。</summary>
        /// <param name="coordinator">已建立的本局协调器。</param><param name="profile">可选的显式Profile服务。</param>
        /// <param name="resources">统一YooAsset服务。</param><param name="camera">当前本局相机；其父节点是运行器所有者。</param><param name="token">退出取消令牌。</param>
        /// <returns>本局独占的反馈UI。</returns><remarks>资源读取TbBuildFeedbackUi.prefabResourceId与TbCombatUiSet.fontResourceId；与正式HUD同为Overlay并挂到运行器，不继承移动相机变换。</remarks>
        /// <exception cref="OperationCanceledException">退出取消。</exception><exception cref="InvalidOperationException">正式Prefab缺少绑定。</exception>
        public static async Task<CombatBuildFeedbackUi> CreateAsync(CombatRunCoordinator coordinator, BuildFeedbackProfileStore profile,
            IResourceService resources, Camera camera, CancellationToken token)
        {
            IResourceHandle<GameObject> prefab = null; IResourceHandle<Font> font = null; GameObject instance = null;
            try
            {
                prefab = await resources.LoadAssetAsync<GameObject>(coordinator.Model.Definition.DraftPolicy.UiId_Ref.PrefabResourceId, token);
                font = await resources.LoadAssetAsync<Font>(coordinator.Model.Definition.UiSet.FontResourceId, token);
                token.ThrowIfCancellationRequested(); instance = UnityEngine.Object.Instantiate(prefab.Asset, camera.transform.parent, false);
                var canvas = instance.GetComponent<Canvas>() ?? throw new InvalidOperationException("Feedback prefab has no Canvas.");
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) throw new InvalidOperationException("Feedback Canvas must match the formal Overlay HUD.");
                return new CombatBuildFeedbackUi(coordinator, profile, prefab, font, instance);
            }
            catch { if (instance != null) UnityEngine.Object.Destroy(instance); font?.Dispose(); prefab?.Dispose(); throw; }
        }

        /// <summary>渲染帧读取聚合状态；候选变代或结算即时更新，稳定状态使用TbBuildFeedbackUi.refreshIntervalMilli节流。</summary>
        /// <param name="time">未缩放显示时钟，不参与战斗DPS。</param><param name="force">按钮操作后的明确重绘请求。</param>
        public void Refresh(double time, bool force = false)
        {
            if (disposed) return;
            var model = coordinator.Model;
            bool newRun = observedRun != model.RunId;
            if (newRun) { observedRun = model.RunId; attemptedSettlement = null; saved = false; formedShown = false; expanded = false; }
            bool newOffer = displayedOffer != model.UpgradePanelGeneration;
            bool newResult = displayedResult != model.Result;
            if (!newRun && !newOffer && !newResult && !force && time < nextRefresh) return;
            displayedResult = model.Result;
            nextRefresh = time + config.RefreshIntervalMilli / 1000d;
            view.DraftControls.SetActive(model.State == CombatRunState.UpgradeChoice);
            view.HudText.gameObject.SetActive(model.State == CombatRunState.Playing || model.State == CombatRunState.Boss);
            if (newOffer || newRun)
            {
                displayedOffer = model.UpgradePanelGeneration; ulong offer = displayedOffer;
                view.RerollButton.onClick.RemoveAllListeners(); view.SkipButton.onClick.RemoveAllListeners();
                view.RerollButton.onClick.AddListener(() => Decide(offer, true));
                view.SkipButton.onClick.AddListener(() => Decide(offer, false));
            }
            view.RerollText.text = Format(config.RerollFormat, model.RerollsRemaining);
            view.SkipText.text = Format(config.SkipFormat, model.SkipsRemaining);
            view.RerollButton.interactable = model.CanReroll;
            view.SkipButton.interactable = model.SkipsRemaining > 0;
            view.DraftStatus.text = model.LastReplacementCount > 0 ? Format(config.ReplacedFormat, model.LastReplacementCount)
                : model.ReliefActive ? config.ReliefText : model.CanReroll ? string.Empty : config.EmptyText;
            if (model.BuildFormed && !formedShown) { formedShown = true; formedUntil = time + config.FormedNoticeMilli / 1000d; }
            string mechanism = string.Empty;
            if (coordinator.Tribulation != null)
            {
                var signature = coordinator.Tribulation.Config.SignatureId_Ref; var oldUi = coordinator.Tribulation.Config.UiId_Ref;
                var player = coordinator.Session.ReadUnit(0); double simulationTime = coordinator.Session.Statistics.Tick * coordinator.Session.StepSeconds;
                mechanism = signature.Kind == ETribulationSignature.Guard
                    ? player.Target.Guard.IsActive(simulationTime) ? Format(oldUi.GuardActiveFormat, Math.Max(0, player.Target.Guard.UntilSeconds-simulationTime))
                        : Format(oldUi.GuardFormat, player.Target.Guard.Charge, signature.GuardChargeHits)
                    : Format(config.DomainProgressFormat, coordinator.Domains.MoveProgressPixels / signature.DomainStepPixels,
                        coordinator.BuildFeedback.EffectiveDetonations);
            }
            string notice = coordinator.WeaveRemainingSeconds > 0 ? Format(config.WeaveFormat, coordinator.WeaveRemainingSeconds)
                : time < formedUntil ? config.FormedText : string.Empty;
            view.HudText.text = Format(config.HudFormat, mechanism, notice);
            bool settled = model.Result.HasValue; view.SettlementRoot.SetActive(settled);
            if (!settled) return;
            if (attemptedSettlement != model.RunId) TrySave();
            view.SummaryText.text = Summary();
            view.DetailsText.text = expanded ? config.CollapseText : config.DetailsText;
            view.RetryText.text = config.RetryText;
            view.SaveText.text = profile == null ? config.DemoText : saved ? config.SavedText : config.PendingSaveText;
            view.RetryButton.gameObject.SetActive(profile != null && !saved);
        }

        /// <summary>按钮携带显示时的offerId，拒绝同帧连点；成功后只刷新当前正式界面。</summary>
        /// <param name="offer">当前按钮捕获的候选组代次。</param><param name="reroll">是否为重掷，否则跳过。</param>
        private void Decide(ulong offer, bool reroll)
        {
            if (submittedFrame == Time.frameCount) return;
            bool accepted = reroll ? coordinator.RerollUpgrade(offer) : coordinator.SkipUpgrade(offer);
            if (!accepted) return; submittedFrame = Time.frameCount; Refresh(Time.unscaledTimeAsDouble, true);
        }

        /// <summary>按配置名聚合直接来源，默认显示前三个主要来源，展开后显示完整来源。</summary>
        /// <returns>不重复计数的结算摘要及本局目标差距。</returns>
        private string Summary()
        {
            var feedback = coordinator.BuildFeedback; var model = coordinator.Model;
            var rows = new List<KeyValuePair<string, long>>();
            foreach (var weapon in feedback.WeaponDamage)
                rows.Add(new KeyValuePair<string, long>(model.Definition.AvailableSkills.FirstOrDefault(s => s.Id == weapon.Key)?.Name ?? config.WeaponLabel, weapon.Value));
            foreach (var source in feedback.DamageBySource.Where(s => s.Key != CombatDamageSource.Weapon))
                rows.Add(new KeyValuePair<string, long>(source.Key == CombatDamageSource.Counter ? config.CounterLabel
                    : source.Key == CombatDamageSource.DomainPulse ? config.PulseLabel : config.DetonationLabel, source.Value));
            var sorted = rows.OrderByDescending(r => r.Value); var shown = expanded ? sorted.AsEnumerable() : sorted.Take(config.PrimarySourceCount);
            string damage = string.Join("\n", shown.Select(r => Format(config.DamageFormat, r.Key, r.Value)));
            var goal = model.Definition.DraftPolicy.GoalId_Ref;
            string progress = Format(config.GoalFormat, goal.Name, feedback.GoalEvents, goal.RequiredEvents,
                model.Result == ERunResultType.Victory ? model.Definition.UiSet.VictoryText : model.Definition.UiSet.DefeatText);
            string mechanism = goal.SignatureKind == ETribulationSignature.Guard
                ? Format(config.CounterSummaryFormat, feedback.GuardActivations, feedback.EffectiveCounters, feedback.CounterBonusTargets, feedback.CounterTargets)
                : Format(config.DomainSummaryFormat, feedback.EffectiveDetonations, feedback.WeaveActivations);
            if (expanded && feedback.DetonationTargets.Count > 0)
                mechanism += "\n" + Format(config.DetonationTargetsFormat, string.Join(", ", feedback.DetonationTargets));
            return Format(config.SummaryFormat, feedback.TotalDamage, feedback.Dps, damage, mechanism + "\n" + progress);
        }

        /// <summary>真实结算时以稳定身份提交Profile；失败保留当前结算和待保存状态，可再次调用。</summary>
        /// <returns>已持久化或显式非持久演示时为真。</returns>
        public bool TrySave()
        {
            var model = coordinator.Model;
            if (!model.Result.HasValue || profile == null) return true;
            if (saved && attemptedSettlement == model.RunId) return true;
            attemptedSettlement = model.RunId;
            try
            {
                saved = profile.ApplySettlement(model.Definition.DraftPolicy.GoalId_Ref, new BuildFeedbackSettlement {
                    RunId = model.RunId, SettlementId = model.RunId + "/final", StageId = model.Definition.Stage.Id,
                    CharacterId = model.Definition.Character.Id, Victory = model.Result == ERunResultType.Victory,
                    QualifiedEvents = coordinator.BuildFeedback.GoalEvents, BestTargets = coordinator.BuildFeedback.BestTargets });
            }
            catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException || exception is System.Runtime.Serialization.SerializationException)
            { saved = false; Debug.LogWarning("[Roguelike] Profile settlement is pending; retry is available."); }
            return saved;
        }

        /// <summary>保存失败按钮重复使用相同结算身份，成功后刷新持久化状态。</summary>
        private void RetrySave() { TrySave(); Refresh(Time.unscaledTimeAsDouble, true); }
        /// <summary>结算时切换来源展开状态，不改变统计或重新提交奖励。</summary>
        private void ToggleDetails() { expanded = !expanded; Refresh(Time.unscaledTimeAsDouble, true); }
        /// <summary>统一按配置格式和稳定文化生成显示文本。</summary>
        /// <param name="format">TbBuildFeedbackUi格式。</param><param name="args">聚合显示值。</param><returns>格式化文本。</returns>
        private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

        /// <summary>运行器关闭时解除监听、销毁实例并释放字体和Prefab句柄；重复调用安全。</summary>
        /// <remarks>必须先于本局相机及资源服务释放，不创建或保存预览资产。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (root != null) UnityEngine.Object.Destroy(root); font.Dispose(); prefab.Dispose();
        }
    }
}
