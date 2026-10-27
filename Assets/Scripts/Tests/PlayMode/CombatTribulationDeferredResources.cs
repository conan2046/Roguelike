using System.Threading;
using System.Threading.Tasks;
using Roguelike.Core.Resources;
using Unity.Entities;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>按真实World创建边界挂起角色会话ANI；切片预加载可复用同一ANI，不能只按资源ID重复推断边界。</summary>
        private sealed class TribulationDeferredRoleResources : IResourceService
        {
            private readonly IResourceService source;
            private readonly int baselineWorlds;
            private readonly TaskCompletionSource<IRawResourceHandle> completion = new TaskCompletionSource<IRawResourceHandle>();
            public bool Blocked { get; private set; }
            public readonly LateHandle Handle = new LateHandle();

            /// <summary>测试选角就绪后绑定当前World基线与真实服务，不提前挂起共享美术预加载。</summary>
            /// <param name="source">真实YooAsset计数代理。</param><param name="baselineWorlds">进入角色前实际World数。</param>
            public TribulationDeferredRoleResources(IResourceService source, int baselineWorlds)
            { this.source = source; this.baselineWorlds = baselineWorlds; }

            /// <summary>正式Unity资源始终转发，不干预其取消与句柄所有权。</summary>
            /// <typeparam name="TAsset">资源类型。</typeparam><param name="resourceId">TbResource.id。</param><param name="cancellationToken">退出令牌。</param>
            /// <returns>真实加载任务。</returns><remarks>仅调用者持有返回句柄，本代理不缓存资产。</remarks>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class =>
                source.LoadAssetAsync<TAsset>(resourceId, cancellationToken);

            /// <summary>World已经创建后的首个原始资源请求挂起，精确覆盖部分会话取消；此前请求均走真实服务。</summary>
            /// <param name="resourceId">正式ANI资源ID。</param><param name="cancellationToken">调用者令牌。</param><returns>真实加载或受控晚到任务。</returns>
            /// <remarks>故意模拟无法立即取消的提供商IO，所有权由晚到Handle.Dispose验证。</remarks>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                if (!Blocked && World.All.Count > baselineWorlds) { Blocked = true; return completion.Task; }
                return source.LoadRawFileAsync(resourceId, cancellationToken);
            }

            /// <summary>入口取消后放行供应商结果；测试清理也可以重复调用。</summary>
            /// <remarks>只完成测试任务，不直接释放Handle，确保真实取消链承担释放责任。</remarks>
            public void Complete() { completion.TrySetResult(Handle); }
        }
    }
}
