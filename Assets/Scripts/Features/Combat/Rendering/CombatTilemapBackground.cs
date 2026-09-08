using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>加载 TbMap 地块纹理并创建仅存在于本局生命周期内的分块 Tilemap 背景。</summary>
    public sealed class CombatTilemapBackground : IDisposable
    {
        private const float BackgroundDepth = 1f;
        private readonly List<IResourceHandle<Texture2D>> textureHandles = new List<IResourceHandle<Texture2D>>();
        private readonly List<Sprite> sprites = new List<Sprite>();
        private readonly List<Tile> tiles = new List<Tile>();
        private bool disposed;

        /// <summary>获取本局动态创建且不会保存到 Scene 或 Prefab 的地图根对象。</summary>
        public GameObject Root { get; private set; }

        /// <summary>获取用于 Unity 分块裁剪和批处理的 Tilemap。</summary>
        public Tilemap Tilemap { get; private set; }

        /// <summary>获取实际渲染格子数。</summary>
        public int TileCount { get; private set; }

        /// <summary>限制外部直接构造，确保异步资源和 Unity 对象由同一个生命周期所有者管理。</summary>
        private CombatTilemapBackground()
        {
        }

        /// <summary>通过 TbResource ID 加载地块并一次性建立完整逻辑地图，后续由 TilemapRenderer 自动按区块裁剪。</summary>
        /// <param name="definition">从 TbMap 创建的已校验地图定义。</param>
        /// <param name="rules">提供 worldUnitsPerPixel 的 TbCombatRules。</param>
        /// <param name="resources">统一资源服务，返回的句柄由地图持有至 Dispose。</param>
        /// <param name="parent">本局运行器根节点。</param>
        /// <param name="token">组合根退出令牌。</param>
        /// <returns>纹理、Tile、Tilemap 全部创建完成的背景。</returns>
        /// <remarks>创建 Grid、Tilemap、运行时 Sprite 和 Tile；不写入 Scene/Prefab，不创建磁盘预览资产。</remarks>
        /// <exception cref="ArgumentNullException">依赖为空。</exception>
        /// <exception cref="InvalidOperationException">像素比例、纹理尺寸或资源类型不符合 TbMap。</exception>
        /// <exception cref="OperationCanceledException">加载期间取消。</exception>
        public static async Task<CombatTilemapBackground> CreateAsync(CombatMapDefinition definition,
            CombatRulesConfig rules, IResourceService resources, Transform parent, CancellationToken token)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            float worldUnitsPerPixel = rules.WorldUnitsPerPixel;
            if (!(worldUnitsPerPixel > 0f))
                throw new InvalidOperationException($"TbCombatRules {rules.Id}: worldUnitsPerPixel must be positive.");

            var result = new CombatTilemapBackground();
            try
            {
                foreach (ResourceConfig resource in definition.TileResources)
                {
                    token.ThrowIfCancellationRequested();
                    IResourceHandle<Texture2D> handle = await resources.LoadAssetAsync<Texture2D>(resource.Id, token);
                    result.textureHandles.Add(handle);
                    Texture2D texture = handle.Asset;
                    if (texture == null || texture.width != definition.Map.TileSizePixels ||
                        texture.height != definition.Map.TileSizePixels)
                    {
                        throw new InvalidOperationException(
                            $"TbResource {resource.Id}: tile texture must be {definition.Map.TileSizePixels}x{definition.Map.TileSizePixels}.");
                    }
                }

                token.ThrowIfCancellationRequested();
                result.Build(definition, worldUnitsPerPixel, parent);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>将已加载纹理转换为共享 Tile，并以单次 SetTilesBlock 填充 TbMap 指定的格子范围。</summary>
        /// <param name="definition">确定性地块选择和地图尺寸定义。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel 直接比例值。</param>
        /// <param name="parent">本局运行器根节点。</param>
        /// <remarks>只生成两个 GameObject；1024 个逻辑格不生成独立 Transform 或 SpriteRenderer。</remarks>
        private void Build(CombatMapDefinition definition, float worldUnitsPerPixel, Transform parent)
        {
            float cellWorldSize = definition.Map.TileSizePixels * worldUnitsPerPixel;
            Root = new GameObject("[Roguelike.Map]");
            Root.transform.SetParent(parent, false);
            Root.transform.localPosition = new Vector3(
                -definition.Map.MapWidthPixels * worldUnitsPerPixel * 0.5f,
                -definition.Map.MapHeightPixels * worldUnitsPerPixel * 0.5f,
                BackgroundDepth);
            Grid grid = Root.AddComponent<Grid>();
            grid.cellLayout = GridLayout.CellLayout.Rectangle;
            grid.cellSize = new Vector3(cellWorldSize, cellWorldSize, 1f);

            var layer = new GameObject("Ground");
            layer.transform.SetParent(Root.transform, false);
            Tilemap = layer.AddComponent<Tilemap>();
            Tilemap.tileAnchor = new Vector3(0.5f, 0.5f, 0f);
            TilemapRenderer renderer = layer.AddComponent<TilemapRenderer>();
            renderer.mode = TilemapRenderer.Mode.Chunk;
            renderer.sortingOrder = -1000;

            float pixelsPerUnit = 1f / worldUnitsPerPixel;
            foreach (IResourceHandle<Texture2D> handle in textureHandles)
            {
                Texture2D texture = handle.Asset;
                Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
                sprite.name = texture.name + " Runtime Sprite";
                sprites.Add(sprite);
                Tile tile = ScriptableObject.CreateInstance<Tile>();
                tile.name = texture.name + " Runtime Tile";
                tile.sprite = sprite;
                tile.colliderType = Tile.ColliderType.None;
                tiles.Add(tile);
            }

            var tileValues = new TileBase[definition.TileCount];
            int offset = 0;
            for (int y = 0; y < definition.Rows; y++)
            {
                for (int x = 0; x < definition.Columns; x++)
                {
                    tileValues[offset++] = tiles[definition.SelectTileIndex(x, y)];
                }
            }

            Tilemap.SetTilesBlock(new BoundsInt(0, 0, 0, definition.Columns, definition.Rows, 1), tileValues);
            TileCount = tileValues.Length;
        }

        /// <summary>销毁运行时 Grid、Sprite、Tile，并释放全部 YooAsset 纹理句柄。</summary>
        /// <remarks>幂等；必须在统一资源服务销毁前调用。</remarks>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Tilemap != null)
            {
                TilemapRenderer renderer = Tilemap.GetComponent<TilemapRenderer>();
                if (renderer != null) renderer.enabled = false;
                Tilemap.ClearAllTiles();
            }
            DestroyOwned(Root);
            Root = null;
            Tilemap = null;
            foreach (Tile tile in tiles) DestroyOwned(tile);
            tiles.Clear();
            foreach (Sprite sprite in sprites) DestroyOwned(sprite);
            sprites.Clear();
            foreach (IResourceHandle<Texture2D> handle in textureHandles) handle.Dispose();
            textureHandles.Clear();
            TileCount = 0;
        }

        /// <summary>在运行模式延迟销毁 Unity 对象，在 EditMode 测试中立即回收内存对象。</summary>
        /// <param name="value">本地图创建并独占的 Unity 对象。</param>
        private static void DestroyOwned(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
