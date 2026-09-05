using System;
using cfg;
using ProjectX.Migration;

namespace Roguelike.Features.Combat
{
    /// <summary>可复制进 Burst 原生容器的攻击方向与原速时序；不持有托管资源。</summary>
    public struct CombatAttackOption
    {
        public int MappingId, ActionIndex, AlignedActionIndex;
        public float X, Y;
        public bool FlipX;
        public double HitSeconds, DurationSeconds;

        /// <summary>资源服务加载 ANI 后，在主线程按攻击表 F 索引和表现时钟构建供模拟消费的快照。</summary>
        /// <param name="visual">TbVisualSet 的显式 attackClipId。</param>
        /// <param name="config">TbCombatPresentation 的时序和攻击候选列表。</param>
        /// <param name="data">资源服务按该片段 aniResourceId 加载并解析的 ANI。</param>
        /// <returns>按表内优先顺序排列的非托管选项。</returns>
        /// <exception cref="ArgumentException">片段、映射或方向不合法。</exception>
        /// <exception cref="ArgumentOutOfRangeException">动作/命中帧索引越界。</exception>
        public static CombatAttackOption[] Build(VisualSetConfig visual, CombatPresentationConfig config, CocosAniData data)
        {
            if (visual?.AttackClipId_Ref == null || visual.AttackClipId_Ref.Action != EAnimationAction.Attack || visual.AttackClipId_Ref.Loop ||
                config?.AttackDirectionIds_Ref == null || config.AttackDirectionIds_Ref.Count == 0)
                throw new ArgumentException("Missing explicit non-looping attack animation or mapping.");
            var animation = new CombatAnimation(data, config, false);
            var result = new CombatAttackOption[config.AttackDirectionIds_Ref.Count];
            for (int i = 0; i < result.Length; i++)
            {
                var map = config.AttackDirectionIds_Ref[i];
                var direction = map?.FacingDirectionId_Ref;
                if (direction == null || !config.DirectionIds.Contains(direction.Id)) throw new ArgumentException("Unresolved attack facing.");
                double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length)) throw new ArgumentException("Invalid attack facing vector.");
                result[i] = new CombatAttackOption { MappingId = map.Id, ActionIndex = map.AttackActionIndex,
                    AlignedActionIndex = direction.ActionIndex, X = (float)(direction.X / length), Y = (float)(direction.Y / length),
                    FlipX = direction.FlipX, HitSeconds = animation.FrameStartSeconds(map.AttackActionIndex, map.HitFrameIndex),
                    DurationSeconds = animation.DurationSeconds(map.AttackActionIndex) };
                for (int j = 0; j < i; j++)
                    if (result[j].MappingId == result[i].MappingId || (result[j].X == result[i].X && result[j].Y == result[i].Y))
                        throw new ArgumentException("Duplicate attack mapping.");
            }
            return result;
        }
    }
}
