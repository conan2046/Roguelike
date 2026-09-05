using System;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>沿竖直轴的圆柱移动形状；XY 为战斗地面，Elevation/Height 为独立高度区间。</summary>
    public struct CombatCylinder
    {
        public float Radius, Height, Elevation;
        public float2 Offset;

        /// <summary>入局从 TbCharacter/TbMonster 的 move*Pixels 字段换算，拒绝缺失配置。</summary>
        /// <param name="radius">半径像素。</param><param name="height">高度像素。</param>
        /// <param name="x">地面横向偏移。</param><param name="y">地面纵向偏移。</param><param name="elevation">底面离地高度。</param>
        /// <param name="scale">TbCombatRules.worldUnitsPerPixel。</param><returns>世界单位圆柱。</returns>
        /// <exception cref="InvalidOperationException">字段不完整、非有限或尺寸非正。</exception>
        public static CombatCylinder FromConfig(float? radius, float? height, float? x, float? y, float? elevation, float scale)
        {
            if (!radius.HasValue || !height.HasValue || !x.HasValue || !y.HasValue || !elevation.HasValue)
                throw new InvalidOperationException("Missing movement cylinder configuration; export the actor prefab first.");
            var shape = new CombatCylinder { Radius = radius.Value * scale, Height = height.Value * scale,
                Elevation = elevation.Value * scale, Offset = new float2(x.Value, y.Value) * scale };
            if (!(scale > 0) || !(shape.Radius > 0) || !(shape.Height > 0) ||
                !math.all(math.isfinite(new float4(shape.Radius, shape.Height, shape.Elevation, scale))) || !math.all(math.isfinite(shape.Offset)))
                throw new InvalidOperationException("Invalid movement cylinder configuration.");
            return shape;
        }

        /// <summary>移动扫掠前判断圆柱高度区间是否相交；仅底面/顶面相切不产生水平阻挡。</summary>
        /// <param name="other">另一个单位的圆柱。</param><returns>竖直区间存在正长度交集。</returns>
        public bool HeightOverlaps(in CombatCylinder other) => Elevation < other.Elevation + other.Height && other.Elevation < Elevation + Height;

        /// <summary>计算圆柱平移的首个侧面接触；初始相切允许切向和远离移动。</summary>
        /// <param name="relative">两圆柱底心差。</param><param name="motion">本次水平位移。</param>
        /// <param name="radius">半径和。</param><param name="fraction">首接触比例。</param><returns>是否阻挡本次移动。</returns>
        public static bool Sweep(float2 relative, float2 motion, float radius, out float fraction)
        {
            fraction = 0;
            if (math.dot(relative, motion) >= 0) return false;
            return CombatGeometry.Sweep(relative, relative + motion, radius, out fraction);
        }
    }
}
