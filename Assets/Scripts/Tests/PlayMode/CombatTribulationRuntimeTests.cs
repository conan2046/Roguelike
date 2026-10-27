using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        private YooAssetPackageInitializer tribulationTestInitializer;
        private GameObject tribulationTestRoot;
        private Action tribulationReleaseDeferred;

        /// <summary>真实YooAsset角色加载在World已创建后挂起，关闭入口必须等待晚到IO并释放全部句柄。</summary>
        /// <returns>初始化、取消、晚到结果和清理的运行时协程。</returns>
        /// <remarks>只创建测试所有者，使用当前正式入口Prefab和Luban；不会保存Scene或预览资产。</remarks>
        [UnityTest]
        public IEnumerator TribulationShutdownDrainsLateRoleLoad()
        {
            tribulationTestInitializer = new YooAssetPackageInitializer();
            yield return Wait(tribulationTestInitializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(tribulationTestInitializer); yield return Wait(config.InitializeAsync(CancellationToken.None));
            var tracking = new TribulationTrackingResources(new YooAssetResourceService(tribulationTestInitializer, config));
            int baselineWorlds = World.All.Count;
            var deferred = new TribulationDeferredRoleResources(tracking, baselineWorlds); tribulationReleaseDeferred = deferred.Complete;
            tribulationTestRoot = new GameObject("Tribulation late role load test");
            var entry = tribulationTestRoot.AddComponent<TribulationEntryController>();
            yield return Wait(entry.InitializeAsync(config.Tables, deferred, CancellationToken.None));
            entry.LobbyRoot.GetComponent<TribulationLobbyView>().RoleButtons[0].onClick.Invoke();
            double deadline = Time.realtimeSinceStartupAsDouble + 20;
            while (!deferred.Blocked && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(deferred.Blocked, Is.True, "Expected the real session ANI reload boundary.");
            Assert.That(World.All.Count, Is.GreaterThan(baselineWorlds), "The combat World may also own Entities loading worlds.");
            Task stopping = entry.ShutdownAsync();
            Assert.That(stopping.IsCompleted, Is.False, "Provider IO still owns the delayed result.");
            deferred.Complete(); yield return Wait(stopping); yield return null;
            Assert.That(deferred.Handle.Disposed, Is.True);
            Assert.That(entry.ActiveRunner, Is.Null); Assert.That(entry.LobbyRoot, Is.Null);
            Assert.That(tracking.Outstanding, Is.Zero); Assert.That(World.All.Count, Is.EqualTo(baselineWorlds));
        }

        /// <summary>真实角色首个纹理加载失败时返回选角并释放部分资源；同一入口可重新选择并成功入局。</summary>
        /// <returns>失败、重试、返回及释放的运行时协程。</returns>
        /// <remarks>仅代理一次故障，不修改正式资源；成功重试仍使用真实YooAsset与独立World。</remarks>
        [UnityTest]
        public IEnumerator TribulationRoleFailureAllowsRetryWithoutLeaking()
        {
            tribulationTestInitializer = new YooAssetPackageInitializer();
            yield return Wait(tribulationTestInitializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(tribulationTestInitializer); yield return Wait(config.InitializeAsync(CancellationToken.None));
            var tracking = new TribulationTrackingResources(new YooAssetResourceService(tribulationTestInitializer, config)) { FailNextTexture = true };
            int baselineWorlds = World.All.Count;
            tribulationTestRoot = new GameObject("Tribulation retry ownership test");
            var entry = tribulationTestRoot.AddComponent<TribulationEntryController>();
            yield return Wait(entry.InitializeAsync(config.Tables, tracking, CancellationToken.None));
            var lobby = entry.LobbyRoot.GetComponent<TribulationLobbyView>();
            LogAssert.Expect(LogType.Exception, new Regex("Injected Tribulation texture load failure"));
            lobby.RoleButtons[0].onClick.Invoke();
            double deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((tracking.FailNextTexture || entry.ActiveRunner != null || !lobby.RoleButtons.All(b => b.interactable)) &&
                Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(tracking.FailNextTexture, Is.False); Assert.That(entry.ActiveRunner, Is.Null);
            Assert.That(entry.LobbyRoot.activeInHierarchy, Is.True);
            Assert.That(lobby.ErrorText.text, Is.EqualTo(config.Tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry).ErrorText));
            Assert.That(tracking.Outstanding, Is.EqualTo(1), "Only the reused lobby Prefab should be owned.");
            Assert.That(World.All.Count, Is.EqualTo(baselineWorlds));
            lobby.RoleButtons[0].onClick.Invoke(); deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((entry.ActiveRunner == null || !entry.ActiveRunner.Ready) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(entry.ActiveRunner != null && entry.ActiveRunner.Ready, Is.True);
            Assert.That(entry.LobbyRoot.activeInHierarchy, Is.False);
            entry.ReturnToLobby(); yield return null;
            Assert.That(entry.ActiveRunner, Is.Null); Assert.That(entry.LobbyRoot.activeInHierarchy, Is.True);
            Assert.That(World.All.Count, Is.EqualTo(baselineWorlds)); Assert.That(tracking.Outstanding, Is.EqualTo(1));
            yield return Wait(entry.ShutdownAsync()); yield return null;
            Assert.That(tracking.Outstanding, Is.Zero);
        }

        /// <summary>每项测试结束先取消入口，再放行晚到结果并排空，最后才释放真实资源服务。</summary>
        /// <returns>允许取消后的Unity同步上下文完成清理的协程。</returns>
        /// <remarks>对原有运行时测试为空操作；失败路径也不遗留World、输入系统或YooAsset句柄。</remarks>
        [UnityTearDown]
        public IEnumerator CleanupTribulationRuntimeFixture()
        {
            try
            {
                if (tribulationTestRoot != null)
                {
                    var entry = tribulationTestRoot.GetComponent<TribulationEntryController>(); entry.Close();
                    tribulationReleaseDeferred?.Invoke(); yield return Wait(entry.ShutdownAsync());
                    UnityEngine.Object.Destroy(tribulationTestRoot); yield return null;
                }
            }
            finally
            {
                if (tribulationTestRoot != null) UnityEngine.Object.Destroy(tribulationTestRoot);
                tribulationTestRoot = null; tribulationReleaseDeferred = null;
                tribulationTestInitializer?.Dispose(); tribulationTestInitializer = null;
            }
        }

        /// <summary>真实服务的所有权计数代理；故障只用于覆盖部分加载恢复边界。</summary>
        private sealed class TribulationTrackingResources : IResourceService
        {
            private readonly IResourceService source;
            public int Outstanding { get; private set; }
            public bool FailNextTexture { get; set; }
            /// <summary>保存真实服务；不接管其包生命周期。</summary><param name="source">已初始化的正式服务。</param>
            public TribulationTrackingResources(IResourceService source) { this.source = source; }

            /// <summary>转发真实资源加载后计数；可在首个纹理前注入一次受控失败。</summary>
            /// <typeparam name="TAsset">正式资源类型。</typeparam><param name="resourceId">TbResource.id。</param><param name="cancellationToken">退出令牌。</param>
            /// <returns>持有真实底层句柄的计数包装。</returns><remarks>每次成功返回均由对应包装Dispose减回一次。</remarks>
            /// <exception cref="InvalidOperationException">测试要求的首个纹理故障。</exception><exception cref="OperationCanceledException">正式加载取消。</exception>
            public async Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class
            {
                if (FailNextTexture && typeof(TAsset) == typeof(Texture2D))
                { FailNextTexture = false; throw new InvalidOperationException("Injected Tribulation texture load failure"); }
                var handle = await source.LoadAssetAsync<TAsset>(resourceId, cancellationToken); Outstanding++;
                return new TribulationAssetHandle<TAsset>(handle, Released);
            }

            /// <summary>转发真实ANI读取并计数；不修改字节或解析行为。</summary>
            /// <param name="resourceId">TbResource.id。</param><param name="cancellationToken">退出令牌。</param><returns>真实原始句柄包装。</returns>
            /// <remarks>只记录成功取得的所有权；底层取消前创建的内部句柄仍由真实服务归还。</remarks>
            /// <exception cref="OperationCanceledException">正式加载取消。</exception>
            public async Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                var handle = await source.LoadRawFileAsync(resourceId, cancellationToken); Outstanding++;
                return new TribulationRawHandle(handle, Released);
            }

            /// <summary>一个包装成功释放底层所有权后减少计数。</summary><remarks>仅改变本测试代理，不操作Unity资源或全局状态。</remarks>
            private void Released() { Outstanding--; }
        }

        private sealed class TribulationAssetHandle<TAsset> : IResourceHandle<TAsset> where TAsset : class
        {
            private IResourceHandle<TAsset> source; private readonly Action released;
            public TAsset Asset => source.Asset;
            /// <summary>接管一个已经取得的真实资源句柄。</summary><param name="source">底层所有权。</param><param name="released">归还计数回调。</param>
            public TribulationAssetHandle(IResourceHandle<TAsset> source, Action released) { this.source = source; this.released = released; }
            /// <summary>幂等归还底层资源并记录一次释放。</summary><remarks>不销毁共享资产对象，销毁策略属于真实服务。</remarks>
            public void Dispose() { if (source == null) return; source.Dispose(); source = null; released(); }
        }

        private sealed class TribulationRawHandle : IRawResourceHandle
        {
            private IRawResourceHandle source; private readonly Action released;
            public byte[] Data => source.Data;
            /// <summary>接管真实ANI句柄。</summary><param name="source">底层所有权。</param><param name="released">归还计数回调。</param>
            public TribulationRawHandle(IRawResourceHandle source, Action released) { this.source = source; this.released = released; }
            /// <summary>幂等释放原始字节所有权并记录一次释放。</summary><remarks>不保存字节副本或创建预览资源。</remarks>
            public void Dispose() { if (source == null) return; source.Dispose(); source = null; released(); }
        }
    }
}
