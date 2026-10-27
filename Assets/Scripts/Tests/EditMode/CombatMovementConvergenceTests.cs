using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Roguelike.Tests
{
    /// <summary>独立保留改造前有界扫掠公式作为参照，验证循环消除不改变接触、滑动和终点。</summary>
    public sealed class CombatMovementConvergenceTests
    {
        /// <summary>逐Tick比较斜向滑动、双侧夹角、初始相切、地图边缘、密集群体及远坐标舍入。</summary>
        /// <param name="fixture">仅用于测试的几何边界编号，不是运行时配置。</param>
        /// <remarks>测试自行创建并释放World和全部原生容器；死槽仅提高旧算法上界。参照在JIT中执行，允许与Burst有不超过1e-5世界单位的浮点差异；同路径位级一致性由轨迹专项单独验证。</remarks>
        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5)]
        public void ContactBoundaryMatchesLegacyEndpoint(int fixture)
        {
            float2 start = new float2(-3, -0.5f), motion = new float2(5, 2), arena = new float2(40);
            float2[] obstacles = { float2.zero };
            if (fixture == 1) { start = new float2(0, -2); motion = new float2(0, 5); obstacles = new[] { new float2(-0.8f, 1), new float2(0.8f, 1) }; }
            if (fixture == 2) { start = new float2(-1, 0); motion = new float2(0, 2); }
            if (fixture == 3) { start = new float2(-2, 2.9f); motion = new float2(6, 1); arena = new float2(4); obstacles = new[] { new float2(0, 3) }; }
            if (fixture == 4)
            {
                start = new float2(-3, 0.2f); motion = new float2(6, 0.4f); obstacles = new float2[9];
                for (int index = 0; index < obstacles.Length; index++) obstacles[index] = new float2(index % 3, index / 3 - 1) * 1.05f;
            }
            if (fixture == 5) { start = new float2(31.0123f, 20.2001f); motion = new float2(0.0327f, 0.0191f); obstacles = new[] { new float2(32.0103f, 20.2633f) }; }
            using var world = new World("Movement convergence reference");
            var manager = world.EntityManager;
            using var units = new NativeArray<Entity>(33, Allocator.TempJob);
            using var states = new NativeArray<CombatUnit>(units.Length, Allocator.TempJob);
            using var templates = new NativeArray<CombatUnit>(units.Length, Allocator.TempJob);
            using var projectiles = new NativeArray<Entity>(0, Allocator.TempJob);
            using var options = new NativeArray<CombatAttackOption>(0, Allocator.TempJob);
            using var counters = new NativeArray<CombatCounters>(1, Allocator.TempJob);
            using var grid = new NativeParallelMultiHashMap<int2, int>(units.Length, Allocator.TempJob);
            using var requests = new NativeList<CombatDamageRequest>(Allocator.TempJob);
            using var impacts = new NativeList<CombatImpactEvent>(Allocator.TempJob);
            using var areas = new NativeList<CombatAreaEvent>(Allocator.TempJob);
            using var persistent = new NativeList<CombatPersistentArea>(Allocator.TempJob);
            using var deaths = new NativeList<CombatDeathEvent>(Allocator.TempJob);
            using var weapons = new NativeArray<CombatWeaponState>(0, Allocator.TempJob);
            var slots = units;
            float delta = 1f / 60;
            float speed = math.length(motion) / delta;
            float2 input = math.normalize(motion);
            for (int slot = 0; slot < units.Length; slot++)
            {
                slots[slot] = manager.CreateEntity(typeof(CombatUnit), typeof(LocalTransform));
                var unit = new CombatUnit { Position = slot == 0 ? start : slot <= obstacles.Length ? obstacles[slot - 1] : new float2(-30),
                    MoveSpeed = slot == 0 ? speed : 0, Cooldown = 1000, TargetSlot = -1, Radius = 0.2f,
                    Movement = CombatCylinder.FromConfig(50, 100, 0, 0, 0, 0.01f) };
                unit.Target.Health = slot <= obstacles.Length ? 100 : 0;
                unit.Target.Lifetime = (ulong)slot + 1; unit.Target.Faction = slot == 0 ? 0 : 1;
                unit.Target.IsPlayer = slot == 0;
                manager.SetComponentData(units[slot], unit);
            }
            var job = new CombatTickJob { Units = units, UnitStates = states, Projectiles = projectiles, Templates = templates,
                AttackOptions = options, Counters = counters, Grid = grid, Requests = requests, Impacts = impacts,
                Areas = areas, PersistentAreas = persistent, Deaths = deaths, PlayerWeapons = weapons,
                Arena = arena, Input = input, Delta = delta, CellSize = 1, MaximumRadius = 0.2f, TargetRefresh = 1 };
            var system = world.GetOrCreateSystemManaged<CombatTickSystem>();
            for (int tick = 0; tick < 32; tick++)
            {
                float2 expected = LegacyEndpoint(manager.GetComponentData<CombatUnit>(units[0]).Position,
                    input * speed * delta, arena, obstacles, units.Length);
                system.RunTick(job);
                float2 actual = manager.GetComponentData<CombatUnit>(units[0]).Position;
                Assert.That(math.distance(actual, expected), Is.LessThanOrEqualTo(0.00001f), "fixture " + fixture + " tick " + tick);
                Assert.That(math.cmax(math.abs(actual) + 0.5f - arena), Is.LessThanOrEqualTo(0.00001f));
                foreach (float2 obstacle in obstacles)
                    Assert.That(math.distance(actual, obstacle), Is.GreaterThanOrEqualTo(1 - 0.00001f));
            }
        }

        /// <summary>独立枚举所有静态圆形障碍，执行修复前的夹取、最早命中、ULP退让及单法线投影。</summary>
        /// <param name="position">Tick开始的位置。</param><param name="motion">速度和输入产生的原始位移。</param>
        /// <param name="arena">测试半场地。</param><param name="obstacles">全部存活静态障碍，顺序对应稳定槽位。</param>
        /// <param name="unitCount">旧求解器实际槽数上界。</param><returns>旧循环完成后的精确float终点。</returns>
        private static float2 LegacyEndpoint(float2 position, float2 motion, float2 arena, float2[] obstacles, int unitCount)
        {
            motion = math.clamp(position + motion, -arena + 0.5f, arena - 0.5f) - position;
            for (int contact = 0; contact <= unitCount && math.lengthsq(motion) > 0; contact++)
            {
                motion = math.clamp(position + motion, -arena + 0.5f, arena - 0.5f) - position;
                int selected = -1; float fraction = 1; float2 normal = float2.zero;
                for (int index = 0; index < obstacles.Length; index++)
                {
                    if (!CombatCylinder.Sweep(position - obstacles[index], motion, 1, 0, out float hit, out float2 hitNormal)) continue;
                    if (hit < fraction || (hit == fraction && (selected < 0 || index < selected)))
                    { selected = index; fraction = hit; normal = hitNormal; }
                }
                if (selected < 0) return position + motion;
                float safe = fraction > 0 ? math.asfloat(math.asint(fraction) - 1) : 0;
                position += motion * safe;
                float2 remainder = motion * (1 - fraction);
                float2 slide = remainder - normal * math.min(0, math.dot(remainder, normal));
                if (math.lengthsq(slide) >= math.lengthsq(motion)) break;
                motion = slide;
            }
            return position;
        }
    }
}
