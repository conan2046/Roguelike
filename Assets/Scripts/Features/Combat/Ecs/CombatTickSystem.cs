using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>显式驱动的 ECS 依赖桥；不自动注册逐帧循环，避免绕过会话固定步进。</summary>
    [DisableAutoCreation]
    public partial class CombatTickSystem : SystemBase
    {
        private CombatTickJob work;

        /// <summary>会话需要一个固定 tick 时设置原生输入并走标准系统依赖更新。</summary>
        /// <param name="job">本局容器与参数；组件 lookup 由本系统填充。</param>
        /// <remarks>任务在返回前完成，可安全读计数或在同帧发起下一 tick。</remarks>
        public void RunTick(CombatTickJob job)
        {
            work = job;
            Update();
        }

        /// <summary>仅由 RunTick 显式触发，获取当次有效 lookup 并调度 Burst 任务。</summary>
        /// <remarks>注册组件读写依赖并等待完成；不在任务执行中进行结构变更。</remarks>
        protected override void OnUpdate()
        {
            work.UnitData = GetComponentLookup<CombatUnit>();
            work.ProjectileData = GetComponentLookup<CombatProjectile>();
            work.Transforms = GetComponentLookup<LocalTransform>();
            Dependency = work.Schedule(Dependency);
            Dependency.Complete();
        }
    }
}
