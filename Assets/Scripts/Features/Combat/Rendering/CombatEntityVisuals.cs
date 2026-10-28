using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Entities;
using Unity.Collections;
using Unity.Profiling;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>向会话实体绑定 Entities Graphics 共享帧资源；不创建每单位 GameObject。</summary>
    public sealed class CombatEntityVisuals : IDisposable
    {
        private static readonly ProfilerMarker AniMarker = new ProfilerMarker("Roguelike.Presentation.AniSample");
        private static readonly ProfilerMarker RenderWriteMarker = new ProfilerMarker("Roguelike.Presentation.RenderWrite");
        private static readonly ProfilerMarker SkillMarker = new ProfilerMarker("Roguelike.Presentation.Skills");
        private readonly EntityManager manager;
        private readonly World world;
        private readonly CombatSession session;
        private readonly CombatVisualResources resources;
        private CombatVisualPlayer[] players;
        private UnitRenderState[] unitRenderStates = Array.Empty<UnitRenderState>();

        /// <summary>本绑定拥有的渲染组件上次提交值；只用于避免重复写入，不替代权威单位状态。</summary>
        private struct UnitRenderState
        {
            public bool Initialized, Visible;
            public int Mesh, Material;
        }
        private long[] previousHealth;
        private ulong[] previousLifetimes;
        private ulong[] hitFlashUntilTicks;
        private readonly ulong hitFlashDurationTicks;
        private readonly float worldUnitsPerPixel;
        private readonly IReadOnlyDictionary<int, VisualSetConfig> visualSets;
        private readonly IReadOnlyDictionary<int, SkillConfig> skills;
        private readonly RenderMeshArray meshArray;
        private readonly RenderMeshDescription description;
        private readonly List<ImpactVisual> impacts = new List<ImpactVisual>();
        private readonly List<AreaVisual> areas = new List<AreaVisual>();
        private readonly List<CastVisual> casts = new List<CastVisual>();
        private ulong lastCastSequence;
        private ulong lastCastCasterLifetime;
        private readonly HashSet<(ulong, ulong)> seenImpacts = new HashSet<(ulong, ulong)>();
        private readonly HashSet<ulong> seenAreas = new HashSet<ulong>();
        private bool disposed;
        private ulong lastTick;
        public int VisibleProjectileCount { get; private set; }
        public int VisibleImpactCount { get; private set; }
        public int VisibleAreaCount { get; private set; }
        public int VisibleCastCount { get; private set; }

        /// <summary>一次实际释放的随身施法层；身份校验后每帧跟随施法者，结束保留实体供后续释放复用。</summary>
        private sealed class CastVisual
        {
            public Entity Entity;
            public int SkillId, ClipId, CasterSlot;
            public ulong StartTick, CasterLifetime;
            public float2 Offset;
            public bool Active;
        }

        /// <summary>命中 ANI 的池化播放状态；用槽位和生命周期跟随同一目标，实体只在池扩容时创建。</summary>
        private sealed class ImpactVisual
        {
            public Entity Entity;
            public int SkillId, ClipId, TargetSlot;
            public ulong StartTick, TargetLifetime;
            public bool FollowsTarget;
            public float2 Offset;
            public bool Active;
        }

        /// <summary>目标位置群体技能的单片段池槽；同一释放的多段 ANI 各占一个槽并叠加播放。</summary>
        private sealed class AreaVisual
        {
            public Entity Entity;
            public int SkillId, ClipId;
            public ulong StartTick;
            public bool Active;
            public bool FollowsOwner;
            public ulong OwnerLifetime;
            public float2 Offset;
        }

        /// <summary>资源准备完成后为本局单位附加共享渲染组件，并采样出生帧。</summary>
        /// <param name="world">必须与会话相同的存活世界。</param>
        /// <param name="session">本局实体所有者。</param>
        /// <param name="scenario">与会话相同的场景配置。</param>
        /// <param name="resources">调用方拥有的共享资源。</param>
        /// <remarks>修改会话单位渲染组件；自有世界退出顺序为本绑定、会话、世界、共享资源，先清理 GPU 注册。不修改 Scene/Prefab。</remarks>
        public CombatEntityVisuals(World world, CombatSession session, PerformanceScenarioConfig scenario, CombatVisualResources resources)
            : this(world, session, resources,
                scenario.PresentationId_Ref,
                scenario.CombatRulesId_Ref.WorldUnitsPerPixel,
                new[] { scenario.CharacterId_Ref.VisualSetId_Ref }.Concat(scenario.MonsterIds_Ref.Select(item => item.VisualSetId_Ref)),
                new[] { scenario.CharacterId_Ref.DefaultSkillId_Ref })
        {
        }

        /// <summary>为正式关卡绑定角色、普通怪、事件精英、Boss 和全部可解锁技能的共享表现。</summary>
        /// <param name="world">必须与会话相同的存活世界。</param>
        /// <param name="session">正式单局实体所有者。</param>
        /// <param name="definition">TbStage 聚合的单位与技能目录。</param>
        /// <param name="resources">调用方拥有的共享资源。</param>
        /// <remarks>槽位根据 CombatUnit.VisualSetId 选择表现，动态扩容不会依赖生成顺序。</remarks>
        public CombatEntityVisuals(World world, CombatSession session, CombatRunDefinition definition, CombatVisualResources resources)
            : this(world, session, resources,
                definition.Presentation,
                definition.CombatRules.WorldUnitsPerPixel,
                new[] { definition.Character.VisualSetId_Ref }
                    .Concat(definition.Monsters.Select(item => item.VisualSetId_Ref))
                    .Concat(new[] { definition.Boss.MonsterId_Ref.VisualSetId_Ref }),
                definition.AvailableSkills)
        {
        }

        /// <summary>建立按真实配置 ID 查询的表现绑定并为既有实体附加渲染组件。</summary>
        /// <param name="world">会话所属世界。</param>
        /// <param name="session">实体与事件读取入口。</param>
        /// <param name="resources">已经完成共享加载的 ANI 资源。</param>
        /// <param name="presentation">提供 TbCombatPresentation.hitFlashDurationSecondsMilli。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel，用于表现节点像素偏移换算。</param>
        /// <param name="visualCatalog">本会话允许出现的 TbVisualSet。</param>
        /// <param name="skillCatalog">本会话允许发射的 TbSkill。</param>
        /// <remarks>只保存配置引用与 GPU 注册；不加载资源、不保存资产。</remarks>
        private CombatEntityVisuals(World world, CombatSession session, CombatVisualResources resources,
            CombatPresentationConfig presentation, float worldUnitsPerPixel,
            IEnumerable<VisualSetConfig> visualCatalog, IEnumerable<SkillConfig> skillCatalog)
        {
            this.world = world;
            manager = world.EntityManager; this.session = session; this.resources = resources;
            if (!(worldUnitsPerPixel > 0f)) throw new InvalidOperationException("Combat visual pixel scale must be positive.");
            this.worldUnitsPerPixel = worldUnitsPerPixel;
            visualSets = visualCatalog.Where(item => item != null).GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First());
            skills = skillCatalog.Where(item => item != null).GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First());
            players = Array.Empty<CombatVisualPlayer>();
            previousHealth = Array.Empty<long>();
            previousLifetimes = Array.Empty<ulong>();
            hitFlashUntilTicks = Array.Empty<ulong>();
            hitFlashDurationTicks = Math.Max(1UL, (ulong)Math.Ceiling(presentation.HitFlashDurationSeconds / session.StepSeconds));
            meshArray = new RenderMeshArray(resources.Materials, resources.Meshes);
            description = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            try
            {
                BindProjectiles();
                Synchronize();
            }
            catch { Dispose(); throw; }
        }

        /// <summary>独立调用时重新采集权威单位状态并同步表现，不复用上一帧快照。</summary>
        /// <remarks>同帧主体与反馈应使用接受只读借用的重载共享一次采集；不推进模拟或墙钟。</remarks>
        /// <exception cref="ObjectDisposedException">绑定已释放。</exception>
        public void Synchronize()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatEntityVisuals));
            Synchronize(session.CapturePresentationUnits());
        }

        /// <summary>消费当前同步帧的权威只读借用，按变化提交材质、网格、包围盒及可见状态。</summary>
        /// <param name="units">调用方刚从本会话采集、且尚未再次采集或改动单位槽的只读借用。</param>
        /// <remarks>不保存借用；动态绑定、白闪和技能跟随使用同一状态。仍需调用方推进World完成绘制。</remarks>
        /// <exception cref="ObjectDisposedException">绑定已释放。</exception>
        public void Synchronize(NativeArray<CombatUnit>.ReadOnly units)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatEntityVisuals));
            BindNewUnits(units);
            ulong tick = session.Statistics.Tick;
            if (tick < lastTick)
            {
                Array.Clear(hitFlashUntilTicks, 0, hitFlashUntilTicks.Length);
                seenImpacts.Clear();
                seenAreas.Clear();
                foreach (var impact in impacts) HideImpact(impact);
                foreach (var area in areas) HideArea(area);
                lastCastSequence = 0;
                foreach (var cast in casts) HideCast(cast);
            }
            lastTick = tick;
            for (int slot = 0; slot < players.Length; slot++)
            {
                CombatUnit unit = units[slot];
                UpdateHitFlash(slot, unit, tick);
                var player = players[slot];
                using (AniMarker.Auto()) player.Sample(unit, tick, session.StepSeconds);
                using (RenderWriteMarker.Auto())
                {
                    var entity = session.UnitEntity(slot);
                    ref UnitRenderState state = ref unitRenderStates[slot];
                    if (!state.Initialized || state.Visible != player.Visible)
                    {
                        bool hidden = manager.HasComponent<DisableRendering>(entity);
                        if (player.Visible && hidden) manager.RemoveComponent<DisableRendering>(entity);
                        else if (!player.Visible && !hidden) manager.AddComponent<DisableRendering>(entity);
                        state.Visible = player.Visible;
                    }
                    if (!player.Visible) continue;
                    int materialIndex = unit.Target.Health > 0 && tick < hitFlashUntilTicks[slot]
                        ? player.HitFlashMaterialIndex : player.MaterialIndex;
                    if (!state.Initialized || state.Mesh != player.MeshIndex || state.Material != materialIndex)
                        manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(materialIndex, player.MeshIndex));
                    if (!state.Initialized || state.Mesh != player.MeshIndex)
                    {
                        var bounds = resources.Meshes[player.MeshIndex].bounds;
                        manager.SetComponentData(entity, new RenderBounds { Value = new AABB { Center = bounds.center, Extents = bounds.extents } });
                    }
                    state.Mesh = player.MeshIndex;
                    state.Material = materialIndex;
                    state.Initialized = true;
                }
            }
            using (SkillMarker.Auto())
            {
                SynchronizeProjectiles();
                SynchronizeCasts(tick, units);
                SynchronizeImpacts(tick, units);
                SynchronizeAreas(tick, units);
            }
        }

        /// <summary>诊断和测试读取某单位当前选帧，不转移游标所有权。</summary>
        /// <param name="slot">会话单位槽。</param>
        /// <returns>当前播放器。</returns>
        public CombatVisualPlayer Read(int slot) => players[slot];

        /// <summary>诊断和验收读取指定单位是否正处于实际扣血触发的白闪窗口。</summary>
        /// <param name="slot">会话单位槽。</param>
        /// <returns>当前生命周期仍存活且模拟 tick 尚未越过白闪截止 tick 时为真。</returns>
        public bool IsHitFlashing(int slot) => session.ReadUnit(slot).Target.Health > 0 &&
            session.Statistics.Tick < hitFlashUntilTicks[slot];

        /// <summary>比较同一生命周期前后生命值，只在发生正数扣血时刷新白闪截止 tick。</summary>
        /// <param name="slot">会话单位槽。</param>
        /// <param name="unit">当前 CombatUnit 快照。</param>
        /// <param name="tick">CombatSession 已完成的模拟 tick。</param>
        /// <remarks>持续时间来自 TbCombatPresentation.hitFlashDurationSecondsMilli；生命周期切换、治疗、闪避、免疫和无敌均不会触发。</remarks>
        private void UpdateHitFlash(int slot, in CombatUnit unit, ulong tick)
        {
            if (previousLifetimes[slot] != unit.Target.Lifetime)
                hitFlashUntilTicks[slot] = 0;
            else if (unit.Target.Health > 0 && previousHealth[slot] > unit.Target.Health)
                hitFlashUntilTicks[slot] = tick + hitFlashDurationTicks;
            previousHealth[slot] = unit.Target.Health;
            previousLifetimes[slot] = unit.Target.Lifetime;
        }

        /// <summary>会话按批扩容后，为新增槽位建立播放器并绑定已加载的共享美术。</summary>
        /// <param name="units">本次同步帧的权威只读借用，不跨帧保存。</param>
        /// <remarks>单位类型读取 CombatUnit.VisualSetId；只在容量增长时分配播放器数组，不重复加载纹理、材质或网格。旧槽复用由生命周期变化重置动画。</remarks>
        private void BindNewUnits(NativeArray<CombatUnit>.ReadOnly units)
        {
            if (players.Length == session.UnitCount) return;
            int previous = players.Length;
            Array.Resize(ref players, session.UnitCount);
            Array.Resize(ref unitRenderStates, session.UnitCount);
            Array.Resize(ref previousHealth, session.UnitCount);
            Array.Resize(ref previousLifetimes, session.UnitCount);
            Array.Resize(ref hitFlashUntilTicks, session.UnitCount);
            for (int slot = previous; slot < players.Length; slot++)
            {
                CombatUnit unit = units[slot];
                int visualId = unit.VisualSetId;
                if (!visualSets.TryGetValue(visualId, out var visual))
                    throw new InvalidOperationException("Combat unit references a visual that was not preloaded: " + visualId);
                players[slot] = new CombatVisualPlayer(visual, resources);
                previousHealth[slot] = unit.Target.Health;
                previousLifetimes[slot] = unit.Target.Lifetime;
                RenderMeshUtility.AddComponents(session.UnitEntity(slot), manager, description, meshArray,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            }
        }

        /// <summary>初始化时为会话弹丸池附加共享 Entities Graphics 组件，失活槽保持隐藏。</summary>
        /// <remarks>不创建额外弹丸实体；生命周期和运动仍由 CombatSession 拥有。</remarks>
        private void BindProjectiles()
        {
            for (int slot = 0; slot < session.ProjectileCapacity; slot++)
            {
                var entity = session.ProjectileEntity(slot);
                RenderMeshUtility.AddComponents(entity, manager, description, meshArray,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                manager.AddComponent<DisableRendering>(entity);
            }
        }

        /// <summary>按 CombatProjectile 的技能、运动方向和模拟年龄播放飞行 ANI。</summary>
        /// <remarks>飞行片段由 TbSkill.projectileClipId 指定并在入局时共享加载；不在同步阶段读取资源。</remarks>
        private void SynchronizeProjectiles()
        {
            VisibleProjectileCount = 0;
            for (int slot = 0; slot < session.ProjectileCapacity; slot++)
            {
                var projectile = session.ReadProjectile(slot);
                var entity = session.ProjectileEntity(slot);
                SetVisible(entity, projectile.Active);
                if (!projectile.Active) continue;
                if (!skills.TryGetValue(projectile.SkillId, out var projectileSkill) || projectileSkill.ProjectileClipId_Ref == null)
                    throw new InvalidOperationException("Combat projectile references a skill visual that was not preloaded.");
                var clip = resources.Read(projectile.SkillId, projectileSkill.ProjectileClipId.Value);
                if (!projectileSkill.ProjectileVisualOffsetXPixels.HasValue ||
                    !projectileSkill.ProjectileVisualOffsetYPixels.HasValue ||
                    !projectileSkill.ProjectileVisualScale.HasValue || !(projectileSkill.ProjectileVisualScale.Value > 0f))
                    throw new InvalidOperationException("Combat projectile visual placement is incomplete: " + projectile.SkillId);
                int frame = clip.Animation.Sample(0, projectile.Age);
                ApplyFrame(entity, clip, frame, clip.FlipX);
                float angle = math.atan2(projectile.Velocity.y, projectile.Velocity.x);
                float2 localOffset = new float2(projectileSkill.ProjectileVisualOffsetXPixels.Value,
                    projectileSkill.ProjectileVisualOffsetYPixels.Value) * worldUnitsPerPixel;
                math.sincos(angle, out float sin, out float cos);
                float2 visualOffset = new float2(cos * localOffset.x - sin * localOffset.y,
                    sin * localOffset.x + cos * localOffset.y);
                manager.SetComponentData(entity, LocalTransform.FromPositionRotationScale(
                    new float3(projectile.Position + visualOffset, 0), quaternion.RotateZ(angle),
                    projectileSkill.ProjectileVisualScale.Value));
                VisibleProjectileCount++;
            }
        }

        /// <summary>消费一次实际释放事件并推进随身层，不依赖弹丸是否仍然存活。</summary>
        /// <param name="tick">本次同步的已完成模拟 tick，暂停时保持不变。</param>
        /// <param name="units">同帧单位只读借用；不跨帧保存，槽复用通过生命周期拒绝。</param>
        /// <remarks>片段、根节点局部偏移与缩放来自 TbSkill.castClipId/castVisualOffset/castVisualScalePermille。每次释放只播一层，不改变移动或伤害；时长取正式 ANI，结束回池，死亡立即隐藏。</remarks>
        private void SynchronizeCasts(ulong tick, NativeArray<CombatUnit>.ReadOnly units)
        {
            // 重开后可能已推进到与上次相同的 tick；用施法者代次重置游标，不能只靠 tick 回退。
            if (units.Length > 0 && units[0].Target.Lifetime != lastCastCasterLifetime)
            {
                lastCastCasterLifetime = units[0].Target.Lifetime;
                lastCastSequence = 0;
                foreach (CastVisual visual in casts) HideCast(visual);
            }
            for (int index = 0; index < session.CastCount; index++)
            {
                CombatCastEvent castEvent = session.ReadCast(index);
                if ((uint)castEvent.CasterSlot >= (uint)units.Length ||
                    units[castEvent.CasterSlot].Target.Lifetime != castEvent.CasterLifetime) continue;
                if (castEvent.AttackSequence <= lastCastSequence) continue;
                lastCastSequence = castEvent.AttackSequence;
                if (!skills.TryGetValue(castEvent.SkillId, out SkillConfig skill) || !skill.CastClipId.HasValue) continue;
                CastVisual visual = AcquireCast();
                visual.Active = true;
                visual.SkillId = skill.Id;
                visual.ClipId = skill.CastClipId.Value;
                visual.StartTick = castEvent.Tick;
                visual.CasterSlot = castEvent.CasterSlot;
                visual.CasterLifetime = castEvent.CasterLifetime;
                visual.Offset = new float2(skill.CastVisualOffsetXPixels.Value,
                    skill.CastVisualOffsetYPixels.Value) * worldUnitsPerPixel;
            }
            VisibleCastCount = 0;
            foreach (CastVisual visual in casts)
            {
                if (!visual.Active) continue;
                if ((uint)visual.CasterSlot >= (uint)units.Length ||
                    units[visual.CasterSlot].Target.Lifetime != visual.CasterLifetime ||
                    units[visual.CasterSlot].Target.Health <= 0)
                { HideCast(visual); continue; }
                CombatVisualResources.Clip clip = resources.Read(visual.SkillId, visual.ClipId);
                double age = (tick - visual.StartTick) * session.StepSeconds;
                if (age >= clip.Animation.DurationSeconds(0)) { HideCast(visual); continue; }
                manager.SetComponentData(visual.Entity, LocalTransform.FromPositionRotationScale(
                    new float3(units[visual.CasterSlot].Position + visual.Offset, 0), quaternion.identity,
                    skills[visual.SkillId].CastVisualScale.Value));
                ApplyFrame(visual.Entity, clip, clip.Animation.Sample(0, age), clip.FlipX);
                SetVisible(visual.Entity, true);
                VisibleCastCount++;
            }
        }

        /// <summary>取得空闲随身层实体，没有空槽时按真实并发扩容一个。</summary>
        /// <returns>可写入本次释放身份的池槽。</returns>
        /// <remarks>新实体只存在于当前 World，GPU 资源由共享集合拥有；Dispose 销毁实体，不保存资产。</remarks>
        private CastVisual AcquireCast()
        {
            foreach (CastVisual visual in casts) if (!visual.Active) return visual;
            Entity entity = manager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld));
            RenderMeshUtility.AddComponents(entity, manager, description, meshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            manager.AddComponent<DisableRendering>(entity);
            var created = new CastVisual { Entity = entity };
            casts.Add(created);
            return created;
        }

        /// <summary>消费本帧首次接触事件并推进所有仍在播放的非循环命中 ANI。</summary>
        /// <param name="tick">CombatSession 已完成的模拟 tick。</param>
        /// <param name="units">当前同步帧的权威单位只读借用；通过事件槽位和生命周期确认跟随目标。</param>
        /// <remarks>同一弹丸对同一目标生命周期只创建一次；挂点来自 TbCharacter/TbMonster.hitEffectOffset，局部偏移与缩放来自 TbSkill.impactVisualOffset/Scale，爆发按实际范围倍率缩放。存活同代目标每帧跟随，死亡或复用后停在最后位置播完；播放结束回池，不参与伤害或修改模拟。</remarks>
        private void SynchronizeImpacts(ulong tick, NativeArray<CombatUnit>.ReadOnly units)
        {
            for (int index = 0; index < session.ImpactCount; index++)
            {
                var impactEvent = session.ReadImpact(index);
                if (!seenImpacts.Add((impactEvent.AttackSequence, impactEvent.TargetLifetime))) continue;
                if (!skills.TryGetValue(impactEvent.SkillId, out var projectileSkill) || projectileSkill.ImpactClipId_Ref == null) continue;
                if (!projectileSkill.ImpactVisualOffsetXPixels.HasValue ||
                    !projectileSkill.ImpactVisualOffsetYPixels.HasValue ||
                    !projectileSkill.ImpactVisualScale.HasValue || !(projectileSkill.ImpactVisualScale.Value > 0f))
                    throw new InvalidOperationException("Combat impact visual placement is incomplete: " + impactEvent.SkillId);
                var visual = AcquireImpact();
                visual.Active = true;
                visual.SkillId = impactEvent.SkillId;
                visual.ClipId = projectileSkill.ImpactClipId.Value;
                visual.StartTick = impactEvent.Tick;
                visual.TargetSlot = impactEvent.TargetSlot;
                visual.TargetLifetime = impactEvent.TargetLifetime;
                visual.FollowsTarget = true;
                float2 offset = new float2(projectileSkill.ImpactVisualOffsetXPixels.Value,
                    projectileSkill.ImpactVisualOffsetYPixels.Value) * worldUnitsPerPixel;
                float radiusMultiplier = projectileSkill.CombatProfileId_Ref.ImpactRadiusPixels > 0 ? impactEvent.RadiusMultiplier : 1;
                if (!(radiusMultiplier > 0)) throw new InvalidOperationException("Impact explosion lost its radius snapshot.");
                visual.Offset = offset * radiusMultiplier;
                manager.SetComponentData(visual.Entity, LocalTransform.FromPositionRotationScale(
                    new float3(impactEvent.Position + visual.Offset, 0), quaternion.identity,
                    projectileSkill.ImpactVisualScale.Value * radiusMultiplier));
                SetVisible(visual.Entity, true);
            }

            VisibleImpactCount = 0;
            foreach (var visual in impacts)
            {
                if (!visual.Active) continue;
                var clip = resources.Read(visual.SkillId, visual.ClipId);
                double age = (tick - visual.StartTick) * session.StepSeconds;
                if (age >= clip.Animation.DurationSeconds(0))
                {
                    HideImpact(visual);
                    continue;
                }
                if (visual.FollowsTarget)
                {
                    if ((uint)visual.TargetSlot >= (uint)units.Length ||
                        units[visual.TargetSlot].Target.Lifetime != visual.TargetLifetime ||
                        units[visual.TargetSlot].Target.Health <= 0)
                        visual.FollowsTarget = false;
                    else
                    {
                        CombatUnit target = units[visual.TargetSlot];
                        LocalTransform transform = manager.GetComponentData<LocalTransform>(visual.Entity);
                        transform.Position = new float3(target.Position + target.HitEffectOffset + visual.Offset, 0);
                        manager.SetComponentData(visual.Entity, transform);
                    }
                }
                ApplyFrame(visual.Entity, clip, clip.Animation.Sample(0, age), clip.FlipX);
                VisibleImpactCount++;
            }
        }

        /// <summary>从命中表现池取得空闲实体；没有空槽时按真实并发扩容一个。</summary>
        /// <returns>可立即配置的池槽。</returns>
        /// <remarks>创建的实体由本表现绑定拥有并在 Dispose 时销毁。</remarks>
        private ImpactVisual AcquireImpact()
        {
            foreach (var visual in impacts) if (!visual.Active) return visual;
            var entity = manager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld));
            RenderMeshUtility.AddComponents(entity, manager, description, meshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            manager.AddComponent<DisableRendering>(entity);
            var created = new ImpactVisual { Entity = entity };
            impacts.Add(created);
            return created;
        }

        /// <summary>消费目标位置释放事件，并让一个技能配置的全部 Area 片段在同一位置同时播放。</summary>
        /// <param name="tick">CombatSession 已完成的模拟 tick。</param>
        /// <param name="units">当前同步帧单位借用，用于读取跟随拥有者。</param>
        /// <remarks>同一攻击序号只创建一次表现；按事件实际半径同步星级/被动范围缩放，每段结束后独立回池。跟随施法者的地面层锚定角色根脚点加 TbSkill.areaVisualOffset，不叠加身体碰撞中心；判定位置仍由模拟事件拥有。</remarks>
        private void SynchronizeAreas(ulong tick, NativeArray<CombatUnit>.ReadOnly units)
        {
            for (int index = 0; index < session.AreaCount; index++)
            {
                CombatAreaEvent areaEvent = session.ReadArea(index);
                if (!seenAreas.Add(areaEvent.AttackSequence)) continue;
                if (!skills.TryGetValue(areaEvent.SkillId, out SkillConfig skill) ||
                    skill.AreaClipIds_Ref == null || skill.AreaClipIds_Ref.Count == 0)
                    throw new InvalidOperationException("Combat area event references a skill visual that was not preloaded.");
                if (!skill.AreaVisualOffsetXPixels.HasValue || !skill.AreaVisualOffsetYPixels.HasValue ||
                    !skill.AreaVisualScale.HasValue || !(skill.AreaVisualScale.Value > 0f) ||
                    !skill.AreaRadiusPixels.HasValue || !(skill.AreaRadiusPixels.Value > 0f) ||
                    !(areaEvent.Radius > 0f) || float.IsNaN(areaEvent.Radius) || float.IsInfinity(areaEvent.Radius))
                    throw new InvalidOperationException("Combat area visual placement is incomplete: " + areaEvent.SkillId);
                float radiusMultiplier = areaEvent.Radius / (skill.AreaRadiusPixels.Value * worldUnitsPerPixel);
                float2 offset = new float2(skill.AreaVisualOffsetXPixels.Value,
                    skill.AreaVisualOffsetYPixels.Value) * worldUnitsPerPixel * radiusMultiplier;
                foreach (AnimationClipConfig clipConfig in skill.AreaClipIds_Ref)
                {
                    AreaVisual visual = AcquireArea();
                    visual.Active = true;
                    visual.SkillId = areaEvent.SkillId;
                    visual.ClipId = clipConfig.Id;
                    visual.StartTick = areaEvent.Tick;
                    visual.FollowsOwner = areaEvent.FollowsOwner;
                    visual.OwnerLifetime = areaEvent.OwnerLifetime;
                    visual.Offset = offset;
                    manager.SetComponentData(visual.Entity,
                        LocalTransform.FromPositionRotationScale(new float3(areaEvent.Position + offset, 0),
                            quaternion.identity, skill.AreaVisualScale.Value * radiusMultiplier));
                    SetVisible(visual.Entity, true);
                }
            }

            VisibleAreaCount = 0;
            foreach (AreaVisual visual in areas)
            {
                if (!visual.Active) continue;
                CombatVisualResources.Clip clip = resources.Read(visual.SkillId, visual.ClipId);
                if (visual.FollowsOwner)
                {
                    CombatUnit owner = units[0];
                    if (owner.Target.Health <= 0 || owner.Target.Lifetime != visual.OwnerLifetime)
                    { HideArea(visual); continue; }
                    LocalTransform transform = manager.GetComponentData<LocalTransform>(visual.Entity);
                    transform.Position = new float3(owner.Position + visual.Offset, 0);
                    manager.SetComponentData(visual.Entity, transform);
                }
                double age = (tick - visual.StartTick) * session.StepSeconds;
                if (age >= clip.Animation.DurationSeconds(0))
                {
                    HideArea(visual);
                    continue;
                }
                ApplyFrame(visual.Entity, clip, clip.Animation.Sample(0, age), clip.FlipX);
                VisibleAreaCount++;
            }
        }

        /// <summary>从范围表现池取得空闲实体，没有空槽时按实际片段并发创建一个。</summary>
        /// <returns>可立即设置技能、片段与开始 tick 的池槽。</returns>
        /// <remarks>实体只存在于运行时 World，由本绑定 Dispose 销毁，不生成或保存资产。</remarks>
        private AreaVisual AcquireArea()
        {
            foreach (AreaVisual visual in areas) if (!visual.Active) return visual;
            Entity entity = manager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld));
            RenderMeshUtility.AddComponents(entity, manager, description, meshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            manager.AddComponent<DisableRendering>(entity);
            var created = new AreaVisual { Entity = entity };
            areas.Add(created);
            return created;
        }

        /// <summary>把共享片段的材质、帧网格及包围盒写入一个表现实体。</summary>
        /// <param name="entity">已绑定 RenderMeshArray 的实体。</param>
        /// <param name="clip">CombatVisualResources 中的共享片段。</param>
        /// <param name="globalFrame">CombatAnimation 采样的 ANI 全局帧。</param>
        /// <param name="flipX">是否使用预建镜像网格。</param>
        private void ApplyFrame(Entity entity, CombatVisualResources.Clip clip, int globalFrame, bool flipX)
        {
            int meshIndex = clip.MeshIndices[globalFrame * 2 + (flipX ? 1 : 0)];
            manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(clip.MaterialIndex, meshIndex));
            var bounds = resources.Meshes[meshIndex].bounds;
            manager.SetComponentData(entity, new RenderBounds { Value = new AABB { Center = bounds.center, Extents = bounds.extents } });
        }

        /// <summary>切换 Entities Graphics 禁用标记，重复设置不触发结构变化。</summary>
        /// <param name="entity">会话或命中池拥有的表现实体。</param>
        /// <param name="visible">是否应参与绘制。</param>
        private void SetVisible(Entity entity, bool visible)
        {
            bool hidden = manager.HasComponent<DisableRendering>(entity);
            if (visible && hidden) manager.RemoveComponent<DisableRendering>(entity);
            else if (!visible && !hidden) manager.AddComponent<DisableRendering>(entity);
        }

        /// <summary>结束一个命中表现并保留实体供后续首次接触复用。</summary>
        /// <param name="visual">当前池槽。</param>
        private void HideImpact(ImpactVisual visual)
        {
            visual.Active = false;
            if (world.IsCreated && manager.Exists(visual.Entity)) SetVisible(visual.Entity, false);
        }

        /// <summary>结束随身施法层并隐藏实体，后续施法可以复用同一池槽。</summary>
        /// <param name="visual">本绑定拥有的随身层池槽。</param>
        private void HideCast(CastVisual visual)
        {
            visual.Active = false;
            if (world.IsCreated && manager.Exists(visual.Entity)) SetVisible(visual.Entity, false);
        }

        /// <summary>结束一个目标位置片段并保留实体，供后续任意 Area 片段复用。</summary>
        /// <param name="visual">当前范围表现池槽。</param>
        private void HideArea(AreaVisual visual)
        {
            visual.Active = false;
            if (world.IsCreated && manager.Exists(visual.Entity)) SetVisible(visual.Entity, false);
        }

        /// <summary>会话销毁前解除渲染资源引用并禁用绘制，允许重复调用。</summary>
        /// <remarks>只移除本绑定使用的渲染入口；会话随后销毁实体，资源所有者再释放网格和材质。Unity 退出 Play Mode 可能先销毁 World，此时实体及 GPU 注册已随 World 回收，不再访问失效的 EntityManager。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (!world.IsCreated) return;
            for (int slot = 0; slot < players.Length; slot++)
            {
                var entity = session.UnitEntity(slot);
                if (!manager.Exists(entity)) continue;
                if (manager.HasComponent<MaterialMeshInfo>(entity)) manager.RemoveComponent<MaterialMeshInfo>(entity);
                if (manager.HasComponent<RenderMeshArray>(entity)) manager.RemoveComponent<RenderMeshArray>(entity);
            }
            for (int slot = 0; slot < session.ProjectileCapacity; slot++)
            {
                var entity = session.ProjectileEntity(slot);
                if (!manager.Exists(entity)) continue;
                if (manager.HasComponent<MaterialMeshInfo>(entity)) manager.RemoveComponent<MaterialMeshInfo>(entity);
                if (manager.HasComponent<RenderMeshArray>(entity)) manager.RemoveComponent<RenderMeshArray>(entity);
            }
            foreach (var impact in impacts)
                if (manager.Exists(impact.Entity)) manager.DestroyEntity(impact.Entity);
            foreach (var area in areas)
                if (manager.Exists(area.Entity)) manager.DestroyEntity(area.Entity);
            foreach (var cast in casts)
                if (manager.Exists(cast.Entity)) manager.DestroyEntity(cast.Entity);
            casts.Clear(); lastCastSequence = 0;
            impacts.Clear(); seenImpacts.Clear(); areas.Clear(); seenAreas.Clear();
        }
    }
}
