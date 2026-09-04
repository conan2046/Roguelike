using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Roguelike.Features.Performance
{
    /// <summary>
    /// Marks an entity as owned by the command-line performance scenario.
    /// </summary>
    public struct PerformanceEntityTag : IComponentData
    {
    }

    /// <summary>
    /// Stores deterministic oscillation data for one performance-test entity.
    /// </summary>
    public struct PerformanceMotion : IComponentData
    {
        /// <summary>Gets or sets the entity's grid origin.</summary>
        public float3 Origin;

        /// <summary>Gets or sets the configured horizontal displacement.</summary>
        public float Amplitude;

        /// <summary>Gets or sets the configured angular speed.</summary>
        public float AngularSpeed;

        /// <summary>Gets or sets the deterministic phase offset.</summary>
        public float Phase;
    }

    /// <summary>
    /// Schedules parallel transform updates for entities created by a performance scenario.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PerformanceMotionSystem : ISystem
    {
        /// <summary>
        /// Called by the ECS simulation group once per frame to schedule deterministic horizontal movement.
        /// </summary>
        /// <param name="state">Current system state used to read elapsed world time and schedule work.</param>
        /// <remarks>Writes LocalTransform positions for PerformanceEntityTag entities; the job completes before transform presentation.</remarks>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new PerformanceMotionJob
            {
                ElapsedSeconds = (float)SystemAPI.Time.ElapsedTime
            };
            job.ScheduleParallel();
        }

        /// <summary>
        /// Applies one allocation-free sine-wave offset while retaining each entity's configured grid origin.
        /// </summary>
        [BurstCompile]
        private partial struct PerformanceMotionJob : IJobEntity
        {
            /// <summary>Gets or sets the current ECS world elapsed time.</summary>
            public float ElapsedSeconds;

            /// <summary>
            /// Called by the generated ECS job for each matching performance entity.
            /// </summary>
            /// <param name="transform">Writable local transform rendered by Entities Graphics.</param>
            /// <param name="motion">Read-only motion values sourced from Luban TbPerformanceScenario.</param>
            /// <param name="tag">Read-only marker that restricts execution to performance-test entities.</param>
            private void Execute(
                ref LocalTransform transform,
                in PerformanceMotion motion,
                in PerformanceEntityTag tag)
            {
                float offset = math.sin((ElapsedSeconds * motion.AngularSpeed) + motion.Phase) * motion.Amplitude;
                transform.Position = motion.Origin + new float3(offset, 0f, 0f);
            }
        }
    }
}
