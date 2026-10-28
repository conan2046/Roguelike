using System;
using System.IO;
using System.Linq;
using System.Threading;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>真实正式关卡资源验证随身施法层的触发、移动跟随、暂停、回池和身份隔离。</summary>
    public sealed class CombatCastVisualTests
    {
        /// <summary>每种实际接入的正式技能在发射同 tick 播一次随身层，之后跟随施法者而非弹丸。</summary>
        /// <param name="skillId">正式玩家技能 ID，覆盖基础、进化与散射阵型。</param>
        /// <remarks>只创建隔离 World 与共享内存资源，Dispose 释放，不写表、Scene 或 Prefab。</remarks>
        [TestCase(20001)] [TestCase(20016)] [TestCase(20027)] [TestCase(20030)] [TestCase(20044)]
        public void CastFollowsMovingCasterAndRecyclesIndependently(int skillId)
        {
            Tables tables = LoadTables();
            CombatRunDefinition definition = CombatRunDefinition.Create(tables, 1);
            SkillConfig skill = tables.TbSkill.Get(skillId);
            var source = new CombatVisualTests.Files(tables);
            using var world = new World("Caster effect acceptance " + skillId);
            using var resources = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, definition, source, CancellationToken.None).GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, definition, resources);
            session.SynchronizePlayerSkills(new[] { skillId });
            CombatWeaponState weapon = Enumerable.Range(0, session.PlayerWeaponCount).Select(session.ReadPlayerWeapon).Single(w => w.SkillId == skillId);
            Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 123, false, out int monsterSlot), Is.True);
            CombatUnit monster = session.ReadUnit(monsterSlot);
            monster.Position = session.ReadUnit(0).Position + new float2(weapon.Range * 0.75f, 0);
            monster.PreviousPosition = monster.Position; monster.MoveSpeed = 0; monster.Cooldown = 1000;
            monster.Target.Health = monster.Target.MaxHealth = 1000000000;
            world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);

            session.Advance(session.StepSeconds, new float2(0, 1));
            visuals.Synchronize();
            Assert.That(session.CastCount, Is.EqualTo(1), "Fan/radial shots share one cast.");
            Assert.That(session.ReadCast(0).Tick, Is.EqualTo(session.Statistics.Tick));
            Assert.That(session.Statistics.ProjectileSpawns, Is.EqualTo(weapon.ProjectileCount));
            Assert.That(visuals.VisibleCastCount, Is.EqualTo(1));
            CombatVisualResources.Clip castClip = resources.Read(skillId, skill.CastClipId.Value);
            Entity castEntity = FindCastEntity(world.EntityManager, castClip.MaterialIndex);
            Assert.That(world.EntityManager.GetComponentData<LocalTransform>(castEntity).Scale, Is.EqualTo(skill.CastVisualScale.Value));
            float2 configuredOffset = new float2(skill.CastVisualOffsetXPixels.Value, skill.CastVisualOffsetYPixels.Value) * definition.CombatRules.WorldUnitsPerPixel;
            float2 firstPosition = world.EntityManager.GetComponentData<LocalTransform>(castEntity).Position.xy;
            Assert.That(math.distance(firstPosition, session.ReadUnit(0).Position + configuredOffset), Is.LessThan(1e-5f));
            visuals.Synchronize();
            Assert.That(visuals.VisibleCastCount, Is.EqualTo(1), "Repeated synchronization must not replay the event.");

            // 故意跳过重开后的 tick 0 同步，验证回到相同 tick/攻击序号也会播放新一代。
            session.Restart(); session.SynchronizePlayerSkills(new[] { skillId });
            Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 789, false, out int restartedSlot), Is.True);
            monster = session.ReadUnit(restartedSlot);
            monster.Position = session.ReadUnit(0).Position + new float2(weapon.Range * 0.75f, 0);
            monster.PreviousPosition = monster.Position; monster.MoveSpeed = 0; monster.Cooldown = 1000;
            monster.Target.Health = monster.Target.MaxHealth = 1000000000;
            world.EntityManager.SetComponentData(session.UnitEntity(restartedSlot), monster);
            session.Advance(session.StepSeconds, new float2(0, 1)); visuals.Synchronize();
            Assert.That(session.Statistics.Tick, Is.EqualTo(1));
            Assert.That(visuals.VisibleCastCount, Is.EqualTo(1), "Equal tick and sequence after restart belong to a new caster lifetime.");
            Assert.That(FindCastEntity(world.EntityManager, castClip.MaterialIndex), Is.EqualTo(castEntity));

            session.SynchronizePlayerSkills(Array.Empty<int>());
            session.Advance(session.StepSeconds, new float2(0, 1)); visuals.Synchronize();
            float2 secondPosition = world.EntityManager.GetComponentData<LocalTransform>(castEntity).Position.xy;
            Assert.That(math.distance(secondPosition, firstPosition), Is.GreaterThan(0));
            Assert.That(math.distance(secondPosition, session.ReadUnit(0).Position + configuredOffset), Is.LessThan(1e-5f));
            Assert.That(Enumerable.Range(0, session.ProjectileCapacity).Select(session.ReadProjectile)
                .Any(p => p.Active && math.distance(p.Position, secondPosition) > 0.01f), Is.True);
            session.Paused = true;
            ulong pausedTick = session.Statistics.Tick;
            MaterialMeshInfo pausedMesh = world.EntityManager.GetComponentData<MaterialMeshInfo>(castEntity);
            session.Advance(1, new float2(1, 0)); visuals.Synchronize();
            Assert.That(session.Statistics.Tick, Is.EqualTo(pausedTick));
            Assert.That(world.EntityManager.GetComponentData<MaterialMeshInfo>(castEntity).Mesh, Is.EqualTo(pausedMesh.Mesh));
            session.Paused = false;
            int durationTicks = (int)Math.Ceiling(castClip.Animation.DurationSeconds(0) / session.StepSeconds) + 1;
            for (int i = 0; i < durationTicks; i++) { session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize(); }
            Assert.That(visuals.VisibleCastCount, Is.Zero);
            Assert.That(world.EntityManager.HasComponent<DisableRendering>(castEntity), Is.True);
            session.SynchronizePlayerSkills(new[] { skillId });
            session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize();
            Assert.That(visuals.VisibleCastCount, Is.EqualTo(1));
            Assert.That(FindCastEntity(world.EntityManager, castClip.MaterialIndex), Is.EqualTo(castEntity), "Completed slot should be reused.");
            CombatUnit player = session.ReadUnit(0); player.Target.Lifetime++;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player); visuals.Synchronize();
            Assert.That(visuals.VisibleCastCount, Is.Zero, "A replacement caster must not inherit the old layer.");
            session.Restart(); visuals.Synchronize();
            Assert.That(session.CastCount, Is.Zero);
            Assert.That(visuals.VisibleCastCount, Is.Zero);
            session.SynchronizePlayerSkills(new[] { skillId });
            Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 456, false, out int nextSlot), Is.True);
            monster = session.ReadUnit(nextSlot);
            monster.Position = session.ReadUnit(0).Position + new float2(weapon.Range * 0.75f, 0);
            monster.PreviousPosition = monster.Position; monster.MoveSpeed = 0; monster.Cooldown = 1000;
            world.EntityManager.SetComponentData(session.UnitEntity(nextSlot), monster);
            session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize();
            Assert.That(visuals.VisibleCastCount, Is.EqualTo(1), "Restart must accept a new cast even though attack sequences restart.");
            player = session.ReadUnit(0); player.Target.Health = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player); visuals.Synchronize();
            Assert.That(visuals.VisibleCastCount, Is.Zero);
            visuals.Dispose();
            Assert.That(world.EntityManager.Exists(castEntity), Is.False);
            Assert.That(source.ActiveHandles, Is.Zero);
        }

        /// <summary>加载随身层时错误角色和缺失队列都明确失败，且释放已创建的资源句柄。</summary>
        /// <param name="missingQueue">为真删除绑定，否则故意让 CastClipId_Ref 指向弹丸。</param>
        [TestCase(true)] [TestCase(false)]
        public void InvalidCastBindingFailsWithoutLeakingHandles(bool missingQueue)
        {
            Tables tables = LoadTables();
            SkillConfig skill = tables.TbSkill.Get(20001);
            if (missingQueue) tables.TbCombatPresentation.Get(1).RenderConfigIds_Ref.RemoveAll(r => r.SkillId == skill.Id && r.ClipId == skill.CastClipId);
            else skill.CastClipId_Ref = skill.ProjectileClipId_Ref;
            var source = new CombatVisualTests.Files(tables);
            Assert.Throws<InvalidOperationException>(() => CombatVisualResources.LoadAsync(tables.TbPerformanceScenario.Get(4), source, CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(source.ActiveHandles, Is.Zero);
        }

        /// <summary>通过共享材质身份查找本技能随身实体，避免把命中层或弹丸误认为施法层。</summary>
        /// <param name="manager">隔离 World 的实体管理器。</param><param name="material">当前技能随身片段材质索引。</param>
        /// <returns>唯一可见的随身层实体。</returns>
        private static Entity FindCastEntity(EntityManager manager, int material)
        {
            using var all = manager.GetAllEntities(Allocator.Temp);
            return all.ToArray().Single(e => manager.HasComponent<MaterialMeshInfo>(e) &&
                !manager.HasComponent<DisableRendering>(e) &&
                MaterialMeshInfo.StaticIndexToArrayIndex(manager.GetComponentData<MaterialMeshInfo>(e).Material) == material);
        }

        /// <summary>从正式生成数据建立独立配置快照，测试不会改写业务表。</summary>
        /// <returns>已经解析强引用的 Luban 表对象。</returns>
        private static Tables LoadTables() => new Tables(n => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config/Luban", n + ".bytes"))));
    }
}
