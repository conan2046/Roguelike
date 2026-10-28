using Roguelike.Features.Combat.Rendering;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class CombatRuntimeRunner
    {
        private CombatArenaBoundaryVisuals arenaBoundary;
        /// <summary>当前运行器的实际行走边界线实例；纯菜单或初始化前没有实例。</summary>
        public GameObject ArenaBoundaryRoot => arenaBoundary?.Root;
        /// <summary>本局边界显示状态；不代表碰撞开关，也不持久化设置。</summary>
        public bool ArenaBoundaryVisible => arenaBoundary != null && arenaBoundary.Visible;

        /// <summary>相机和会话就绪后为所有战斗入口建立通用边界，默认显示与样式读取 TbCombatPresentation。</summary>
        /// <remarks>拥有内存线条和材质；退出时先于相机、会话释放，不新增 Scene、Prefab 或预览资产。</remarks>
        /// <exception cref="System.InvalidOperationException">移动边界、显示配置或 Shader 无效。</exception>
        private void InitializeArenaBoundary()
        {
            arenaBoundary = new CombatArenaBoundaryVisuals(Session.MovementBoundsHalfSize, presentation, transform);
            arenaBoundary.Synchronize(ViewCamera);
        }

        /// <summary>后续设置页面可在战斗就绪后调用统一开关；即时隐藏／恢复，重开同局保留显示选择。</summary>
        /// <param name="visible">期望的边界可见性；不改变地图、摄像机、移动或碰撞。</param>
        /// <remarks>只修改本运行器拥有的可视对象，不写玩家存档；退出后新入口重新读取表内默认值。</remarks>
        public void SetArenaBoundaryVisible(bool visible)
        {
            if (arenaBoundary == null) return;
            arenaBoundary.SetVisible(visible); arenaBoundary.Synchronize(ViewCamera);
        }
    }
}
