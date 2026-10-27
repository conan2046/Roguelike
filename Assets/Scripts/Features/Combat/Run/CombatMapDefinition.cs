using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>将 TbMap 的程序化地块配置转换为已校验、可确定性采样的运行时定义。</summary>
    public sealed class CombatMapDefinition
    {
        private readonly ResourceConfig[] tileResources;
        private readonly int[] cumulativeWeights;
        private readonly int totalWeight;

        /// <summary>获取配置来源 TbMap。</summary>
        public MapConfig Map { get; }

        /// <summary>获取横向格子数量。</summary>
        public int Columns { get; }

        /// <summary>获取纵向格子数量。</summary>
        public int Rows { get; }

        /// <summary>当前布局补齐完整地块后的实际逻辑像素宽度；交互入口使用 FitViewport 派生布局。</summary>
        public int WidthPixels => checked(Columns * Map.TileSizePixels);

        /// <summary>当前布局补齐完整地块后的实际逻辑像素高度。</summary>
        public int HeightPixels => checked(Rows * Map.TileSizePixels);

        /// <summary>获取需要通过统一资源服务加载的地块纹理。</summary>
        public IReadOnlyList<ResourceConfig> TileResources => tileResources;

        /// <summary>获取整张逻辑地图包含的格子数。</summary>
        public int TileCount => checked(Columns * Rows);

        /// <summary>保存已验证的尺寸、纹理和累计权重，供渲染层无分支采样。</summary>
        /// <param name="map">Luban TbMap 行。</param>
        /// <param name="columns">横向格子数量。</param>
        /// <param name="rows">纵向格子数量。</param>
        /// <param name="tileResources">TbMap.tileTextureResourceIds 解析出的 TbResource 行。</param>
        /// <param name="cumulativeWeights">与纹理顺序一致的累计权重。</param>
        /// <param name="totalWeight">全部正权重之和。</param>
        private CombatMapDefinition(MapConfig map, int columns, int rows, ResourceConfig[] tileResources,
            int[] cumulativeWeights, int totalWeight)
        {
            Map = map;
            Columns = columns;
            Rows = rows;
            this.tileResources = tileResources;
            this.cumulativeWeights = cumulativeWeights;
            this.totalWeight = totalWeight;
        }

        /// <summary>从 TbMap 创建地图定义，并一次性验证尺寸整除、资源引用和权重对应关系。</summary>
        /// <param name="map">Luban TbMap 行。</param>
        /// <returns>可用于构建 Unity Tilemap 的只读定义。</returns>
        /// <exception cref="InvalidOperationException">地图尺寸、纹理引用或权重配置不合法。</exception>
        public static CombatMapDefinition Create(MapConfig map)
        {
            Require(map != null, "Combat map configuration is missing.");
            Require(map.TileSizePixels > 0 && map.MapWidthPixels > 0 && map.MapHeightPixels > 0,
                $"TbMap {map.Id}: tile and map pixel sizes must be positive.");
            Require(map.MapWidthPixels % map.TileSizePixels == 0 && map.MapHeightPixels % map.TileSizePixels == 0,
                $"TbMap {map.Id}: map pixel size must be divisible by tileSizePixels.");
            Require(map.CameraViewportHeightPixels > 0,
                $"TbMap {map.Id}: cameraViewportHeightPixels must be positive.");
            Require(map.TileTextureResourceIds_Ref != null && map.TileWeights != null &&
                    map.TileTextureResourceIds_Ref.Count > 0 &&
                    map.TileTextureResourceIds_Ref.Count == map.TileWeights.Count,
                $"TbMap {map.Id}: tile resources and weights must be non-empty and aligned.");

            var resources = map.TileTextureResourceIds_Ref.ToArray();
            Require(resources.All(item => item != null && item.Type == EResourceType.Texture &&
                                          !string.IsNullOrWhiteSpace(item.Path)),
                $"TbMap {map.Id}: every tile must reference a Texture in TbResource.");
            var cumulative = new int[map.TileWeights.Count];
            long sum = 0;
            for (int index = 0; index < map.TileWeights.Count; index++)
            {
                int weight = map.TileWeights[index];
                Require(weight > 0, $"TbMap {map.Id}: tile weight at index {index} must be positive.");
                sum += weight;
                Require(sum <= int.MaxValue, $"TbMap {map.Id}: tile weight total exceeds Int32.");
                cumulative[index] = (int)sum;
            }

            int columns = map.MapWidthPixels / map.TileSizePixels;
            int rows = map.MapHeightPixels / map.TileSizePixels;
            Require((long)columns * rows <= int.MaxValue, $"TbMap {map.Id}: tile count exceeds Int32.");
            return new CombatMapDefinition(map, columns, rows, resources, cumulative, (int)sum);
        }

        /// <summary>入局或视野改变时，以行走范围外各半个视野推导背景布局，两侧对称向外补齐完整地块。</summary>
        /// <param name="viewportAspect">当前摄像机可见范围的宽高比；不是设备物理像素与逻辑像素直接相加。</param>
        /// <returns>格子数不变时返回当前实例；否则返回复用纹理目录、种子和权重的只读布局。</returns>
        /// <remarks>尺寸仅消费 TbMap.arenaHalfWidthPixels、arenaHalfHeightPixels、cameraViewportHeightPixels、tileSizePixels，不修改配置或加载资源。</remarks>
        /// <exception cref="InvalidOperationException">视野比例、场地或派生尺寸非法，或超出可索引地块数量。</exception>
        public CombatMapDefinition FitViewport(double viewportAspect)
        {
            Require(!double.IsNaN(viewportAspect) && !double.IsInfinity(viewportAspect) && viewportAspect > 0,
                "Combat map viewport aspect must be finite and positive.");
            Require(Map.ArenaHalfWidthPixels > 0 && Map.ArenaHalfHeightPixels > 0,
                $"TbMap {Map.Id}: walkable arena half sizes must be positive.");
            double halfColumns = Math.Ceiling((Map.ArenaHalfWidthPixels + Map.CameraViewportHeightPixels * viewportAspect * 0.5) / Map.TileSizePixels);
            double halfRows = Math.Ceiling((Map.ArenaHalfHeightPixels + Map.CameraViewportHeightPixels * 0.5) / Map.TileSizePixels);
            Require(halfColumns > 0 && halfRows > 0 &&
                    halfColumns * Map.TileSizePixels * 2 <= int.MaxValue &&
                    halfRows * Map.TileSizePixels * 2 <= int.MaxValue && halfColumns * halfRows * 4 <= int.MaxValue,
                $"TbMap {Map.Id}: viewport-derived map dimensions exceed supported integer ranges.");
            int columns = checked((int)halfColumns * 2), rows = checked((int)halfRows * 2);
            return columns == Columns && rows == Rows ? this :
                new CombatMapDefinition(Map, columns, rows, tileResources, cumulativeWeights, totalWeight);
        }

        /// <summary>按 TbMap.generationSeed、格子坐标和 tileWeights 返回稳定的纹理索引。</summary>
        /// <param name="x">从零开始的横向格子坐标。</param>
        /// <param name="y">从零开始的纵向格子坐标。</param>
        /// <returns>TileResources 中的有效索引。</returns>
        /// <exception cref="ArgumentOutOfRangeException">坐标超出地图边界。</exception>
        public int SelectTileIndex(int x, int y)
        {
            if ((uint)x >= (uint)Columns) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(y));
            return SelectTileIndexAtCell(x, y);
        }

        /// <summary>按地图中心锚定的世界地块坐标取样，使视野改变时同一世界格子保持相同纹理。</summary>
        /// <param name="x">相对地图中心的横向地块坐标，允许负数。</param>
        /// <param name="y">相对地图中心的纵向地块坐标，允许负数。</param>
        /// <returns>由 TbMap.generationSeed 与 tileWeights 决定的纹理目录索引。</returns>
        /// <exception cref="InvalidOperationException">已验证的累计权重无法得到有效纹理。</exception>
        public int SelectTileIndexAtCell(int x, int y)
        {
            int roll = (int)(Hash(Map.GenerationSeed, x, y) % (uint)totalWeight);
            for (int index = 0; index < cumulativeWeights.Length; index++)
            {
                if (roll < cumulativeWeights[index]) return index;
            }

            throw new InvalidOperationException($"TbMap {Map.Id}: weighted tile selection failed.");
        }

        /// <summary>使用固定整数混合生成跨平台一致的格子散列，避免依赖运行时 Random 实现。</summary>
        /// <param name="seed">TbMap.generationSeed。</param>
        /// <param name="x">横向格子坐标。</param>
        /// <param name="y">纵向格子坐标。</param>
        /// <returns>用于权重采样的无符号整数。</returns>
        private static uint Hash(int seed, int x, int y)
        {
            unchecked
            {
                uint value = (uint)seed ^ (uint)x * 0x9E3779B9u ^ (uint)y * 0x85EBCA6Bu;
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                return value ^ value >> 16;
            }
        }

        /// <summary>将配置断言统一转换为带表名上下文的初始化异常。</summary>
        /// <param name="condition">必须成立的配置约束。</param>
        /// <param name="message">失败时的可定位信息。</param>
        /// <exception cref="InvalidOperationException">配置约束不成立。</exception>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
