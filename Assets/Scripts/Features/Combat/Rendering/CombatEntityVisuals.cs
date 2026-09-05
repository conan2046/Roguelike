using System;
using System.Collections.Generic;
using cfg;
using Roguelike.Features.Combat.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>向会话实体绑定 Entities Graphics 共享帧资源；不创建每单位 GameObject。</summary>
    public sealed class CombatEntityVisuals : IDisposable
    {
        private readonly EntityManager manager;
        private readonly World world;
        private readonly CombatSession session;
        private readonly CombatVisualResources resources;
        private CombatVisualPlayer[] players;
        private readonly PerformanceScenarioConfig scenario;
        private readonly RenderMeshArray meshArray;
        private readonly RenderMeshDescription description;
        private readonly SkillConfig projectileSkill;
        private readonly List<ImpactVisual> impacts = new List<ImpactVisual>();
        private readonly HashSet<ulong> seenImpacts = new HashSet<ulong>();
        private bool disposed;
        private ulong lastTick;
        public int VisibleProjectileCount { get; private set; }
        public int VisibleImpactCount { get; private set; }

        /// <summary>命中 ANI 的池化播放状态；实体只在池扩容时创建。</summary>
        private sealed class ImpactVisual
        {
            public Entity Entity;
            public int SkillId, ClipId;
            public ulong StartTick;
            public bool Active;
        }

        /// <summary>资源准备完成后为本局单位附加共享渲染组件，并采样出生帧。</summary>
        /// <param name="world">必须与会话相同的存活世界。</param>
        /// <param name="session">本局实体所有者。</param>
        /// <param name="scenario">与会话相同的场景配置。</param>
        /// <param name="resources">调用方拥有的共享资源。</param>
        /// <remarks>修改会话单位渲染组件；自有世界退出顺序为本绑定、会话、世界、共享资源，先清理 GPU 注册。不修改 Scene/Prefab。</remarks>
        public CombatEntityVisuals(World world, CombatSession session, PerformanceScenarioConfig scenario, CombatVisualResources resources)
        {
            this.world = world;
            manager = world.EntityManager; this.session = session; this.resources = resources;
            this.scenario = scenario;
            projectileSkill = scenario.CharacterId_Ref.DefaultSkillId_Ref;
            players = Array.Empty<CombatVisualPlayer>();
            meshArray = new RenderMeshArray(resources.Materials, resources.Meshes);
            description = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            try
            {
                BindProjectiles();
                Synchronize();
            }
            catch { Dispose(); throw; }
        }

        /// <summary>每次会话 Advance 返回后同步帧索引、帧包围盒和可见状态，不分配材质或网格。</summary>
        /// <remarks>调用方仍须推进世界的 Transform/Presentation 系统完成实际绘制；本方法不推进模拟和墙钟。</remarks>
        /// <exception cref="ObjectDisposedException">绑定已释放。</exception>
        public void Synchronize()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatEntityVisuals));
            BindNewUnits();
            ulong tick = session.Statistics.Tick;
            if (tick < lastTick)
            {
                seenImpacts.Clear();
                foreach (var impact in impacts) HideImpact(impact);
            }
            lastTick = tick;
            for (int slot = 0; slot < players.Length; slot++)
            {
                var player = players[slot];
                player.Sample(session.ReadUnit(slot), tick, session.StepSeconds);
                var entity = session.UnitEntity(slot);
                bool hidden = manager.HasComponent<DisableRendering>(entity);
                if (player.Visible && hidden) manager.RemoveComponent<DisableRendering>(entity);
                else if (!player.Visible && !hidden) manager.AddComponent<DisableRendering>(entity);
                if (!player.Visible) continue;
                manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(player.MaterialIndex, player.MeshIndex));
                var bounds = resources.Meshes[player.MeshIndex].bounds;
                manager.SetComponentData(entity, new RenderBounds { Value = new AABB { Center = bounds.center, Extents = bounds.extents } });
            }
            SynchronizeProjectiles();
            SynchronizeImpacts(tick);
        }

        /// <summary>诊断和测试读取某单位当前选帧，不转移游标所有权。</summary>
        /// <param name="slot">会话单位槽。</param>
        /// <returns>当前播放器。</returns>
        public CombatVisualPlayer Read(int slot) => players[slot];

        /// <summary>会话按批扩容后，为新增槽位建立播放器并绑定已加载的共享美术。</summary>
        /// <remarks>怪物类型依照 TbPerformanceScenario.monsterIds 槽位轮转；只在容量增长时分配播放器数组，不重复加载纹理、材质或网格。旧槽复用由生命周期变化重置动画。</remarks>
        private void BindNewUnits()
        {
            if (players.Length == session.UnitCount) return;
            int previous = players.Length;
            Array.Resize(ref players, session.UnitCount);
            for (int slot = previous; slot < players.Length; slot++)
            {
                var visual = slot == 0 ? scenario.CharacterId_Ref.VisualSetId_Ref :
                    scenario.MonsterIds_Ref[(slot - 1) % scenario.MonsterIds_Ref.Count].VisualSetId_Ref;
                players[slot] = new CombatVisualPlayer(visual, resources);
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
                if (projectile.SkillId != projectileSkill.Id || projectileSkill.ProjectileClipId_Ref == null)
                    throw new InvalidOperationException("Combat projectile references a skill visual that was not preloaded.");
                var clip = resources.Read(projectile.SkillId, projectileSkill.ProjectileClipId.Value);
                int frame = clip.Animation.Sample(0, projectile.Age);
                ApplyFrame(entity, clip, frame, false);
                float angle = math.atan2(projectile.Velocity.y, projectile.Velocity.x);
                manager.SetComponentData(entity, LocalTransform.FromPositionRotationScale(
                    new float3(projectile.Position, 0), quaternion.RotateZ(angle), 1));
                VisibleProjectileCount++;
            }
        }

        /// <summary>消费本帧首次接触事件并推进所有仍在播放的非循环命中 ANI。</summary>
        /// <param name="tick">CombatSession 已完成的模拟 tick。</param>
        /// <remarks>同一攻击序号只创建一次表现；播放结束隐藏并复用实体，不影响伤害结算。</remarks>
        private void SynchronizeImpacts(ulong tick)
        {
            for (int index = 0; index < session.ImpactCount; index++)
            {
                var impactEvent = session.ReadImpact(index);
                if (!seenImpacts.Add(impactEvent.AttackSequence)) continue;
                if (impactEvent.SkillId != projectileSkill.Id || projectileSkill.ImpactClipId_Ref == null) continue;
                var visual = AcquireImpact();
                visual.Active = true;
                visual.SkillId = impactEvent.SkillId;
                visual.ClipId = projectileSkill.ImpactClipId.Value;
                visual.StartTick = impactEvent.Tick;
                manager.SetComponentData(visual.Entity, LocalTransform.FromPosition(new float3(impactEvent.Position, 0)));
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
                ApplyFrame(visual.Entity, clip, clip.Animation.Sample(0, age), false);
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
            impacts.Clear(); seenImpacts.Clear();
        }
    }
}
