using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class CombatUiRuntime
    {
        private readonly EventUiBundle eventUi;
        private readonly Func<ulong, int, bool> claimChest;
        private ulong displayedChestGeneration;
        private CombatBuildState eventBuild;
        private ulong eventBuildRevision;
        private int readyEvolutionCount;

        /// <summary>事件界面资源的单一所有者；加载失败、退出及重复释放都有明确清理路径。</summary>
        private sealed class EventUiBundle : IDisposable
        {
            public ChestRewardPanelView Chest;
            public RunEventHudView Hud;
            private IResourceHandle<GameObject> chestHandle, hudHandle;
            private readonly Dictionary<int, IResourceHandle<GameObject>> worldHandles = new Dictionary<int, IResourceHandle<GameObject>>();
            private readonly List<ChestMarker> markers = new List<ChestMarker>();
            private bool disposed;

            /// <summary>池化场上标记的瞬时绑定；从不序列化或保存运行时对象。</summary>
            private sealed class ChestMarker
            {
                public int ResourceId;
                public ulong Id;
                public CombatChestWorldView View;
            }

            /// <summary>正式界面创建时加载 TbCombatUiSet 和全部 TbChestProfile 的资源闭包。</summary>
            /// <param name="definition">已验证的单局定义。</param>
            /// <param name="resources">统一资源服务。</param>
            /// <param name="parent">运行时 HUD 根，拥有子对象生命周期。</param>
            /// <param name="token">退出取消令牌。</param>
            /// <returns>完整资源及视图绑定。</returns>
            /// <remarks>任何中断均先销毁已创建子对象再释放句柄，不修改正式 Prefab。</remarks>
            /// <exception cref="InvalidOperationException">资源或序列化绑定缺失。</exception>
            /// <exception cref="OperationCanceledException">退出取消加载。</exception>
            public static async Task<EventUiBundle> CreateAsync(CombatRunDefinition definition, IResourceService resources,
                Transform parent, CancellationToken token)
            {
                var result = new EventUiBundle();
                try
                {
                    result.chestHandle = await resources.LoadAssetAsync<GameObject>(definition.UiSet.ChestPanelPrefabResourceId, token);
                    result.hudHandle = await resources.LoadAssetAsync<GameObject>(definition.UiSet.EventHudPrefabResourceId, token);
                    foreach (int resourceId in definition.ChestProfiles.Select(item => item.WorldPrefabResourceId).Distinct())
                        result.worldHandles.Add(resourceId, await resources.LoadAssetAsync<GameObject>(resourceId, token));
                    token.ThrowIfCancellationRequested();
                    result.Hud = RequireView<RunEventHudView>(UnityEngine.Object.Instantiate(result.hudHandle.Asset, parent, false), "RunEventHud");
                    result.Chest = RequireView<ChestRewardPanelView>(UnityEngine.Object.Instantiate(result.chestHandle.Asset, parent, false), "ChestRewardPanel");
                    if (result.Hud.ChestMarkerContainer == null || result.Chest.ClaimButton == null || result.Chest.CardContainer == null ||
                        result.Chest.TitleText == null || result.Chest.RewardNameText == null || result.Chest.DescriptionText == null ||
                        result.Chest.SingleRewardRoot == null || result.Chest.ClaimText == null || result.Hud.NoticeText == null ||
                        result.Hud.EliteText == null || result.Hud.StatusText == null)
                        throw new InvalidOperationException("Event UI prefab lacks authored bindings.");
                    result.Chest.gameObject.SetActive(false);
                    return result;
                }
                catch { result.Dispose(); throw; }
            }

            /// <summary>每帧按未拾取箱位置投影；只更新世界跟踪位置与可见性，不动态修改图形尺寸和布局。</summary>
            /// <param name="chests">协调器当前场上箱。</param>
            /// <param name="camera">当前正式战斗相机。</param>
            /// <param name="font">TbCombatUiSet 持有的字体。</param>
            /// <remarks>首次并发不足时实例化正式 Prefab；领取后隐藏回池，重开复用，退出统一销毁。</remarks>
            public void SynchronizeMarkers(IReadOnlyDictionary<ulong, CombatRuntimeChest> chests, Camera camera, Font font)
            {
                foreach (ChestMarker marker in markers)
                    if (marker.Id != 0 && !chests.ContainsKey(marker.Id)) { marker.Id = 0; marker.View.gameObject.SetActive(false); }
                foreach (CombatRuntimeChest chest in chests.Values)
                {
                    ChestMarker marker = markers.FirstOrDefault(item => item.Id == chest.Id);
                    if (marker == null)
                    {
                        marker = markers.FirstOrDefault(item => item.Id == 0 && item.ResourceId == chest.Profile.WorldPrefabResourceId);
                        if (marker == null)
                        {
                            CombatChestWorldView view = RequireView<CombatChestWorldView>(UnityEngine.Object.Instantiate(
                                worldHandles[chest.Profile.WorldPrefabResourceId].Asset, Hud.ChestMarkerContainer, false), "CombatChestWorld");
                            if (view.NameText == null) throw new InvalidOperationException("CombatChestWorld lacks its authored title.");
                            ApplyFont(view.gameObject, font);
                            marker = new ChestMarker { ResourceId = chest.Profile.WorldPrefabResourceId, View = view };
                            markers.Add(marker);
                        }
                        marker.Id = chest.Id;
                        marker.View.NameText.text = chest.Profile.Name;
                    }
                    Vector3 screen = camera.WorldToScreenPoint(new Vector3(chest.Position.x, chest.Position.y, 0));
                    bool visible = screen.z > 0 && screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
                    marker.View.gameObject.SetActive(visible);
                    if (visible && RectTransformUtility.ScreenPointToLocalPointInRectangle(Hud.ChestMarkerContainer,
                        screen, camera, out Vector2 local)) ((RectTransform)marker.View.transform).anchoredPosition = local;
                }
            }

            /// <summary>运行器退出或加载失败时清除自有子对象并释放全部资源句柄，重复调用安全。</summary>
            /// <remarks>HUD 父对象仍由外层 CombatUiRuntime 拥有；标记随 HUD 子树销毁。</remarks>
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (Chest != null) UnityEngine.Object.Destroy(Chest.gameObject);
                if (Hud != null) UnityEngine.Object.Destroy(Hud.gameObject);
                markers.Clear();
                foreach (var handle in worldHandles.Values) handle.Dispose();
                worldHandles.Clear(); hudHandle?.Dispose(); chestHandle?.Dispose();
            }
        }

        /// <summary>按模型交互状态显示唯一宝箱面板，更新事件通知、最近精英位置及可进化状态。</summary>
        /// <param name="model">纯规则状态及正式数据引用。</param>
        /// <param name="coordinator">场上宝箱位置所有者。</param>
        /// <param name="session">当前生命周期的原生战斗会话。</param>
        /// <remarks>候选仅在面板代次变化时重建；进化资格仅在构筑代次变化时重算。</remarks>
        private void SynchronizeEventUi(CombatRunModel model, CombatRunCoordinator coordinator, CombatSession session)
        {
            bool choosing = model.State == CombatRunState.ChestReward;
            eventUi.Chest.gameObject.SetActive(choosing);
            if (choosing && displayedChestGeneration != model.ChestPanelGeneration) RebuildChestReward(model);
            eventUi.Hud.NoticeText.text = model.EventNotice;
            if (model.Build == null) readyEvolutionCount = 0;
            else if (eventBuild != model.Build || eventBuildRevision != model.Build.Revision)
            {
                eventBuild = model.Build; eventBuildRevision = model.Build.Revision;
                readyEvolutionCount = model.Build.EligibleEvolutions().Count;
            }
            var statuses = new List<string>();
            if (readyEvolutionCount > 0) statuses.Add(string.Format(CultureInfo.InvariantCulture, definition.UiSet.EvolutionReadyFormat, readyEvolutionCount));
            if (coordinator.Chests.Count > 0) statuses.Add(string.Format(CultureInfo.InvariantCulture, definition.UiSet.ChestStatusFormat, coordinator.Chests.Count));
            eventUi.Hud.StatusText.text = statuses.Count > 1 ? string.Join(model.Build.Config.EntrySeparator, statuses) :
                statuses.Count == 1 ? statuses[0] : string.Empty;
            float nearest = float.PositiveInfinity;
            CombatUnit elite = default;
            float2 player = session.ReadUnit(0).Position;
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                CombatUnit unit = session.ReadUnit(slot);
                if (!unit.IsElite || unit.Target.Health <= 0) continue;
                float distance = math.distancesq(player, unit.Position);
                if (distance < nearest) { nearest = distance; elite = unit; }
            }
            if (float.IsPositiveInfinity(nearest)) eventUi.Hud.EliteText.text = string.Empty;
            else
            {
                AnimationDirectionConfig direction = definition.Presentation.DirectionIds_Ref.OrderByDescending(item =>
                    math.dot(math.normalizesafe(elite.Position - player), math.normalizesafe(new float2(item.X,item.Y)))).First();
                string name = definition.Monsters.First(item => item.Id == elite.ConfigId).Name;
                if (model.Build != null) name = string.Join(model.Build.Config.EntrySeparator, name, direction.Name);
                eventUi.Hud.EliteText.text = string.Format(CultureInfo.InvariantCulture,
                    definition.UiSet.EliteStatusFormat,name,math.sqrt(nearest));
            }
            eventUi.SynchronizeMarkers(coordinator.Chests, root.GetComponent<Canvas>().worldCamera, font);
        }

        /// <summary>新箱代次展示一条进化、正式池候选或明确的候选耗尽提示；布局沿用序列化 Prefab。</summary>
        /// <param name="model">当前 ChestReward 状态。</param>
        /// <remarks>旧按钮和卡片移除监听后销毁，回调始终携带创建时的面板代次。</remarks>
        private void RebuildChestReward(CombatRunModel model)
        {
            ChestRewardPanelView panel = eventUi.Chest;
            for (int index = panel.CardContainer.childCount - 1; index >= 0; index--)
                UnityEngine.Object.Destroy(panel.CardContainer.GetChild(index).gameObject);
            panel.ClaimButton.onClick.RemoveAllListeners();
            displayedChestGeneration = model.ChestPanelGeneration;
            ulong generation = displayedChestGeneration;
            ChestProfileConfig profile = model.CurrentChest.Profile;
            panel.TitleText.text = model.ChestEvolution != null ? profile.EvolutionTitle : profile.NormalTitle;
            bool single = model.ChestChoices.Count == 0;
            panel.SingleRewardRoot.SetActive(single);
            panel.CardContainer.gameObject.SetActive(!single);
            if (single)
            {
                panel.RewardNameText.text = model.ChestEvolution != null ? model.ChestEvolution.Name : profile.EmptyName;
                panel.DescriptionText.text = model.ChestEvolution != null ? model.ChestEvolution.Description : profile.EmptyDescription;
                panel.ClaimText.text = profile.ClaimText;
                panel.ClaimButton.onClick.AddListener(() => claimChest?.Invoke(generation, 0));
            }
            else foreach (UpgradeOptionConfig option in model.ChestChoices)
            {
                UpgradeCardView card = RequireView<UpgradeCardView>(UnityEngine.Object.Instantiate(upgradeCardPrefab, panel.CardContainer, false), "UpgradeCard");
                ApplyFont(card.gameObject, font);
                card.NameText.text = option.Name; card.DescriptionText.text = option.Description;
                string format = option.Type == EUpgradeOptionType.RunWeapon ? definition.BuildConfig.WeaponRankFormat :
                    option.Type == EUpgradeOptionType.RunPassive ? definition.BuildConfig.PassiveRankFormat : definition.UiSet.RankFormat;
                card.RankText.text = string.Format(CultureInfo.InvariantCulture, format, model.GetOptionRank(option) + 1, option.MaxRank);
                card.IconImage.enabled = option.IconResourceId.HasValue;
                int optionId = option.Id;
                card.SelectButton.onClick.AddListener(() => claimChest?.Invoke(generation, optionId));
            }
        }
    }
}
