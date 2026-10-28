using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>切片入口所有者：正式选角、唯一战斗运行器、返回及退出取消统一管理。</summary>
    public sealed partial class TribulationEntryController : MonoBehaviour
    {
        private Tables tables;
        private IResourceService resources;
        private TribulationUiConfig entry;
        private CancellationTokenSource linked;
        private IResourceHandle<GameObject> lobbyHandle;
        private GameObject lobbyRoot, ownedEventSystem;
        private TribulationLobbyView lobby;
        private Task transitionTask, stopTask;
        private bool busy, closed;
        public CombatRuntimeRunner ActiveRunner { get; private set; }
        public GameObject LobbyRoot => lobbyRoot;

        /// <summary>配置/资源服务初始化后读取唯一入口表并实例化正式选角Prefab。</summary>
        /// <param name="tables">当前Luban表。</param><param name="resources">组合根资源服务。</param><param name="token">应用关闭令牌。</param>
        /// <param name="profileStore">显式玩家或隔离Profile；为空时不访问存档，只展示非持久演示。</param>
        /// <returns>选角按钮可以交互时完成。</returns>
        /// <remarks>持有Prefab句柄与组合取消源；部分失败主动关闭；不更改场景资产。</remarks>
        /// <exception cref="InvalidOperationException">入口/Prefab/绑定缺失或重复初始化。</exception><exception cref="OperationCanceledException">退出取消。</exception>
        public async Task InitializeAsync(Tables tables, IResourceService resources, CancellationToken token, Run.BuildFeedbackProfileStore profileStore = null)
        {
            if (linked != null || closed) throw new InvalidOperationException("Tribulation entry cannot initialize twice.");
            this.tables = tables; this.resources = resources;
            entry = tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var loadToken = linked.Token;
            try
            {
                lobbyHandle = await resources.LoadAssetAsync<GameObject>(entry.LobbyPrefabResourceId, loadToken);
                loadToken.ThrowIfCancellationRequested();
                lobbyRoot = Instantiate(lobbyHandle.Asset, transform, false);
                lobby = lobbyRoot.GetComponent<TribulationLobbyView>() ?? throw new InvalidOperationException("Tribulation lobby binding is missing.");
                if (lobby.RoleButtons.Length != entry.StageIds.Count || lobby.RoleNames.Length != entry.StageIds.Count ||
                    lobby.RoleDescriptions.Length != entry.StageIds.Count || lobby.RoleButtonLabels.Length != entry.StageIds.Count)
                    throw new InvalidOperationException("Tribulation lobby slot count differs from entry table.");
                lobby.TitleText.text = entry.Title; lobby.IntroText.text = entry.Intro; lobby.ErrorText.text = string.Empty;
                for (int i = 0; i < entry.StageIds.Count; i++)
                {
                    int index = i; var config = entry.StageIds_Ref[i].TribulationSliceId_Ref;
                    if (config == null) throw new InvalidOperationException("Entry stage has no Tribulation slice.");
                    lobby.RoleNames[i].text = config.RoleName; lobby.RoleDescriptions[i].text = config.RoleDescription;
                    lobby.RoleButtonLabels[i].text = entry.ChooseRoleText;
                    lobby.RoleButtons[i].onClick.AddListener(() => RequestRole(index));
                }
                InitializeFeedbackLobby(profileStore);
                if (EventSystem.current == null)
                {
                    ownedEventSystem = new GameObject("Tribulation input", typeof(EventSystem), typeof(StandaloneInputModule));
                    ownedEventSystem.transform.SetParent(transform, false);
                }
            }
            catch { Close(); ReleaseLobby(); throw; }
        }

        /// <summary>选角按钮边界拒绝双击与退出，记录可排空任务并启动相应正式关卡。</summary>
        /// <param name="index">入口表中的角色索引。</param>
        /// <remarks>任务内部捕获并显示失败；同一时刻最多存在一个World加载流程。</remarks>
        private void RequestRole(int index)
        {
            if (busy || closed || ActiveRunner != null) return;
            busy = true; SetButtons(false); lobby.ErrorText.text = string.Empty;
            transitionTask = EnterRoleAsync(index);
        }

        /// <summary>按入口表选定角色创建唯一运行器，加载成功才隐藏选角；失败回收局部对象并恢复选择。</summary>
        /// <param name="index">入口表角色索引。</param><returns>该次切换完成的任务。</returns>
        /// <remarks>全部加载沿组合令牌取消；失败释放World和资源，日志只报告一次。</remarks>
        private async Task EnterRoleAsync(int index)
        {
            var loadToken = linked.Token;
            try
            {
                var definition = CombatRunDefinition.Create(tables, entry.StageIds[index]);
                var root = new GameObject("[Roguelike.Tribulation.Run]"); root.transform.SetParent(transform, false);
                ActiveRunner = root.AddComponent<CombatRuntimeRunner>();
                ActiveRunner.ReturnToLobby = ReturnToLobby;
                await ActiveRunner.InitializeAsync(definition, resources, new UnityCombatInput(definition.Presentation), loadToken, profileStoreForFeedback);
                loadToken.ThrowIfCancellationRequested(); lobbyRoot.SetActive(false);
            }
            catch (OperationCanceledException) { ReleaseRunner(); }
            catch (Exception exception)
            {
                ReleaseRunner();
                if (!closed && lobby != null) { lobbyRoot.SetActive(true); lobby.ErrorText.text = entry.ErrorText; Debug.LogException(exception, this); }
            }
            finally { busy = false; if (!closed) SetButtons(true); }
        }

        /// <summary>局内或结算点击返回时先完整关闭旧World，再恢复当前入口选角。</summary>
        /// <remarks>保留同一份入口Prefab和字体句柄，不创建新EventSystem或第二个World。</remarks>
        public void ReturnToLobby()
        {
            if (closed || busy) return;
            if (ActiveRunner != null && !ActiveRunner.TryFinalizeProfile()) return;
            ReleaseRunner(); lobbyRoot.SetActive(true); lobby.ErrorText.text = string.Empty; SetButtons(true);
            RefreshFeedbackLobby();
        }

        /// <summary>关闭唯一战斗运行器并安排销毁其所有者节点。</summary>
        /// <remarks>Close先同步回收原生World/资源，GameObject随后在帧末销毁。</remarks>
        private void ReleaseRunner()
        {
            if (ActiveRunner == null) return;
            ActiveRunner.Close(); Destroy(ActiveRunner.gameObject); ActiveRunner = null;
        }

        /// <summary>角色加载期间只改变按钮交互状态，不更改任何布局。</summary><param name="enabled">是否允许选择。</param>
        private void SetButtons(bool enabled) { if (lobby != null) foreach (var button in lobby.RoleButtons) button.interactable = enabled; }

        /// <summary>应用退出先同步取消加载并关闭现有战斗；资源服务释放前还需等待ShutdownAsync。</summary>
        /// <remarks>幂等；取消源在异步工作排空后才释放。</remarks>
        public void Close()
        {
            if (closed) return; closed = true; linked?.Cancel(); ReleaseRunner();
            if (lobbyRoot != null) lobbyRoot.SetActive(false);
        }

        /// <summary>组合根停止时排空角色加载，随后释放入口、输入系统、资源句柄和取消源。</summary>
        /// <returns>所有本入口异步消费者都已结束的任务。</returns><remarks>资源服务必须在此任务完成后再销毁。</remarks>
        public Task ShutdownAsync() { Close(); return stopTask ?? (stopTask = DrainAndReleaseAsync()); }

        /// <summary>关闭后等待正在运行的切换完成，确保迟到句柄先于资源包回收。</summary>
        /// <returns>句柄及取消源释放完毕的任务。</returns><remarks>失败已由切换边界报告，不在清理阶段重复抛出。</remarks>
        private async Task DrainAndReleaseAsync()
        {
            try { if (transitionTask != null) await transitionTask; }
            finally { ReleaseLobby(); linked?.Dispose(); linked = null; }
        }

        /// <summary>解除选角监听并回收当前入口实例、输入系统及Prefab句柄。</summary>
        /// <remarks>只释放本所有者创建的EventSystem；不触碰外部系统。</remarks>
        private void ReleaseLobby()
        {
            if (lobby != null) foreach (var button in lobby.RoleButtons) button.onClick.RemoveAllListeners();
            if (lobbyRoot != null) { lobbyRoot.SetActive(false); Destroy(lobbyRoot); lobbyRoot = null; lobby = null; }
            if (ownedEventSystem != null) { Destroy(ownedEventSystem); ownedEventSystem = null; }
            lobbyHandle?.Dispose(); lobbyHandle = null;
        }

        /// <summary>Unity销毁本入口时取消并排空未完成加载。</summary><remarks>正常路径由组合根提前等待ShutdownAsync，此回调重复安全。</remarks>
        private async void OnDestroy() { await ShutdownAsync(); }
    }
}
