using System;
using cfg;
using Roguelike.Features.Combat.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>向会话实体绑定 Entities Graphics 共享帧资源；不创建每单位 GameObject。</summary>
    public sealed class CombatEntityVisuals : IDisposable
    {
        private readonly EntityManager manager;
        private readonly CombatSession session;
        private readonly CombatVisualResources resources;
        private CombatVisualPlayer[] players;
        private readonly PerformanceScenarioConfig scenario;
        private readonly RenderMeshArray meshArray;
        private readonly RenderMeshDescription description;
        private bool disposed;

        /// <summary>资源准备完成后为本局单位附加共享渲染组件，并采样出生帧。</summary>
        /// <param name="world">必须与会话相同的存活世界。</param>
        /// <param name="session">本局实体所有者。</param>
        /// <param name="scenario">与会话相同的场景配置。</param>
        /// <param name="resources">调用方拥有的共享资源。</param>
        /// <remarks>修改会话单位渲染组件；自有世界退出顺序为本绑定、会话、世界、共享资源，先清理 GPU 注册。不修改 Scene/Prefab。</remarks>
        public CombatEntityVisuals(World world, CombatSession session, PerformanceScenarioConfig scenario, CombatVisualResources resources)
        {
            manager = world.EntityManager; this.session = session; this.resources = resources;
            this.scenario = scenario;
            players = Array.Empty<CombatVisualPlayer>();
            meshArray = new RenderMeshArray(resources.Materials, resources.Meshes);
            description = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            try
            {
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

        /// <summary>会话销毁前解除渲染资源引用并禁用绘制，允许重复调用。</summary>
        /// <remarks>只移除本绑定使用的渲染入口；会话随后销毁实体，资源所有者再释放网格和材质。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            for (int slot = 0; slot < players.Length; slot++)
            {
                var entity = session.UnitEntity(slot);
                if (!manager.Exists(entity)) continue;
                if (manager.HasComponent<MaterialMeshInfo>(entity)) manager.RemoveComponent<MaterialMeshInfo>(entity);
                if (manager.HasComponent<RenderMeshArray>(entity)) manager.RemoveComponent<RenderMeshArray>(entity);
            }
        }
    }
}
