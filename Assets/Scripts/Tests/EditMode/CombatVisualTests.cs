using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>真实配置与美术驱动的播放、共享资源和实体绑定回归。</summary>
    public sealed class CombatVisualTests
    {
        /// <summary>模拟编辑器先回收 ECS World，再触发入口关闭；表现和会话必须安全释放且可重复关闭。</summary>
        /// <remarks>使用真实配置与内存美术资源；finally 清理测试独占世界，不保存 Scene 或 Prefab。</remarks>
        [Test]
        public void DisposeAfterWorldShutdownDoesNotAccessEntityManager()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            var world = new World("World shutdown before visuals");
            try
            {
                using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
                using var visuals = new CombatEntityVisuals(world, session, scenario, assets);
                world.Dispose();
                Assert.DoesNotThrow(() => visuals.Dispose());
                Assert.DoesNotThrow(() => visuals.Dispose());
                Assert.DoesNotThrow(() => session.Dispose());
            }
            finally { if (world.IsCreated) world.Dispose(); }
        }

        /// <summary>真实定时刷怪扩容后绑定新增槽，复用共享网格，并在重开后隐藏全部旧怪物。</summary>
        [Test]
        public void TimedSpawnBindsGrowingEntitiesAndHidesOnRestart()
        {
            var tables = LoadTables(true); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var world = new World("Timed rendering growth");
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, scenario, assets);
            int loads = source.Loads;
            int waveTicks = (int)Math.Ceiling(((decimal)scenario.SpawnIntervalSeconds.Value + (scenario.SpawnBatchCount.Value - 1) * (decimal)scenario.SpawnUnitIntervalSeconds.Value) * scenario.CombatRulesId_Ref.SimulationHz);
            for (int i = 0; i < waveTicks * 2; i++)
            {
                for (int slot = 0; slot < session.UnitCount; slot++)
                { var u = session.ReadUnit(slot); u.MoveSpeed = 0; u.Cooldown = 1000; world.EntityManager.SetComponentData(session.UnitEntity(slot), u); }
                session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize();
            }
            Assert.That(session.Statistics.AliveMonsters, Is.EqualTo(2 * scenario.SpawnBatchCount.Value));
            Assert.That(source.Loads, Is.EqualTo(loads));
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                Assert.That(visuals.Read(slot).Visible, Is.True);
                Assert.That(world.EntityManager.HasComponent<MaterialMeshInfo>(session.UnitEntity(slot)), Is.True);
            }
            session.Restart(); visuals.Synchronize();
            for (int slot = 1; slot < session.UnitCount; slot++)
                Assert.That(world.EntityManager.HasComponent<DisableRendering>(session.UnitEntity(slot)), Is.True);
        }

        /// <summary>仅实际生命下降的单位切换共享白闪材质，连续受击刷新持续时间并在配置时长后恢复。</summary>
        [Test]
        public void ActualHealthLossFlashesOnlyDamagedSlotAndRestores()
        {
            var tables = LoadTables();
            PerformanceScenarioConfig scenario = tables.TbPerformanceScenario.Get(4);
            var source = new Files(tables);
            using var world = new World("Actual health loss hit flash");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, scenario, assets);
            Assert.That(session.UnitCount, Is.GreaterThan(1));
            for (int slot = 0; slot < session.UnitCount; slot++)
            {
                CombatUnit unit = session.ReadUnit(slot);
                unit.MoveSpeed = 0;
                unit.Cooldown = 1000;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }

            CombatVisualPlayer damagedPlayer = visuals.Read(1);
            Assert.That(damagedPlayer.HitFlashMaterialIndex, Is.Not.EqualTo(damagedPlayer.MaterialIndex));
            Color flashColor = assets.Materials[damagedPlayer.HitFlashMaterialIndex].GetColor("_FlashColor");
            Assert.That(flashColor.r, Is.EqualTo(scenario.PresentationId_Ref.HitFlashR).Within(0.001f));
            Assert.That(flashColor.g, Is.EqualTo(scenario.PresentationId_Ref.HitFlashG).Within(0.001f));
            Assert.That(flashColor.b, Is.EqualTo(scenario.PresentationId_Ref.HitFlashB).Within(0.001f));
            Assert.That(flashColor.a, Is.EqualTo(0.5f).Within(0.001f));
            CombatUnit damaged = session.ReadUnit(1);
            damaged.Target.Health--;
            world.EntityManager.SetComponentData(session.UnitEntity(1), damaged);
            visuals.Synchronize();
            Assert.That(visuals.IsHitFlashing(1), Is.True);
            Assert.That(visuals.IsHitFlashing(0), Is.False);
            MaterialMeshInfo flashInfo = world.EntityManager.GetComponentData<MaterialMeshInfo>(session.UnitEntity(1));
            Assert.That(MaterialMeshInfo.StaticIndexToArrayIndex(flashInfo.Material), Is.EqualTo(damagedPlayer.HitFlashMaterialIndex));

            int durationTicks = (int)Math.Ceiling(scenario.PresentationId_Ref.HitFlashDurationSeconds / session.StepSeconds);
            int firstPart = durationTicks / 2;
            for (int tick = 0; tick < firstPart; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero);
                visuals.Synchronize();
            }
            damaged = session.ReadUnit(1);
            damaged.Target.Health--;
            world.EntityManager.SetComponentData(session.UnitEntity(1), damaged);
            visuals.Synchronize();
            for (int tick = 0; tick < durationTicks - firstPart; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero);
                visuals.Synchronize();
            }
            Assert.That(visuals.IsHitFlashing(1), Is.True, "The second hit must refresh the configured flash window.");
            for (int tick = durationTicks - firstPart; tick < durationTicks; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero);
                visuals.Synchronize();
            }
            Assert.That(visuals.IsHitFlashing(1), Is.False);
            MaterialMeshInfo restoredInfo = world.EntityManager.GetComponentData<MaterialMeshInfo>(session.UnitEntity(1));
            Assert.That(MaterialMeshInfo.StaticIndexToArrayIndex(restoredInfo.Material), Is.EqualTo(damagedPlayer.MaterialIndex));

            damaged = session.ReadUnit(1);
            damaged.Target.Health++;
            world.EntityManager.SetComponentData(session.UnitEntity(1), damaged);
            visuals.Synchronize();
            Assert.That(visuals.IsHitFlashing(1), Is.False, "Healing must not trigger the hit flash.");
        }

        /// <summary>用真实弹丸结算驱动怪物扣血，确认伤害链路在同次表现同步中启动白闪。</summary>
        /// <remarks>仅修改隔离测试 World 内的单位状态；不绕过 CombatSession 直接改目标生命。</remarks>
        [Test]
        public void ActualProjectileDamageStartsMonsterHitFlash()
        {
            Tables tables = LoadTables();
            PerformanceScenarioConfig scenario = tables.TbPerformanceScenario.Get(4);
            var source = new Files(tables);
            using var world = new World("Actual projectile hit flash");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, scenario, assets);

            CombatUnit player = session.ReadUnit(0);
            player.Cooldown = 0;
            player.Attack.Hit = 1;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                CombatUnit monster = session.ReadUnit(slot);
                monster.Position = player.Position + new float2(slot == 1 ? 2f : 8f, 0f);
                monster.PreviousPosition = monster.Position;
                monster.MoveSpeed = 0;
                monster.Cooldown = 1000;
                monster.Target.Evasion = 0;
                if (slot > 1) monster.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), monster);
            }

            long originalHealth = session.ReadUnit(1).Target.Health;
            session.Advance(session.StepSeconds, float2.zero);
            visuals.Synchronize();
            bool damaged = session.ReadUnit(1).Target.Health < originalHealth;
            if (!damaged)
            {
                int projectileSlot = Enumerable.Range(0, session.ProjectileCapacity)
                    .First(index => session.ReadProjectile(index).Active);
                CombatProjectile projectile = session.ReadProjectile(projectileSlot);
                projectile.Position = session.ReadUnit(1).Position - projectile.CollisionOffset;
                projectile.Velocity = float2.zero;
                projectile.BornThisTick = false;
                world.EntityManager.SetComponentData(session.ProjectileEntity(projectileSlot), projectile);
                session.Advance(session.StepSeconds, float2.zero);
                visuals.Synchronize();
                damaged = session.ReadUnit(1).Target.Health < originalHealth;
            }
            Assert.That(damaged, Is.True, "Configured projectile did not damage the stationary target.");
            Assert.That(visuals.IsHitFlashing(1), Is.True,
                "Actual projectile damage must trigger the target hit flash immediately.");
            Assert.That(visuals.IsHitFlashing(0), Is.False);
        }

        /// <summary>核对共享加载数量、全帧预建和显式释放，不因怪物数量重复加载。</summary>
        [Test]
        public void SharedResourcesLoadOnceAndDispose()
        {
            var tables = LoadTables(); var source = new Files(tables);
            var assets = CombatVisualResources.LoadAsync(tables.TbPerformanceScenario.Get(5), source, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(source.Loads, Is.EqualTo(14));
            Assert.That(source.ActiveHandles, Is.Zero);
            Assert.That(assets.ClipCount, Is.EqualTo(7));
            Assert.That(assets.TextureCount, Is.EqualTo(7));
            Assert.That(assets.Materials.Length, Is.EqualTo(assets.TextureCount * 2));
            Assert.That(assets.Meshes.Length, Is.GreaterThan(100));
            var mesh = assets.Meshes[0]; var material = assets.Materials[0]; var texture = material.mainTexture;
            assets.Dispose(); assets.Dispose();
            Assert.That(mesh == null && material == null && texture == null, Is.True);
            Assert.Throws<ObjectDisposedException>(() => assets.Read(20001, 20002));
        }

        /// <summary>正式关卡一次性预加载三种弹丸及两种目标位置技能的全部显式 ANI。</summary>
        [Test]
        public void FormalRunPreloadsAllConfirmedSkillVisuals()
        {
            var tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, 1);
            var source = new Files(tables);
            using var assets = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None).GetAwaiter().GetResult();
            CombatVisualResources.Clip unitClip = assets.Read(definition.Character.VisualSetId,
                definition.Character.VisualSetId_Ref.StandClipId.Value);
            Assert.That(assets.Materials[unitClip.MaterialIndex].renderQueue,
                Is.EqualTo(definition.Presentation.UnitRenderQueue));
            foreach (var skill in definition.AvailableSkills)
            {
                if (skill.ProjectileClipId.HasValue)
                {
                    Assert.That(assets.Read(skill.Id, skill.ProjectileClipId.Value).Animation.ActionCount, Is.GreaterThan(0));
                    Assert.That(assets.Materials[assets.Read(skill.Id, skill.ProjectileClipId.Value).MaterialIndex].renderQueue,
                        Is.EqualTo(definition.Presentation.SkillRenderQueue));
                }
                if (skill.ImpactClipId.HasValue)
                {
                    Assert.That(assets.Read(skill.Id, skill.ImpactClipId.Value).Animation.ActionCount, Is.GreaterThan(0));
                    Assert.That(assets.Materials[assets.Read(skill.Id, skill.ImpactClipId.Value).MaterialIndex].renderQueue,
                        Is.EqualTo(definition.Presentation.SkillRenderQueue));
                }
                foreach (var clipId in skill.AreaClipIds)
                {
                    Assert.That(assets.Read(skill.Id, clipId).Animation.ActionCount, Is.GreaterThan(0));
                    Assert.That(assets.Materials[assets.Read(skill.Id, clipId).MaterialIndex].renderQueue,
                        Is.EqualTo(definition.Presentation.SkillRenderQueue));
                }
            }
            Assert.That(definition.Presentation.SkillRenderQueue,
                Is.GreaterThan(definition.Presentation.UnitRenderQueue));
            Assert.That(definition.Presentation.FeedbackRenderQueue,
                Is.GreaterThan(definition.Presentation.SkillRenderQueue));
            Assert.That(source.ActiveHandles, Is.Zero);
        }

        /// <summary>正式两种 TargetArea 释放时，一段与三段 ANI 在目标位置叠加并在各自结束后回池。</summary>
        [Test]
        public void FormalTargetAreaVisualsLayerAndRecycle()
        {
            var tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, 1);
            var source = new Files(tables);
            using var world = new World("Formal target area visuals");
            using var assets = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, definition, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, definition, assets);
            session.SynchronizePlayerSkills(definition.AvailableSkills.Select(item => item.Id));
            Assert.That(session.TrySpawnMonster(definition.Monsters[0],
                ConfigNumber.Decode(definition.Map.SpawnRadiusPixelsMilli), 1, false, out int slot), Is.True);
            CombatUnit monster = session.ReadUnit(slot);
            monster.Position = new float2(2, 0);
            monster.PreviousPosition = monster.Position;
            monster.MoveSpeed = 0;
            monster.Cooldown = 1000;
            monster.Target.Evasion = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(slot), monster);

            session.Advance(session.StepSeconds, float2.zero);
            visuals.Synchronize();
            Assert.That(session.AreaCount, Is.EqualTo(2));
            Assert.That(visuals.VisibleAreaCount, Is.EqualTo(4));

            monster = session.ReadUnit(slot);
            monster.Target.Health = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(slot), monster);
            double maximumDuration = definition.AvailableSkills
                .Where(item => item.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.TargetArea)
                .SelectMany(skill => skill.AreaClipIds.Select(clipId =>
                    assets.Read(skill.Id, clipId).Animation.DurationSeconds(0)))
                .Max();
            int ticks = (int)Math.Ceiling(maximumDuration / session.StepSeconds) + 1;
            for (int tick = 0; tick < ticks; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero);
                visuals.Synchronize();
            }
            Assert.That(visuals.VisibleAreaCount, Is.Zero);
        }

        /// <summary>逐一验证正式三种弹丸技能在飞行期间可见，首次命中只产生一个命中 ANI，播放结束后回收。</summary>
        [Test]
        public void FormalProjectileVisualsFlyImpactAndRecycle()
        {
            var tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, 1);
            var source = new Files(tables);
            using var assets = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            SkillConfig[] projectileSkills = definition.AvailableSkills
                .Where(item => item.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Projectile)
                .ToArray();
            Assert.That(projectileSkills.Select(item => item.Id), Is.EquivalentTo(new[] { 20001, 20014, 20032 }));

            foreach (SkillConfig skill in projectileSkills)
            {
                using var world = new World("Formal projectile visual " + skill.Id);
                using var session = CombatSessionFactory.CreateAsync(world, definition, source, CancellationToken.None)
                    .GetAwaiter().GetResult();
                using var visuals = new CombatEntityVisuals(world, session, definition, assets);
                session.SynchronizePlayerSkills(new[] { skill.Id });
                int weaponIndex = Enumerable.Range(0, session.PlayerWeaponCount)
                    .Single(index => session.ReadPlayerWeapon(index).SkillId == skill.Id);
                CombatWeaponState weapon = session.ReadPlayerWeapon(weaponIndex);
                Assert.That(weapon.Active, Is.True);
                Assert.That(session.TrySpawnMonster(definition.Monsters[0],
                    ConfigNumber.Decode(definition.Map.SpawnRadiusPixelsMilli), (ulong)skill.Id, false,
                    out int monsterSlot), Is.True);
                CombatUnit player = session.ReadUnit(0);
                CombatUnit monster = session.ReadUnit(monsterSlot);
                monster.Position = player.Position + new float2(math.max(0.5f, (float)weapon.Range * 0.5f), 0);
                monster.PreviousPosition = monster.Position;
                monster.MoveSpeed = 0;
                monster.Cooldown = 1000;
                monster.Target.Evasion = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);

                bool sawFlight = false, sawExpectedMesh = false, sawImpact = false;
                int maximumTicks = (int)Math.Ceiling(weapon.ProjectileLifetime / session.StepSeconds) + 2;
                for (int tick = 0; tick < maximumTicks && !sawImpact; tick++)
                {
                    session.Advance(session.StepSeconds, float2.zero);
                    visuals.Synchronize();
                    sawFlight |= visuals.VisibleProjectileCount > 0;
                    if (!sawExpectedMesh && visuals.VisibleProjectileCount > 0)
                    {
                        int projectileSlot = Enumerable.Range(0, session.ProjectileCapacity)
                            .First(index => session.ReadProjectile(index).Active &&
                                            session.ReadProjectile(index).SkillId == skill.Id);
                        CombatProjectile projectile = session.ReadProjectile(projectileSlot);
                        CombatVisualResources.Clip clip = assets.Read(skill.Id, skill.ProjectileClipId.Value);
                        int sampledFrame = clip.Animation.Sample(0, projectile.Age);
                        MaterialMeshInfo mesh = world.EntityManager.GetComponentData<MaterialMeshInfo>(
                            session.ProjectileEntity(projectileSlot));
                        Assert.That(MaterialMeshInfo.StaticIndexToArrayIndex(mesh.Mesh),
                            Is.EqualTo(clip.MeshIndices[sampledFrame * 2 + (clip.FlipX ? 1 : 0)]));
                        sawExpectedMesh = true;
                    }
                    sawImpact |= session.ImpactCount == 1 && session.ReadImpact(0).SkillId == skill.Id &&
                                 visuals.VisibleImpactCount == 1;
                }
                Assert.That(sawFlight, Is.True, "Projectile flight was never visible for skill " + skill.Id);
                Assert.That(sawExpectedMesh, Is.True, "Projectile mesh was not verified for skill " + skill.Id);
                Assert.That(sawImpact, Is.True, "Impact feedback was never visible for skill " + skill.Id);
                Assert.That(session.Statistics.DamageEvents, Is.EqualTo(1));

                monster = session.ReadUnit(monsterSlot);
                monster.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);
                int impactTicks = (int)Math.Ceiling(assets.Read(skill.Id,
                    skill.ImpactClipId.Value).Animation.DurationSeconds(0) / session.StepSeconds) + 1;
                for (int tick = 0; tick < impactTicks; tick++)
                {
                    session.Advance(session.StepSeconds, float2.zero);
                    visuals.Synchronize();
                }
                Assert.That(visuals.VisibleImpactCount, Is.Zero,
                    "Impact feedback did not recycle for skill " + skill.Id);
            }
        }

        /// <summary>注入中途取消或加载错误后临时句柄和部分 Unity 对象全部释放。</summary>
        /// <param name="cancel">是否模拟取消而不是资源读取失败。</param>
        [TestCase(false)]
        [TestCase(true)]
        public void InterruptedLoadingReleasesPartialObjects(bool cancel)
        {
            var tables = LoadTables(); var source = new Files(tables) { FailAt = 4, Cancel = cancel };
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            Assert.Catch(() => CombatVisualResources.LoadAsync(tables.TbPerformanceScenario.Get(4), source, CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(source.ActiveHandles, Is.Zero);
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length, Is.EqualTo(meshes));
            Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Length, Is.EqualTo(textures));
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materials));
        }

        /// <summary>读取真实模拟时序验证 zd 前摇、gj F4、暂停与死亡隐藏，表现不修改伤害计数。</summary>
        [Test]
        public void EntityPlaybackFollowsSimulationWithoutDealingDamage()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var world = new World("Visual playback test");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, scenario, assets);
            var hero = session.ReadUnit(0); hero.Cooldown = 100;
            world.EntityManager.SetComponentData(session.UnitEntity(0), hero);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot); unit.Position = hero.Position; unit.MoveSpeed = 0;
                if (slot > 1) unit.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize();
            var player = visuals.Read(1);
            Assert.That(player.ClipId, Is.EqualTo(20007));
            Assert.That(session.ReadUnit(1).AttackAge, Is.Zero);
            var timing = session.ReadUnit(1);
            int maximumTicks = (int)System.Math.Ceiling((timing.WindupSeconds + timing.AttackOption.DurationSeconds) * timing.Interval / timing.BaseInterval / session.StepSeconds) + 2;
            for (int tick = 0; session.Statistics.MeleeJudgements == 0 && tick < maximumTicks; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize();
                var unit = session.ReadUnit(1);
                Assert.That(player.ClipId, Is.EqualTo(unit.AttackAge < unit.WindupSeconds ? 20007 : 20002));
            }
            Assert.That(session.Statistics.MeleeJudgements, Is.GreaterThan(0), "Visual timeline did not reach the configured hit frame.");
            var attack = session.ReadUnit(1);
            var animation = assets.Read(20001, 20002).Animation;
            Assert.That(player.GlobalFrame, Is.EqualTo(animation.HoldFrame(attack.AttackOption.ActionIndex, 4)));
            var meshInfo = world.EntityManager.GetComponentData<MaterialMeshInfo>(session.UnitEntity(1));
            Assert.That(MaterialMeshInfo.StaticIndexToArrayIndex(meshInfo.Mesh), Is.EqualTo(player.MeshIndex));
            Assert.That(MaterialMeshInfo.StaticIndexToArrayIndex(meshInfo.Material), Is.EqualTo(player.MaterialIndex));
            var before = session.Statistics; int frame = player.MeshIndex;
            session.Paused = true; session.Advance(10, float2.zero);
            for (int i = 0; i < 10; i++) visuals.Synchronize();
            Assert.That(player.MeshIndex, Is.EqualTo(frame)); Assert.That(session.Statistics, Is.EqualTo(before));
            session.Paused = false;
            attack.Target.Health = 0; world.EntityManager.SetComponentData(session.UnitEntity(1), attack);
            session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize();
            Assert.That(player.Visible, Is.False);
            Assert.That(world.EntityManager.HasComponent<DisableRendering>(session.UnitEntity(1)), Is.True);
            session.Restart(); visuals.Synchronize();
            Assert.That(player.Visible, Is.True); Assert.That(player.ClipId, Is.EqualTo(20007));
        }

        /// <summary>主角八方向移动、静止保持方向，无攻击动作；预热后纯选帧不分配托管内存。</summary>
        [Test]
        public void HeroDirectionsAndSamplingAllocateNoGarbage()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var world = new World("Hero visual directions");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            var player = new CombatVisualPlayer(scenario.CharacterId_Ref.VisualSetId_Ref, assets);
            var unit = session.ReadUnit(0); ulong tick = 0;
            foreach (var direction in scenario.PresentationId_Ref.DirectionIds_Ref)
            {
                unit.PreviousPosition = float2.zero; unit.Position = new float2(direction.X, direction.Y);
                player.Sample(unit, ++tick, session.StepSeconds);
                Assert.That(player.ClipId, Is.EqualTo(10004)); Assert.That(player.ActionIndex, Is.EqualTo(direction.ActionIndex));
                Assert.That(player.FlipX, Is.EqualTo(direction.FlipX));
                unit.PreviousPosition = unit.Position; player.Sample(unit, ++tick, session.StepSeconds);
                Assert.That(player.ClipId, Is.EqualTo(10002)); Assert.That(player.ActionIndex, Is.EqualTo(direction.ActionIndex));
            }
            for (int i = 0; i < 100; i++) player.Sample(unit, ++tick, session.StepSeconds);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) player.Sample(unit, ++tick, session.StepSeconds);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - allocated, Is.Zero);
        }

        /// <summary>补算跳过完整攻击仍读取模拟最终朝向；生命周期改变清空旧攻击片段与进度。</summary>
        [Test]
        public void SkippedAttackAndRecycledLifetimeKeepCorrectFacing()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var world = new World("Visual skipped attack");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            var visual = scenario.MonsterIds_Ref[0].VisualSetId_Ref;
            var player = new CombatVisualPlayer(visual, assets);
            var unit = session.ReadUnit(1); player.Sample(unit, 0, session.StepSeconds);
            var expected = scenario.PresentationId_Ref.AttackDirectionIds_Ref[3].FacingDirectionId_Ref;
            unit.PreviousPosition = unit.Position; unit.Facing = new float2(expected.X, expected.Y);
            player.Sample(unit, 100, session.StepSeconds);
            Assert.That(player.ActionIndex, Is.EqualTo(expected.ActionIndex)); Assert.That(player.FlipX, Is.EqualTo(expected.FlipX));
            unit.Target.Lifetime++;
            unit.Facing = new float2(scenario.PresentationId_Ref.InitialDirectionId_Ref.X, scenario.PresentationId_Ref.InitialDirectionId_Ref.Y);
            player.Sample(unit, 101, session.StepSeconds);
            Assert.That(player.ClipId, Is.EqualTo(visual.StandClipId.Value));
            Assert.That(player.GlobalFrame, Is.EqualTo(assets.Read(visual.Id, visual.StandClipId.Value).Animation.HoldFrame(player.ActionIndex, 0)));
        }

        /// <summary>加载真实生成配置，旧选帧用例显式关闭定时生成以维持固定出生夹具。</summary>
        /// <param name="timedSpawn">为真时保持场景 4 的正式圆周刷怪参数。</param>
        /// <returns>解析完外键的表。</returns>
        internal static Tables LoadTables(bool timedSpawn = false)
        {
            var tables = new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
            // 固定选帧夹具保留出生目标；动态绑定测试显式消费真实定时配置。
            if (!timedSpawn)
                foreach (string field in new[] { "SpawnRadiusPixelsMilli", "SpawnIntervalSecondsMilli", "SpawnBatchCount", "SpawnUnitIntervalSecondsMilli" })
                    typeof(PerformanceScenarioConfig).GetField(field).SetValue(tables.TbPerformanceScenario.Get(4), null);
            return tables;
        }

        /// <summary>测试专用资源适配器，正式资源层只接收资源 ID。</summary>
        internal sealed class Files : IResourceService
        {
            private readonly Tables tables;
            public int Loads, ActiveHandles, FailAt;
            public bool Cancel;
            /// <summary>保存测试表供 ID 解析。</summary>
            /// <param name="tables">实际生成表。</param>
            internal Files(Tables tables) { this.tables = tables; }
            /// <summary>测试通过 ID 读取实际 ANI/PNG，支持受控异常注入。</summary>
            /// <param name="resourceId">资源表 ID。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>立即完成的测试句柄。</returns>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); Loads++;
                if (Loads == FailAt) { if (Cancel) throw new OperationCanceledException(); throw new IOException("Injected resource failure"); }
                var data = File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, tables.TbResource.Get(resourceId).Path));
                ActiveHandles++; return Task.FromResult<IRawResourceHandle>(new Raw(this, data));
            }
            /// <summary>本测试不支持 Unity 导入资产，误用失败。</summary>
            /// <typeparam name="TAsset">资产类型。</typeparam>
            /// <param name="resourceId">资源 ID。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>不返回。</returns>
            /// <exception cref="NotSupportedException">误用对象加载。</exception>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class => throw new NotSupportedException();
        }
        /// <summary>记录测试句柄生命周期。</summary>
        private sealed class Raw : IRawResourceHandle
        {
            private Files owner;
            public byte[] Data { get; }
            /// <summary>构造时接管已读数据。</summary>
            /// <param name="owner">计数器所有者。</param>
            /// <param name="data">文件内容。</param>
            public Raw(Files owner, byte[] data) { this.owner = owner; Data = data; }
            /// <summary>释放时减计数，重复释放无副作用。</summary>
            public void Dispose() { if (owner == null) return; owner.ActiveHandles--; owner = null; }
        }
    }
}
