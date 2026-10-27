using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
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
        /// <param name="job">本局同槽状态缓冲与参数；仅采集/写回使用的组件lookup由本系统填充。</param>
        /// <remarks>任务在返回前完成，可安全读计数或在同帧发起下一 tick。</remarks>
        /// <exception cref="System.InvalidOperationException">会话状态缓冲未创建或与实体槽数不一致。</exception>
        public void RunTick(CombatTickJob job)
        {
            if (!job.UnitStates.IsCreated || job.UnitStates.Length != job.Units.Length)
                throw new System.InvalidOperationException("Combat unit state buffer must match the session slots.");
            work = job;
            Update();
        }

        /// <summary>表现同步开始时从权威ECS批量复制单位，完成依赖后供当前同步帧共享只读读取。</summary>
        /// <param name="entities">会话持有的稳定单位槽。</param><param name="destination">会话持有且长度匹配的表现缓冲。</param>
        /// <remarks>完成只读任务后立即返回；不借用可能过期的模拟工作缓冲，不修改战斗状态。</remarks>
        public void CapturePresentationUnits(NativeArray<Entity> entities, NativeArray<CombatUnit> destination)
        {
            var capture = new PresentationCaptureJob
            {
                Entities = entities, Destination = destination, Units = GetComponentLookup<CombatUnit>(true)
            };
            Dependency = capture.Schedule(Dependency);
            Dependency.Complete();
        }

        /// <summary>使用本次更新的只读lookup顺序采集权威单位；仅同步帧边界调度。</summary>
        [BurstCompile]
        private struct PresentationCaptureJob : IJob
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public ComponentLookup<CombatUnit> Units;
            [WriteOnly] public NativeArray<CombatUnit> Destination;

            /// <summary>依赖完成后按会话槽序复制全部字段；调用者等待后才借出只读视图。</summary>
            public void Execute()
            {
                for (int slot = 0; slot < Entities.Length; slot++) Destination[slot] = Units[Entities[slot]];
            }
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
