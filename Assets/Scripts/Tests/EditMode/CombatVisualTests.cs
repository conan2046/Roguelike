using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat;
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
        /// <summary>快照必须反映同Tick外部生命修改；主体与血条共享采集后立即隐藏死亡、复用时恢复。</summary>
        /// <remarks>测试修改只发生在隔离World，不推进Tick来掩盖快照过期；反馈实体随绑定释放。</remarks>
        [Test]
        public void SharedPresentationSnapshotSeesSameTickDeathAndReuse()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var world = new World("Same tick presentation snapshot");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, scenario, assets);
            using var feedback = CombatFeedbackVisuals.CreateAsync(world, session, scenario, new ImportedFeedbackFiles(tables), CancellationToken.None).GetAwaiter().GetResult();
            ulong tick = session.Statistics.Tick;
            Entity entity = session.UnitEntity(1);
            CombatUnit unit = session.ReadUnit(1);
            unit.Target.Health = 0;
            world.EntityManager.SetComponentData(entity, unit);
            var snapshot = session.CapturePresentationUnits();
            Assert.That(snapshot[1].Target.Health, Is.Zero);
            visuals.Synchronize(snapshot); feedback.Synchronize(snapshot);
            Assert.That(visuals.Read(1).Visible, Is.False);
            Assert.That(world.EntityManager.HasComponent<DisableRendering>(entity), Is.True);
            Assert.That(feedback.ReadHealthBar(1).Visible, Is.False);
            int deadCount = feedback.HealthBarBatch.Count;
            unit.Target.Health = unit.Target.MaxHealth; unit.Target.Lifetime++;
            world.EntityManager.SetComponentData(entity, unit);
            snapshot = session.CapturePresentationUnits();
            visuals.Synchronize(snapshot); feedback.Synchronize(snapshot);
            Assert.That(visuals.Read(1).Visible, Is.True);
            Assert.That(visuals.IsHitFlashing(1), Is.False);
            Assert.That(world.EntityManager.HasComponent<DisableRendering>(entity), Is.False);
            var frame = feedback.ReadHealthBar(1);
            Assert.That(frame.Visible, Is.True);
            Assert.That(feedback.HealthBarBatch.Count, Is.EqualTo(deadCount + 1));
            Assert.That(frame.Frame.m03, Is.EqualTo(unit.Position.x));
            Assert.That(frame.Frame.m00, Is.EqualTo(scenario.PresentationId_Ref.HealthBarWidthPixels * scenario.CombatRulesId_Ref.WorldUnitsPerPixel));
            Assert.That(frame.Frame.m11, Is.EqualTo(scenario.PresentationId_Ref.HealthBarHeightPixels * scenario.CombatRulesId_Ref.WorldUnitsPerPixel));
            Assert.That(session.Statistics.Tick, Is.EqualTo(tick));
        }

        /// <summary>暂停重复同步不改主体ECS版本，也不重复上传未变化的血条网格。</summary>
        /// <remarks>快照采集提升系统版本后比较主体chunk版本；血条比较真实上传版本，避免只检查数值不变。</remarks>
        [Test]
        public void UnchangedPresentationDoesNotDirtyRenderComponents()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4); var source = new Files(tables);
            using var world = new World("Unchanged presentation writes");
            using var assets = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var visuals = new CombatEntityVisuals(world, session, scenario, assets);
            using var feedback = CombatFeedbackVisuals.CreateAsync(world, session, scenario, new ImportedFeedbackFiles(tables), CancellationToken.None).GetAwaiter().GetResult();
            var manager = world.EntityManager;
            Entity body = session.UnitEntity(1);
            ulong uploadVersion = feedback.HealthBarBatch.UploadVersion;
            uint materialVersion = manager.GetChunk(body).GetChangeVersion(manager.GetComponentTypeHandle<MaterialMeshInfo>(true));
            uint boundsVersion = manager.GetChunk(body).GetChangeVersion(manager.GetComponentTypeHandle<RenderBounds>(true));
            var clock = world.GetOrCreateSystemManaged<PresentationVersionClock>();
            clock.Update();
            var snapshot = session.CapturePresentationUnits();
            visuals.Synchronize(snapshot); feedback.Synchronize(snapshot);
            Assert.That(manager.GetChunk(body).GetChangeVersion(manager.GetComponentTypeHandle<MaterialMeshInfo>(true)), Is.EqualTo(materialVersion));
            Assert.That(manager.GetChunk(body).GetChangeVersion(manager.GetComponentTypeHandle<RenderBounds>(true)), Is.EqualTo(boundsVersion));
            Assert.That(feedback.HealthBarBatch.UploadVersion, Is.EqualTo(uploadVersion));
        }

        /// <summary>仅在测试中推进全局系统版本，不读写组件或运行战斗。</summary>
        [DisableAutoCreation]
        public partial class PresentationVersionClock : SystemBase
        {
            /// <summary>测试显式Update时推进版本；空实现确保版本变化不来自被测渲染写入。</summary>
            protected override void OnUpdate() { }
        }

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
                var callbacks = typeof(UnityEngine.Rendering.RenderPipelineManager).GetField("beginCameraRendering",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                using var feedback = CombatFeedbackVisuals.CreateAsync(world, session, scenario, new ImportedFeedbackFiles(tables), CancellationToken.None).GetAwaiter().GetResult();
                object batch = feedback.HealthBarBatch;
                var registered = (Delegate)callbacks.GetValue(null);
                Assert.That(registered.GetInvocationList().Count(item => ReferenceEquals(item.Target, batch)), Is.EqualTo(1));
                world.Dispose();
                Assert.DoesNotThrow(() => visuals.Dispose());
                Assert.DoesNotThrow(() => visuals.Dispose());
                Assert.DoesNotThrow(() => feedback.Dispose());
                Assert.DoesNotThrow(() => feedback.Dispose());
                var remaining = (Delegate)callbacks.GetValue(null);
                Assert.That(remaining == null || remaining.GetInvocationList().All(item => !ReferenceEquals(item.Target, batch)), Is.True);
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
            using var feedback = CombatFeedbackVisuals.CreateAsync(world, session, scenario, new ImportedFeedbackFiles(tables), CancellationToken.None).GetAwaiter().GetResult();
            int loads = source.Loads;
            int waveTicks = (int)Math.Ceiling(((decimal)scenario.SpawnIntervalSeconds.Value + (scenario.SpawnBatchCount.Value - 1) * (decimal)scenario.SpawnUnitIntervalSeconds.Value) * scenario.CombatRulesId_Ref.SimulationHz);
            for (int i = 0; i < waveTicks * 2; i++)
            {
                for (int slot = 0; slot < session.UnitCount; slot++)
                { var u = session.ReadUnit(slot); u.MoveSpeed = 0; u.Cooldown = 1000; world.EntityManager.SetComponentData(session.UnitEntity(slot), u); }
                session.Advance(session.StepSeconds, float2.zero); visuals.Synchronize(); feedback.Synchronize();
            }
            Assert.That(session.Statistics.AliveMonsters, Is.EqualTo(2 * scenario.SpawnBatchCount.Value));
            Assert.That(feedback.HealthBarBatch.Count, Is.EqualTo(1 + session.Statistics.AliveMonsters));
            Assert.That(source.Loads, Is.EqualTo(loads));
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                Assert.That(visuals.Read(slot).Visible, Is.True);
                Assert.That(world.EntityManager.HasComponent<MaterialMeshInfo>(session.UnitEntity(slot)), Is.True);
            }
            session.Restart(); visuals.Synchronize(); feedback.Synchronize();
            Assert.That(feedback.HealthBarBatch.Count, Is.EqualTo(1));
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

        /// <summary>正式关卡预加载全部显式 ANI，并逐模型与技能片段核对 TbCombatRender 队列，加载后不遗留临时句柄。</summary>
        [Test]
        public void FormalRunPreloadsAllConfirmedSkillVisuals()
        {
            var tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, 1);
            var source = new Files(tables);
            using var assets = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None).GetAwaiter().GetResult();
            var renderCatalog = new CombatRenderCatalog(definition.Presentation);
            CombatVisualResources.Clip unitClip = assets.Read(definition.Character.VisualSetId,
                definition.Character.VisualSetId_Ref.StandClipId.Value);
            Assert.That(assets.Materials[unitClip.MaterialIndex].renderQueue,
                Is.EqualTo(renderCatalog.ReadModel(definition.Character.VisualSetId)));
            foreach (var skill in definition.AvailableSkills)
            {
                if (skill.ProjectileClipId.HasValue)
                {
                    Assert.That(assets.Read(skill.Id, skill.ProjectileClipId.Value).Animation.ActionCount, Is.GreaterThan(0));
                    Assert.That(assets.Materials[assets.Read(skill.Id, skill.ProjectileClipId.Value).MaterialIndex].renderQueue,
                        Is.EqualTo(renderCatalog.ReadSkill(skill.Id, skill.ProjectileClipId.Value)));
                }
                if (skill.ImpactClipId.HasValue)
                {
                    Assert.That(assets.Read(skill.Id, skill.ImpactClipId.Value).Animation.ActionCount, Is.GreaterThan(0));
                    Assert.That(assets.Materials[assets.Read(skill.Id, skill.ImpactClipId.Value).MaterialIndex].renderQueue,
                        Is.EqualTo(renderCatalog.ReadSkill(skill.Id, skill.ImpactClipId.Value)));
                }
                foreach (var clipId in skill.AreaClipIds)
                {
                    Assert.That(assets.Read(skill.Id, clipId).Animation.ActionCount, Is.GreaterThan(0));
                    Assert.That(assets.Materials[assets.Read(skill.Id, clipId).MaterialIndex].renderQueue,
                        Is.EqualTo(renderCatalog.ReadSkill(skill.Id, clipId)));
                }
            }
            Assert.That(source.ActiveHandles, Is.Zero);
        }

        /// <summary>验证正式范围技能叠加、回池，以及满星和范围被动作用后视觉与实际判定使用相同缩放。</summary>
        /// <param name="skillId">六种基础或进化范围形态。</param>
        /// <param name="withGrowth">是否应用正式满星与范围被动配置的效果快照。</param>
        [TestCase(20028, false)] [TestCase(20028, true)]
        [TestCase(20033, false)] [TestCase(20033, true)]
        [TestCase(20043, false)] [TestCase(20043, true)]
        [TestCase(20050, false)] [TestCase(20050, true)]
        [TestCase(20007, false)] [TestCase(20007, true)]
        [TestCase(20037, false)] [TestCase(20037, true)]
        public void FormalTargetAreaVisualsLayerAndRecycle(int skillId, bool withGrowth)
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
            session.SynchronizePlayerSkills(new[] { skillId });
            SkillConfig selectedSkill = definition.AvailableSkills.Single(skill => skill.Id == skillId);
            RunWeaponConfig selectedWeapon = definition.BuildConfig.WeaponIds_Ref.Single(weapon =>
                weapon.Ranks.Any(rank => rank.SkillId == skillId) || weapon.EvolutionId_Ref.ResultSkillId == skillId);
            double growthRange = 1;
            if (withGrowth)
            {
                var attributes = CombatAttributeSnapshot.Capture(new CombatAttributes(definition.Character.AttributeProfileId_Ref));
                RunPassiveRank rangePassive = definition.BuildConfig.PassiveIds_Ref.SelectMany(passive => passive.Ranks)
                    .Last(rank => rank.AttributeId_Ref.AttributeType == EAttributeType.SkillRangeMultiplier);
                attributes.SkillRangeMultiplier *= 1 + rangePassive.ValueMilli / 1000d;
                session.ApplyWeaponUpgrades(new[] { new CombatWeaponUpgrade(selectedWeapon.Ranks.Last(),
                    definition.BuildConfig.WeaponDamagePermille, skillId) }, attributes);
                growthRange = attributes.SkillRangeMultiplier * selectedWeapon.Ranks.Last().RangePermille / 1000d;
            }
            Assert.That(session.TrySpawnMonster(definition.Monsters[0],
                definition.Map.SpawnRadiusPixels, 1, false, out int slot), Is.True);
            CombatUnit monster = session.ReadUnit(slot);
            monster.Position = new float2(2, 0);
            monster.PreviousPosition = monster.Position;
            monster.MoveSpeed = 0;
            monster.Cooldown = 1000;
            monster.Target.Evasion = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(slot), monster);

            using var entitiesBeforeArea = world.EntityManager.GetAllEntities(Unity.Collections.Allocator.Temp);
            var oldEntities = new System.Collections.Generic.HashSet<Entity>(entitiesBeforeArea.ToArray());
            session.Advance(session.StepSeconds, float2.zero);
            visuals.Synchronize();
            int layers = Math.Max(1, selectedSkill.CombatProfileId_Ref.AreaRingCount);
            Assert.That(session.AreaCount, Is.EqualTo(layers));
            Assert.That(visuals.VisibleAreaCount, Is.EqualTo(selectedSkill.AreaClipIds.Count * layers));
            for (int index = 0; index < session.AreaCount; index++)
            {
                CombatAreaEvent area = session.ReadArea(index);
                SkillConfig skill = definition.AvailableSkills.Single(item => item.Id == area.SkillId);
                Assert.That(area.Radius, Is.EqualTo(skill.AreaRadiusPixels.Value * definition.CombatRules.WorldUnitsPerPixel * growthRange * (index + 1) / layers).Within(1e-6));
            }
            using var entitiesAfterArea = world.EntityManager.GetAllEntities(Unity.Collections.Allocator.Temp);
            Entity[] spawnedAreaEntities = entitiesAfterArea.ToArray().Where(entity => !oldEntities.Contains(entity) &&
                world.EntityManager.HasComponent<Unity.Transforms.LocalTransform>(entity)).ToArray();
            Assert.That(spawnedAreaEntities.Length, Is.EqualTo(selectedSkill.AreaClipIds.Count * layers));
            float[] actualScales = spawnedAreaEntities.Select(entity => world.EntityManager.GetComponentData<Unity.Transforms.LocalTransform>(entity).Scale).OrderBy(value => value).ToArray();
            float[] expectedScales = Enumerable.Range(1, layers).SelectMany(layer => Enumerable.Repeat((float)(growthRange * layer / layers), selectedSkill.AreaClipIds.Count)).OrderBy(value => value).ToArray();
            Assert.That(actualScales, Is.EqualTo(expectedScales).Within(1e-6));

            monster = session.ReadUnit(slot);
            monster.Target.Health = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(slot), monster);
            double maximumDuration = definition.AvailableSkills
                .Where(item => item.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.TargetArea)
                .SelectMany(skill => skill.AreaClipIds.Select(clipId =>
                    assets.Read(skill.Id, clipId).Animation.DurationSeconds(0)))
                .Max();
            session.SynchronizePlayerSkills(Array.Empty<int>());
            maximumDuration += definition.AvailableSkills.Max(skill => skill.CombatProfileId_Ref.AreaDurationMilli) / 1000d;
            int ticks = (int)Math.Ceiling(maximumDuration / session.StepSeconds) + 1;
            for (int tick = 0; tick < ticks; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero);
                visuals.Synchronize();
            }
            Assert.That(visuals.VisibleAreaCount, Is.Zero);
        }

        /// <summary>逐一验证六种弹丸的飞行帧、命中挂点跟随、目标失效后停留及播放结束回收。</summary>
        /// <param name="invalidateByDeath">目标死亡时为真；否则直接更换生命周期，模拟槽位复用。</param>
        [TestCase(false)]
        [TestCase(true)]
        public void FormalProjectileVisualsFlyImpactAndRecycle(bool invalidateByDeath)
        {
            var tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, 1);
            var source = new Files(tables);
            using var assets = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None)
                .GetAwaiter().GetResult();
            SkillConfig[] projectileSkills = definition.AvailableSkills
                .Where(item => item.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Projectile)
                .ToArray();
            Assert.That(projectileSkills.Select(item => item.Id), Is.EquivalentTo(new[] { 20001, 20014, 20016, 20044, 20027, 20030 }));

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
                    definition.Map.SpawnRadiusPixels, (ulong)skill.Id, false,
                    out int monsterSlot), Is.True);
                CombatUnit player = session.ReadUnit(0);
                CombatUnit monster = session.ReadUnit(monsterSlot);
                monster.Position = player.Position + new float2(math.max(0.5f, (float)weapon.Range * 0.5f), 0);
                monster.PreviousPosition = monster.Position;
                monster.MoveSpeed = 0;
                monster.Cooldown = 1000;
                monster.Target.Evasion = 0;
                monster.Target.Health = monster.Target.MaxHealth = 1000000000;
                monster.HitEffectOffset = new float2(0.25f, 0.75f);
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);

                using var entitiesBeforeImpact = world.EntityManager.GetAllEntities(Unity.Collections.Allocator.Temp);
                var priorEntities = new System.Collections.Generic.HashSet<Entity>(entitiesBeforeImpact.ToArray());
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
                    sawImpact |= session.ImpactCount > 0 && session.ReadImpact(0).SkillId == skill.Id &&
                                 visuals.VisibleImpactCount > 0;
                }
                Assert.That(sawFlight, Is.True, "Projectile flight was never visible for skill " + skill.Id);
                Assert.That(sawExpectedMesh, Is.True, "Projectile mesh was not verified for skill " + skill.Id);
                Assert.That(sawImpact, Is.True, "Impact feedback was never visible for skill " + skill.Id);
                Assert.That(session.Statistics.DamageEvents, Is.GreaterThan(0));

                CombatImpactEvent impactEvent = session.ReadImpact(0);
                Assert.That(impactEvent.TargetSlot, Is.EqualTo(monsterSlot));
                Assert.That(impactEvent.TargetLifetime, Is.EqualTo(session.ReadUnit(monsterSlot).Target.Lifetime));
                using var entitiesAfterImpact = world.EntityManager.GetAllEntities(Unity.Collections.Allocator.Temp);
                Entity[] impactEntities = entitiesAfterImpact.ToArray().Where(entity => !priorEntities.Contains(entity) &&
                    world.EntityManager.HasComponent<Unity.Transforms.LocalTransform>(entity) &&
                    !world.EntityManager.HasComponent<DisableRendering>(entity)).ToArray();
                Assert.That(impactEntities.Length, Is.GreaterThan(0));
                var beforeMove = impactEntities.Select(entity => world.EntityManager.GetComponentData<Unity.Transforms.LocalTransform>(entity)).ToArray();
                monster = session.ReadUnit(monsterSlot);
                float multiplier = skill.CombatProfileId_Ref.ImpactRadiusPixels > 0 ? impactEvent.RadiusMultiplier : 1f;
                float2 expectedPosition = monster.Position + monster.HitEffectOffset +
                    new float2(skill.ImpactVisualOffsetXPixels.Value, skill.ImpactVisualOffsetYPixels.Value) *
                    definition.CombatRules.WorldUnitsPerPixel * multiplier;
                foreach (var transform in beforeMove)
                    Assert.That(math.distance(transform.Position.xy, expectedPosition), Is.LessThan(1e-5f));
                ulong movementTick = session.Statistics.Tick;
                var displacement = new float2(1.25f, -0.4f);
                monster.Position += displacement;
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);
                visuals.Synchronize();
                Assert.That(session.Statistics.Tick, Is.EqualTo(movementTick), "Presentation must not advance simulation.");
                var afterMove = impactEntities.Select(entity => world.EntityManager.GetComponentData<Unity.Transforms.LocalTransform>(entity)).ToArray();
                for (int index = 0; index < impactEntities.Length; index++)
                {
                    Assert.That(math.distance(afterMove[index].Position.xy, beforeMove[index].Position.xy + displacement), Is.LessThan(1e-5f));
                    Assert.That(afterMove[index].Scale, Is.EqualTo(beforeMove[index].Scale));
                }

                // 同Tick验证死亡与复用，防止依靠下一次模拟推进才清除旧目标引用。
                if (invalidateByDeath) monster.Target.Health = 0;
                else monster.Target.Lifetime++;
                monster.Position += new float2(4f, 3f);
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);
                visuals.Synchronize();
                for (int index = 0; index < impactEntities.Length; index++)
                    Assert.That(math.distance(world.EntityManager.GetComponentData<Unity.Transforms.LocalTransform>(impactEntities[index]).Position,
                        afterMove[index].Position), Is.LessThan(1e-5f), "Expired target must not drag its previous impact.");

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
                foreach (string field in new[] { "SpawnRadiusPixels", "SpawnIntervalSecondsMilli", "SpawnBatchCount", "SpawnUnitIntervalSecondsMilli" })
                    typeof(PerformanceScenarioConfig).GetField(field).SetValue(tables.TbPerformanceScenario.Get(4), null);
            return tables;
        }

        /// <summary>反馈专项按TbResource.path借用正式导入纹理；仅供Editor测试，不创建或保存资产。</summary>
        private sealed class ImportedFeedbackFiles : IResourceService
        {
            private readonly Tables tables;
            /// <summary>保存本次真实配置，后续按资源ID解析路径。</summary>
            /// <param name="tables">Luban表集合。</param>
            public ImportedFeedbackFiles(Tables tables) { this.tables = tables; }
            /// <summary>按正式资源表读取已导入纹理；句柄释放不销毁AssetDatabase拥有的原图。</summary>
            /// <typeparam name="TAsset">仅接受Texture2D。</typeparam>
            /// <param name="resourceId">TbResource.id。</param><param name="cancellationToken">加载前检查取消。</param>
            /// <returns>借用原图的测试句柄。</returns>
            /// <exception cref="NotSupportedException">请求类型不是纹理。</exception>
            /// <exception cref="FileNotFoundException">配置路径没有正式纹理。</exception>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (typeof(TAsset) != typeof(Texture2D)) throw new NotSupportedException();
                // ProjectResourceAddressRule为GameContent资源移除Assets/前缀；测试按同一地址协议还原物理路径。
                string path = "Assets/" + tables.TbResource.Get(resourceId).Path;
                Texture2D texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null) throw new FileNotFoundException(path);
                return Task.FromResult((IResourceHandle<TAsset>)(object)new ImportedTextureHandle(texture));
            }
            /// <summary>反馈专项不接受原始文件路径，误用立即失败。</summary>
            /// <param name="resourceId">未消费的资源ID。</param><param name="cancellationToken">未消费的取消令牌。</param>
            /// <returns>不会返回。</returns><exception cref="NotSupportedException">始终抛出。</exception>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        /// <summary>测试借用的正式纹理句柄，不拥有原始资产生命周期。</summary>
        private sealed class ImportedTextureHandle : IResourceHandle<Texture2D>
        {
            public Texture2D Asset { get; }
            /// <summary>保留AssetDatabase拥有的正式纹理引用。</summary>
            /// <param name="asset">正式导入原图。</param>
            public ImportedTextureHandle(Texture2D asset) { Asset = asset; }
            /// <summary>结束借用；原图由AssetDatabase持有，不销毁或保存。</summary>
            public void Dispose() { }
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
