using System;
using cfg;

namespace Roguelike.Features.Combat
{
    /// <summary>怪物攻击方向只读映射；攻击组和对齐方向来自 Luban，不推测文件后缀或组号。</summary>
    public sealed class CombatAttackDirections
    {
        private readonly AttackDirectionConfig[] mappings;
        private readonly double[] x, y;

        /// <summary>资源加载完成后验证 TbCombatPresentation.attackDirectionIds 及每行攻击、待机、移动动作索引。</summary>
        /// <param name="config">已解析外键的表现方案；列表顺序用于等角裁决。</param>
        /// <param name="attack">TbVisualSet.attackClipId 指定的普攻动画。</param>
        /// <param name="idle">同一表现集合的待机动画。</param>
        /// <param name="move">同一表现集合的移动动画。</param>
        /// <exception cref="ArgumentNullException">缺少依赖。</exception>
        /// <exception cref="ArgumentException">映射为空、重复、方向非法或未包含在表现方向中。</exception>
        /// <exception cref="ArgumentOutOfRangeException">表配置的动作索引不在实际资源内。</exception>
        public CombatAttackDirections(CombatPresentationConfig config, CombatAnimation attack, CombatAnimation idle, CombatAnimation move)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (attack == null) throw new ArgumentNullException(nameof(attack));
            if (idle == null) throw new ArgumentNullException(nameof(idle));
            if (move == null) throw new ArgumentNullException(nameof(move));
            if (config.AttackDirectionIds_Ref == null || config.AttackDirectionIds_Ref.Count == 0)
                throw new ArgumentException("No resolved attack directions.", nameof(config));
            mappings = config.AttackDirectionIds_Ref.ToArray();
            x = new double[mappings.Length]; y = new double[mappings.Length];
            for (int i = 0; i < mappings.Length; i++)
            {
                var mapping = mappings[i];
                var facing = mapping?.FacingDirectionId_Ref;
                if (facing == null || config.DirectionIds == null || !config.DirectionIds.Contains(facing.Id))
                    throw new ArgumentException("Missing attack facing reference.", nameof(config));
                double length = Math.Sqrt((double)facing.X * facing.X + (double)facing.Y * facing.Y);
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length))
                    throw new ArgumentException("Invalid attack direction vector.", nameof(config));
                x[i] = facing.X / length; y[i] = facing.Y / length;
                for (int previous = 0; previous < i; previous++)
                    if (mappings[previous].Id == mapping.Id || (x[previous] == x[i] && y[previous] == y[i]))
                        throw new ArgumentException("Duplicate attack direction.", nameof(config));
                attack.Sample(mapping.AttackActionIndex, 0);
                attack.FrameStartSeconds(mapping.AttackActionIndex, mapping.HitFrameIndex);
                idle.Sample(facing.ActionIndex, 0);
                move.Sample(facing.ActionIndex, 0);
            }
        }

        /// <summary>开始一次普攻时按目标方向最大点积选组；等角按表顺序，零位移保留调用方提供的有效索引。</summary>
        /// <param name="targetX">目标相对横坐标。</param>
        /// <param name="targetY">目标相对纵坐标。</param>
        /// <param name="previousIndex">先前选择；第一次应由当前朝向调用本方法取得。</param>
        /// <returns>可供 Read 读取并由调用方在本次攻击期间锁定的索引。</returns>
        /// <exception cref="ArgumentOutOfRangeException">方向非有限或历史索引非法。</exception>
        /// <remarks>不改变模拟或动画状态；调用方应在攻击开始选择一次，攻击中不要逐帧重选。</remarks>
        public int Select(float targetX, float targetY, int previousIndex)
        {
            Read(previousIndex);
            if (float.IsNaN(targetX) || float.IsInfinity(targetX) || float.IsNaN(targetY) || float.IsInfinity(targetY))
                throw new ArgumentOutOfRangeException(nameof(targetX));
            if (targetX == 0 && targetY == 0) return previousIndex;
            int selected = 0;
            double best = double.NegativeInfinity;
            for (int i = 0; i < mappings.Length; i++)
            {
                double dot = targetX * x[i] + targetY * y[i];
                if (dot > best) { selected = i; best = dot; }
            }
            return selected;
        }

        /// <summary>表现层按选定索引读取 TbAttackDirection.attackActionIndex，以及关联方向的镜像和待机/移动组。</summary>
        /// <param name="index">Select 返回的索引。</param>
        /// <returns>已验证的攻击映射行。</returns>
        /// <exception cref="ArgumentOutOfRangeException">索引非法。</exception>
        public AttackDirectionConfig Read(int index)
        {
            if ((uint)index >= (uint)mappings.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return mappings[index];
        }
    }
}
