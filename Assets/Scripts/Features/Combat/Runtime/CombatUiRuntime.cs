using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>实例化并刷新正式战斗 UI Prefab，不把布局或业务文本复制到运行时代码。</summary>
    public sealed class CombatUiRuntime : IDisposable
    {
        private readonly CombatRunDefinition definition;
        private readonly GameObject root;
        private readonly CombatHudView hud;
        private readonly BossHealthBarView boss;
        private readonly UpgradeChoicePanelView upgrade;
        private readonly SettlementPanelView settlement;
        private readonly GameObject upgradeCardPrefab;
        private readonly Font font;
        private readonly IResourceHandle<GameObject> hudHandle;
        private readonly IResourceHandle<GameObject> bossHandle;
        private readonly IResourceHandle<GameObject> upgradeHandle;
        private readonly IResourceHandle<GameObject> cardHandle;
        private readonly IResourceHandle<GameObject> settlementHandle;
        private readonly IResourceHandle<Font> fontHandle;
        private readonly GameObject ownedEventSystem;
        private readonly Func<ulong, int, bool> chooseUpgrade;
        private readonly Func<bool> restart;
        private ulong displayedPanelGeneration;
        private bool disposed;

        /// <summary>获取已实例化的可调 HUD 根节点，供 PlayMode 验收定位。</summary>
        public GameObject Root => root;

        /// <summary>接管全部资源句柄和实例；调用方只能通过 CreateAsync 构造完整对象。</summary>
        private CombatUiRuntime(CombatRunDefinition definition, GameObject root, CombatHudView hud,
            BossHealthBarView boss, UpgradeChoicePanelView upgrade, SettlementPanelView settlement,
            GameObject upgradeCardPrefab, Font font, IResourceHandle<GameObject> hudHandle,
            IResourceHandle<GameObject> bossHandle, IResourceHandle<GameObject> upgradeHandle,
            IResourceHandle<GameObject> cardHandle, IResourceHandle<GameObject> settlementHandle,
            IResourceHandle<Font> fontHandle, GameObject ownedEventSystem,
            Func<ulong, int, bool> chooseUpgrade, Func<bool> restart)
        {
            this.definition = definition;
            this.root = root;
            this.hud = hud;
            this.boss = boss;
            this.upgrade = upgrade;
            this.settlement = settlement;
            this.upgradeCardPrefab = upgradeCardPrefab;
            this.font = font;
            this.hudHandle = hudHandle;
            this.bossHandle = bossHandle;
            this.upgradeHandle = upgradeHandle;
            this.cardHandle = cardHandle;
            this.settlementHandle = settlementHandle;
            this.fontHandle = fontHandle;
            this.ownedEventSystem = ownedEventSystem;
            this.chooseUpgrade = chooseUpgrade;
            this.restart = restart;
            settlement.RestartButton.onClick.AddListener(RestartFromButton);
        }

        /// <summary>按 TbCombatUiSet 逐项加载正式 Prefab 与字体，并在运行器节点下组成界面。</summary>
        /// <param name="definition">已经通过 UI 引用校验的 TbStage 聚合定义。</param>
        /// <param name="resources">统一 YooAsset 资源服务。</param>
        /// <param name="parent">战斗运行器拥有的生命周期父节点。</param>
        /// <param name="camera">正式战斗相机，用于让 ScreenSpaceCamera Canvas 进入同一帧缓冲。</param>
        /// <param name="chooseUpgrade">升级卡按钮提交入口。</param>
        /// <param name="restart">结算按钮重开入口。</param>
        /// <param name="token">退出加载令牌。</param>
        /// <returns>全部资源及实例就绪的正式 UI。</returns>
        /// <remarks>部分加载或实例化失败时立即释放已有句柄和对象，不保存 Scene 或 Prefab。</remarks>
        public static async Task<CombatUiRuntime> CreateAsync(CombatRunDefinition definition,
            IResourceService resources, Transform parent, Camera camera, Func<ulong, int, bool> chooseUpgrade,
            Func<bool> restart, CancellationToken token)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            definition.RequireUiReady();
            IResourceHandle<GameObject> hudHandle = null, bossHandle = null, upgradeHandle = null,
                cardHandle = null, settlementHandle = null;
            IResourceHandle<Font> fontHandle = null;
            GameObject root = null, eventSystem = null;
            try
            {
                CombatUiSetConfig ui = definition.UiSet;
                hudHandle = await resources.LoadAssetAsync<GameObject>(ui.HudPrefabResourceId, token);
                bossHandle = await resources.LoadAssetAsync<GameObject>(ui.BossBarPrefabResourceId, token);
                upgradeHandle = await resources.LoadAssetAsync<GameObject>(ui.UpgradePanelPrefabResourceId, token);
                cardHandle = await resources.LoadAssetAsync<GameObject>(ui.UpgradeCardPrefabResourceId, token);
                settlementHandle = await resources.LoadAssetAsync<GameObject>(ui.SettlementPrefabResourceId, token);
                fontHandle = await resources.LoadAssetAsync<Font>(ui.FontResourceId, token);
                token.ThrowIfCancellationRequested();

                root = UnityEngine.Object.Instantiate(hudHandle.Asset, parent, false);
                CombatHudView hud = RequireView<CombatHudView>(root, "CombatHud");
                Canvas canvas = root.GetComponent<Canvas>() ??
                    throw new InvalidOperationException("CombatHud prefab lacks its Canvas.");
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                BossHealthBarView boss = RequireView<BossHealthBarView>(
                    UnityEngine.Object.Instantiate(bossHandle.Asset, root.transform, false), "BossHealthBar");
                UpgradeChoicePanelView upgrade = RequireView<UpgradeChoicePanelView>(
                    UnityEngine.Object.Instantiate(upgradeHandle.Asset, root.transform, false), "UpgradeChoicePanel");
                SettlementPanelView settlement = RequireView<SettlementPanelView>(
                    UnityEngine.Object.Instantiate(settlementHandle.Asset, root.transform, false), "SettlementPanel");
                if (EventSystem.current == null)
                {
                    eventSystem = new GameObject("Combat Event System", typeof(EventSystem), typeof(StandaloneInputModule));
                    eventSystem.transform.SetParent(parent, false);
                }
                ApplyFont(root, fontHandle.Asset);
                return new CombatUiRuntime(definition, root, hud, boss, upgrade, settlement,
                    cardHandle.Asset, fontHandle.Asset, hudHandle, bossHandle, upgradeHandle, cardHandle,
                    settlementHandle, fontHandle, eventSystem, chooseUpgrade, restart);
            }
            catch
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                if (eventSystem != null) UnityEngine.Object.Destroy(eventSystem);
                fontHandle?.Dispose(); settlementHandle?.Dispose(); cardHandle?.Dispose();
                upgradeHandle?.Dispose(); bossHandle?.Dispose(); hudHandle?.Dispose();
                throw;
            }
        }

        /// <summary>按当前单局模型和 ECS 实体更新 HUD、Boss、升级三选一与结算面板。</summary>
        /// <param name="model">表驱动单局状态。</param>
        /// <param name="coordinator">正式暂停与掉落协调器。</param>
        /// <param name="session">玩家和 Boss 实体快照来源。</param>
        /// <remarks>界面只读模型并通过委托提交按钮，不直接修改 ECS 或配置对象。</remarks>
        public void Refresh(CombatRunModel model, CombatRunCoordinator coordinator, CombatSession session)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatUiRuntime));
            CombatUiSetConfig ui = definition.UiSet;
            int minutes = (int)(model.ElapsedMilli / 60000);
            int seconds = (int)(model.ElapsedMilli / 1000 % 60);
            int requiredExperience = model.Level - 1 < definition.ExperienceLevels.Count
                ? definition.ExperienceLevels[model.Level - 1].RequiredExperience
                : Math.Max(1, model.Experience);
            hud.HealthText.text = string.Format(CultureInfo.InvariantCulture, ui.HealthFormat, model.CurrentHealth, model.MaxHealth);
            hud.TimerText.text = string.Format(CultureInfo.InvariantCulture, ui.TimerFormat, minutes, seconds);
            hud.LevelText.text = string.Format(CultureInfo.InvariantCulture, ui.LevelFormat, model.Level);
            hud.ExperienceText.text = string.Format(CultureInfo.InvariantCulture, ui.ExperienceFormat, model.Experience, requiredExperience);
            hud.KillsText.text = string.Format(CultureInfo.InvariantCulture, ui.KillsFormat, model.Kills);
            hud.WaveText.text = string.Format(CultureInfo.InvariantCulture, ui.WaveFormat, model.CompletedWaves);
            hud.ExperienceBar.value = Mathf.Clamp01(model.Experience / (float)requiredExperience);

            bool bossVisible = TryReadBoss(session, out CombatUnit bossUnit);
            boss.gameObject.SetActive(bossVisible);
            if (bossVisible)
            {
                boss.NameText.text = definition.Boss.HealthBarText;
                boss.HealthText.text = string.Format(CultureInfo.InvariantCulture, ui.BossHealthFormat,
                    bossUnit.Target.Health, bossUnit.Target.MaxHealth);
                boss.HealthBar.value = bossUnit.Target.MaxHealth <= 0 ? 0 :
                    Mathf.Clamp01(bossUnit.Target.Health / (float)bossUnit.Target.MaxHealth);
            }

            bool choosing = model.State == CombatRunState.UpgradeChoice;
            upgrade.gameObject.SetActive(choosing);
            if (choosing && displayedPanelGeneration != model.UpgradePanelGeneration)
                RebuildUpgradeCards(model);

            bool settled = model.State == CombatRunState.VictorySettlement || model.State == CombatRunState.DefeatSettlement;
            settlement.gameObject.SetActive(settled);
            if (settled)
            {
                settlement.ResultText.text = model.State == CombatRunState.VictorySettlement ? ui.VictoryText : ui.DefeatText;
                settlement.StatisticsText.text = string.Format(CultureInfo.InvariantCulture, ui.SettlementStatsFormat,
                    minutes, seconds, model.Kills, model.Level);
                settlement.RestartText.text = ui.RestartText;
            }
        }

        /// <summary>销毁当前三选一卡片并按模型候选顺序实例化可点击卡片。</summary>
        /// <param name="model">当前处于 UpgradeChoice 的单局模型。</param>
        /// <remarks>图标资源尚未配置时使用 Prefab 内的空槽，不生成替代贴图。</remarks>
        private void RebuildUpgradeCards(CombatRunModel model)
        {
            for (int index = upgrade.CardContainer.childCount - 1; index >= 0; index--)
                UnityEngine.Object.Destroy(upgrade.CardContainer.GetChild(index).gameObject);
            displayedPanelGeneration = model.UpgradePanelGeneration;
            upgrade.TitleText.text = definition.UiSet.LevelUpTitle;
            foreach (UpgradeOptionConfig option in model.CurrentChoices)
            {
                UpgradeCardView card = RequireView<UpgradeCardView>(
                    UnityEngine.Object.Instantiate(upgradeCardPrefab, upgrade.CardContainer, false), "UpgradeCard");
                ApplyFont(card.gameObject, font);
                card.NameText.text = option.Name;
                card.DescriptionText.text = option.Description;
                int rank = model.UpgradeRanks.TryGetValue(option.Id, out int current) ? current + 1 : 1;
                card.RankText.text = string.Format(CultureInfo.InvariantCulture, definition.UiSet.RankFormat, rank, option.MaxRank);
                card.IconImage.enabled = option.IconResourceId.HasValue;
                ulong generation = displayedPanelGeneration;
                int optionId = option.Id;
                card.SelectButton.onClick.AddListener(() => ChooseFromCard(generation, optionId));
            }
        }

        /// <summary>升级卡点击时提交创建该卡片时的面板代次，成功后下一帧按模型刷新。</summary>
        /// <param name="generation">卡片所属面板代次。</param>
        /// <param name="optionId">TbUpgradeOption.id。</param>
        private void ChooseFromCard(ulong generation, int optionId)
        {
            chooseUpgrade?.Invoke(generation, optionId);
        }

        /// <summary>结算按钮触发正式重开委托。</summary>
        private void RestartFromButton()
        {
            restart?.Invoke();
        }

        /// <summary>从会话槽位中定位当前存活 Boss。</summary>
        /// <param name="session">正式 ECS 会话。</param>
        /// <param name="bossUnit">找到时返回 Boss 快照。</param>
        /// <returns>存在存活 Boss 时为真。</returns>
        private static bool TryReadBoss(CombatSession session, out CombatUnit bossUnit)
        {
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                CombatUnit unit = session.ReadUnit(slot);
                if (!unit.IsBoss || unit.Target.Health <= 0) continue;
                bossUnit = unit;
                return true;
            }
            bossUnit = default;
            return false;
        }

        /// <summary>要求实例化 Prefab 根上存在指定绑定组件。</summary>
        /// <typeparam name="TView">预期绑定组件。</typeparam>
        /// <param name="instance">刚实例化的正式 Prefab。</param>
        /// <param name="label">错误定位名称。</param>
        /// <returns>已验证组件。</returns>
        /// <exception cref="InvalidOperationException">Prefab 缺少绑定组件。</exception>
        private static TView RequireView<TView>(GameObject instance, string label) where TView : Component
        {
            TView view = instance.GetComponent<TView>();
            return view != null ? view : throw new InvalidOperationException(label + " prefab lacks its view binding.");
        }

        /// <summary>把 TbCombatUiSet.fontResourceId 加载的字体应用到当前实例全部 uGUI 文本。</summary>
        /// <param name="instance">HUD、面板或升级卡实例。</param>
        /// <param name="font">YooAsset 持有的正式 uGUI 字体。</param>
        private static void ApplyFont(GameObject instance, Font font)
        {
            foreach (Text text in instance.GetComponentsInChildren<Text>(true)) text.font = font;
        }

        /// <summary>销毁 UI 实例并按加载逆序释放 YooAsset 句柄。</summary>
        /// <remarks>由运行器在世界和共享资源释放前调用；重复调用安全。</remarks>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            settlement.RestartButton.onClick.RemoveListener(RestartFromButton);
            if (root != null) UnityEngine.Object.Destroy(root);
            if (ownedEventSystem != null) UnityEngine.Object.Destroy(ownedEventSystem);
            fontHandle.Dispose(); settlementHandle.Dispose(); cardHandle.Dispose();
            upgradeHandle.Dispose(); bossHandle.Dispose(); hudHandle.Dispose();
        }
    }
}
