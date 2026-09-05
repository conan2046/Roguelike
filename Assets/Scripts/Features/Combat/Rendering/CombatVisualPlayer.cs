using System;
using cfg;
using Roguelike.Features.Combat.Ecs;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>单实体动画游标，不改变战斗数据；资源与网格由整局共享。</summary>
    public sealed class CombatVisualPlayer
    {
        private readonly VisualSetConfig visual;
        private readonly CombatVisualResources resources;
        private ulong lifetime, lastTick, sequence;
        private int direction, previousClip;
        private double started;
        private bool initialized;
        public int ClipId { get; private set; }
        public int ActionIndex { get; private set; }
        public int GlobalFrame { get; private set; }
        public int MeshIndex { get; private set; }
        public int MaterialIndex { get; private set; }
        public bool FlipX { get; private set; }
        public bool Visible { get; private set; }

        /// <summary>实体绑定时从已加载待机方向初始化游标；不读取墙钟。</summary>
        /// <param name="visual">TbVisualSet 显式片段配置。</param>
        /// <param name="resources">生命周期长于播放器的共享资源。</param>
        public CombatVisualPlayer(VisualSetConfig visual, CombatVisualResources resources)
        {
            this.visual = visual; this.resources = resources;
            direction = resources.Read(visual.Id, visual.StandClipId.Value).Directions.InitialIndex;
        }

        /// <summary>固定模拟完成后按代际、移动状态和攻击进度选择共享帧；同 tick 重复调用不推进。</summary>
        /// <param name="unit">只读的 CombatSession 单位快照。</param>
        /// <param name="tick">会话已完成 tick 数，暂停时保持不变。</param>
        /// <param name="stepSeconds">TbCombatRules.simulationHz 换算的步长。</param>
        /// <remarks>仅修改本播放器游标，不改变生命、朝向或攻击计数。代际变化及 tick 回退清空播放进度。</remarks>
        public void Sample(in CombatUnit unit, ulong tick, double stepSeconds)
        {
            bool reset = !initialized || lifetime != unit.Target.Lifetime || tick < lastTick;
            if (!reset && tick == lastTick) return;
            double now = tick * stepSeconds;
            if (reset)
            {
                direction = resources.Read(visual.Id, visual.StandClipId.Value).Directions.InitialIndex;
                started = now; previousClip = 0; sequence = 0;
            }
            initialized = true; lifetime = unit.Target.Lifetime; lastTick = tick;
            Visible = unit.Target.Health > 0;
            if (!Visible) return;
            bool attacking = !unit.Target.IsPlayer && unit.AttackActive;
            bool windup = attacking && unit.AttackAge < unit.WindupSeconds;
            bool moving = math.any(unit.Position != unit.PreviousPosition);
            ClipId = attacking && !windup ? visual.AttackClipId.Value :
                moving && !attacking ? visual.MoveClipId.Value : visual.StandClipId.Value;
            var clip = resources.Read(visual.Id, ClipId);
            if (ClipId != previousClip || (attacking && sequence != unit.PendingAttack.Sequence)) started = now;
            if (attacking)
            {
                ActionIndex = windup ? unit.AttackOption.AlignedActionIndex : unit.AttackOption.ActionIndex;
                FlipX = unit.AttackOption.FlipX;
                // 离开攻击后静止仍保持已对齐朝向，不恢复起手前的五组方向。
                var idle = resources.Read(visual.Id, visual.StandClipId.Value);
                direction = idle.Directions.Select(unit.Facing.x, unit.Facing.y, direction);
            }
            else
            {
                if (moving)
                {
                    var offset = unit.Position - unit.PreviousPosition;
                    direction = clip.Directions.Select(offset.x, offset.y, direction);
                }
                else if (!unit.Target.IsPlayer)
                {
                    // 补算可能跳过一次完整攻击，怪物的模拟朝向仍是最终权威值。
                    direction = clip.Directions.Select(unit.Facing.x, unit.Facing.y, direction);
                }
                var facing = clip.Directions.Read(direction);
                ActionIndex = facing.ActionIndex; FlipX = facing.FlipX;
            }
            double age = attacking ? (windup ? unit.AttackAge : unit.AttackAge - unit.WindupSeconds) : now - started;
            FlipX ^= clip.FlipX;
            GlobalFrame = clip.Animation.Sample(ActionIndex, age);
            MeshIndex = clip.MeshIndices[GlobalFrame * 2 + (FlipX ? 1 : 0)];
            MaterialIndex = clip.MaterialIndex;
            previousClip = ClipId; sequence = attacking ? unit.PendingAttack.Sequence : 0;
        }
    }
}
