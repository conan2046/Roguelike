using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Roguelike.Core;
using Roguelike.Core.Features;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.App
{
    public sealed partial class GameApplicationBootstrap
    {
        private TribulationEntryController tribulationEntry;

        /// <summary>切片启动仅选择入口模式；职业、关卡和资源在配置就绪后由Luban入口表决定。</summary>
        private sealed class TribulationStartupFeature : IGameFeature
        {
            private readonly GameApplicationBootstrap owner;
            public string Name => "Tribulation";

            /// <summary>保留应用所有者以接管后续选角与战斗生命周期。</summary><param name="owner">组合根。</param>
            public TribulationStartupFeature(GameApplicationBootstrap owner) { this.owner = owner; }

            /// <summary>资源与配置可用后建立正式选角入口；角色加载由玩家按钮触发。</summary>
            /// <param name="services">已初始化服务。</param><param name="cancellationToken">应用退出令牌。</param>
            /// <returns>选角就绪的任务。</returns><remarks>入口挂在持久应用根下，资源服务停止前必须排空ShutdownAsync。</remarks>
            public async Task InitializeAsync(ApplicationServices services, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = new GameObject("[Roguelike.Tribulation]"); root.transform.SetParent(owner.transform, false);
                owner.tribulationEntry = root.AddComponent<TribulationEntryController>();
                var profileConfig = services.Config.Tables.TbBuildFeedbackUi.DataList.Single();
                var profile = new BuildFeedbackProfileStore(Application.persistentDataPath, profileConfig);
                await owner.tribulationEntry.InitializeAsync(services.Config.Tables, services.Resources, cancellationToken, profile);
            }
        }
    }
}
