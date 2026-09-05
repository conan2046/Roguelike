using System;
using System.IO;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat.Ecs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>圆柱配置与扫掠边界测试；使用真实表及隔离世界，不修改场景或源数据。</summary>
    public sealed class CombatCylinderTests
    {
        /// <summary>高速横穿必须找到首次接触，切向和离开已有接触不能被误阻挡。</summary>
        [Test]
        public void SweepBlocksCrossingAndAllowsLeavingContact()
        {
            Assert.That(CombatCylinder.Sweep(new float2(-2, 0), new float2(4, 0), 1, out float fraction), Is.True);
            Assert.That(fraction, Is.EqualTo(0.25f).Within(1e-6));
            Assert.That(CombatCylinder.Sweep(new float2(-1, 0), new float2(-1, 0), 1, out _), Is.False);
            Assert.That(CombatCylinder.Sweep(new float2(-1, 0), new float2(0, 1), 1, out _), Is.False);
        }

        /// <summary>圆柱高度区间决定阻挡，水平相同但竖直分离时不碰撞；配置缺失必须拒绝。</summary>
        [Test]
        public void CylinderHeightAndPixelConversionAreIndependent()
        {
            var a = CombatCylinder.FromConfig(20, 100, 10, -20, 0, 0.01f);
            var b = CombatCylinder.FromConfig(20, 100, 0, 0, 100, 0.01f);
            Assert.That(a.Radius, Is.EqualTo(0.2f).Within(1e-6));
            Assert.That(math.distance(a.Offset, new float2(0.1f, -0.2f)), Is.LessThan(1e-6));
            Assert.That(a.HeightOverlaps(b), Is.False);
            b.Elevation = 0.5f; Assert.That(a.HeightOverlaps(b), Is.True);
            Assert.Throws<InvalidOperationException>(() => CombatCylinder.FromConfig(null, 1, 0, 0, 0, 1));
        }

        /// <summary>隔离 Job 中玩家高速冲向多个怪物，必须停在外缘且高度分离后允许通过。</summary>
        [Test]
        public void PlayerCannotTunnelThroughMonsterCylinder()
        {
            using var world = new World("Cylinder movement fixture");
            var manager = world.EntityManager;
            using var units = new NativeArray<Entity>(2, Allocator.TempJob);
            using var projectiles = new NativeArray<Entity>(0, Allocator.TempJob);
            using var templates = new NativeArray<CombatUnit>(2, Allocator.TempJob);
            using var options = new NativeArray<Roguelike.Features.Combat.CombatAttackOption>(0, Allocator.TempJob);
            using var counters = new NativeArray<CombatCounters>(1, Allocator.TempJob);
            using var grid = new NativeParallelMultiHashMap<int2,int>(2, Allocator.TempJob);
            using var requests = new NativeList<CombatDamageRequest>(Allocator.TempJob);
            using var impacts = new NativeList<CombatImpactEvent>(Allocator.TempJob);
            var unitSlots = units;
            for (int slot = 0; slot < 2; slot++)
            {
                unitSlots[slot] = manager.CreateEntity(typeof(CombatUnit), typeof(Unity.Transforms.LocalTransform));
                var u = new CombatUnit { Position = new float2(slot == 0 ? -2 : 0, 0), MoveSpeed = slot == 0 ? 240 : 0,
                    Cooldown = 1000, TargetSlot = -1, Radius = 0.2f, Movement = CombatCylinder.FromConfig(50, 100, 0, 0, 0, 0.01f) };
                u.Target.Health = 100; u.Target.Lifetime = (ulong)(slot + 1); u.Target.Faction = slot;
                manager.SetComponentData(units[slot], u);
            }
            var job = new CombatTickJob { Units=units, Projectiles=projectiles, Templates=templates, AttackOptions=options,
                Counters=counters, Grid=grid, Requests=requests, Impacts=impacts, Arena=new float2(10), Input=new float2(1,0), Delta=1f/60,
                CellSize=1, MaximumRadius=0.2f, MaximumMotion=4, TargetRefresh=1 };
            var system = world.GetOrCreateSystemManaged<CombatTickSystem>();
            system.RunTick(job);
            Assert.That(manager.GetComponentData<CombatUnit>(units[0]).Position.x, Is.EqualTo(-1).Within(1e-5));
            var player=manager.GetComponentData<CombatUnit>(units[0]); player.Movement.Elevation=2; manager.SetComponentData(units[0],player);
            system.RunTick(job);
            Assert.That(manager.GetComponentData<CombatUnit>(units[0]).Position.x, Is.GreaterThan(0));
        }
    }
}
