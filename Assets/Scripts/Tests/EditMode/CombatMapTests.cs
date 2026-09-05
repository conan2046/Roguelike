using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Roguelike.Tests
{
    /// <summary>验证草地地图配置、确定性生成和 Unity Tilemap 分块渲染生命周期。</summary>
    public sealed class CombatMapTests
    {
        /// <summary>读取正式 Luban bytes，确认 4096 地图被解析为 32×32 格且六种纹理可确定性选择。</summary>
        [Test]
        public void MapDefinition_UsesConfiguredDimensionsResourcesAndSeed()
        {
            Tables tables = LoadTables();
            CombatMapDefinition first = CombatMapDefinition.Create(tables.TbMap.Get(1));
            CombatMapDefinition second = CombatMapDefinition.Create(tables.TbMap.Get(1));

            Assert.That(first.Columns, Is.EqualTo(32));
            Assert.That(first.Rows, Is.EqualTo(32));
            Assert.That(first.TileCount, Is.EqualTo(1024));
            Assert.That(first.Map.CameraViewportHeightPixels, Is.EqualTo(750));
            Assert.That(first.TileResources.Select(item => item.Id),
                Is.EqualTo(new[] { 420044, 420045, 420046, 420047, 420048, 420049 }));
            int[] firstLayout = Enumerable.Range(0, first.TileCount)
                .Select(index => first.SelectTileIndex(index % first.Columns, index / first.Columns)).ToArray();
            int[] secondLayout = Enumerable.Range(0, second.TileCount)
                .Select(index => second.SelectTileIndex(index % second.Columns, index / second.Columns)).ToArray();
            Assert.That(secondLayout, Is.EqualTo(firstLayout));
            Assert.That(firstLayout.Distinct().OrderBy(value => value), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5 }));
            Assert.That(firstLayout.Count(value => value == 0), Is.GreaterThan(firstLayout.Count(value => value == 5)));
        }

        /// <summary>用同步完成的内存纹理任务创建完整地图，确认仅有一张 Tilemap、Chunk 模式和释放句柄计数。</summary>
        [Test]
        public void TilemapBackground_BuildsOneChunkRenderedMapAndReleasesResources()
        {
            Tables tables = LoadTables();
            CombatMapDefinition definition = CombatMapDefinition.Create(tables.TbMap.Get(1));
            var owner = new GameObject("Combat Map Test Owner");
            using var resources = new MemoryTextureResources(definition.Map.TileSizePixels);
            CombatTilemapBackground background = null;
            try
            {
                background = CombatTilemapBackground.CreateAsync(definition, tables.TbCombatRules.Get(1),
                    resources, owner.transform, CancellationToken.None).GetAwaiter().GetResult();

                Assert.That(background.TileCount, Is.EqualTo(1024));
                Assert.That(background.Root.transform.childCount, Is.EqualTo(1));
                Assert.That(background.Tilemap.cellBounds.size, Is.EqualTo(new Vector3Int(32, 32, 1)));
                Assert.That(background.Tilemap.GetComponent<TilemapRenderer>().mode, Is.EqualTo(TilemapRenderer.Mode.Chunk));
                Assert.That(background.Tilemap.GetUsedTilesCount(), Is.EqualTo(6));
            }
            finally
            {
                background?.Dispose();
                Object.DestroyImmediate(owner);
            }

            Assert.That(resources.ReleaseCount, Is.EqualTo(6));
        }

        /// <summary>从 Assets/StreamingAssets/Config/Luban 装载测试所需的正式生成数据。</summary>
        /// <returns>完成全部跨表引用解析的 Luban 表集合。</returns>
        private static Tables LoadTables()
        {
            return new Tables(name => new ByteBuf(File.ReadAllBytes(
                Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
        }

        /// <summary>为 EditMode 测试按请求创建相同尺寸的内存 Texture2D，并统计句柄释放。</summary>
        private sealed class MemoryTextureResources : IResourceService, IDisposable
        {
            private readonly int tileSize;
            private readonly List<Texture2D> textures = new List<Texture2D>();

            /// <summary>获取已释放的模拟资源句柄数量。</summary>
            public int ReleaseCount { get; private set; }

            /// <summary>保存 TbMap.tileSizePixels，保证测试纹理符合正式尺寸约束。</summary>
            /// <param name="tileSize">正方形纹理边长。</param>
            public MemoryTextureResources(int tileSize)
            {
                this.tileSize = tileSize;
            }

            /// <summary>为每个 TbResource ID 创建一张独立内存纹理并返回可计数释放句柄。</summary>
            /// <typeparam name="TAsset">测试只接受 Texture2D。</typeparam>
            /// <param name="resourceId">TbResource.id，用于纹理命名。</param>
            /// <param name="cancellationToken">调用前检查的取消令牌。</param>
            /// <returns>立即完成的纹理句柄任务。</returns>
            /// <exception cref="NotSupportedException">请求的不是 Texture2D。</exception>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId,
                CancellationToken cancellationToken) where TAsset : class
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (typeof(TAsset) != typeof(Texture2D)) throw new NotSupportedException(typeof(TAsset).FullName);
                var texture = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false)
                {
                    name = "Tile " + resourceId
                };
                textures.Add(texture);
                IResourceHandle<Texture2D> handle = new TextureHandle(this, texture);
                return Task.FromResult((IResourceHandle<TAsset>)(object)handle);
            }

            /// <summary>拒绝本测试不需要的原始文件读取路径。</summary>
            /// <param name="resourceId">未使用的资源 ID。</param>
            /// <param name="cancellationToken">未使用的取消令牌。</param>
            /// <returns>此实现不会返回任务结果。</returns>
            /// <exception cref="NotSupportedException">始终抛出。</exception>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            /// <summary>在地图句柄释放后销毁测试资源服务拥有的全部 Texture2D。</summary>
            public void Dispose()
            {
                foreach (Texture2D texture in textures) Object.DestroyImmediate(texture);
                textures.Clear();
            }

            /// <summary>记录一张模拟纹理的所有权，并将释放事件回写到资源服务。</summary>
            private sealed class TextureHandle : IResourceHandle<Texture2D>
            {
                private MemoryTextureResources owner;

                /// <summary>获取句柄存活期间的内存纹理。</summary>
                public Texture2D Asset { get; }

                /// <summary>创建可重复安全释放的内存纹理句柄。</summary>
                /// <param name="owner">统计释放数量的资源服务。</param>
                /// <param name="asset">资源服务拥有的纹理。</param>
                public TextureHandle(MemoryTextureResources owner, Texture2D asset)
                {
                    this.owner = owner;
                    Asset = asset;
                }

                /// <summary>首次释放时递增资源服务计数；纹理由服务最终销毁。</summary>
                public void Dispose()
                {
                    if (owner == null) return;
                    owner.ReleaseCount++;
                    owner = null;
                }
            }
        }
    }
}
