using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>读取正式切片状态在内存绘制雷坛、锁点预警及阵域；不生成任何预览资产。</summary>
    public sealed class CombatTribulationMarkers : IDisposable
    {
        // 技术离散精度，不承担策划范围/数量规则；范围、宽度及颜色均读取TbTribulationRule。
        private const int CircleVertices = 64;
        private readonly CombatRunCoordinator coordinator;
        private readonly GameObject root;
        private readonly Material material;
        private readonly Dictionary<string, LineRenderer> circles = new Dictionary<string, LineRenderer>();
        private readonly HashSet<string> visible = new HashSet<string>();
        private readonly Color warning, altar, domain, spent;
        private bool disposed;
#if UNITY_EDITOR
        private LineRenderer arenaBoundary;
        private Material arenaBoundaryMaterial;
#endif

        /// <summary>当前运行器就绪后建立临时绘制所有者；材质仅驻留内存并随Dispose释放。</summary>
        /// <param name="coordinator">当前正式协调器。</param><param name="parent">运行器根节点。</param>
        /// <remarks>不调用AssetDatabase、不序列化对象；Sprites/Default为引擎技术着色器。</remarks>
        /// <exception cref="InvalidOperationException">源表颜色或引擎着色器不可用。</exception>
        public CombatTribulationMarkers(CombatRunCoordinator coordinator, Transform parent)
        {
            this.coordinator = coordinator;
            var config = coordinator.Tribulation.Config.RuleId_Ref;
            if (!ColorUtility.TryParseHtmlString(config.WarningColor, out warning) || !ColorUtility.TryParseHtmlString(config.AltarColor, out altar) ||
                !ColorUtility.TryParseHtmlString(config.DomainColor, out domain) || !ColorUtility.TryParseHtmlString(config.SpentColor, out spent))
                throw new InvalidOperationException("TbTribulationRule: invalid marker colors.");
            var shader = Shader.Find("Sprites/Default") ?? throw new InvalidOperationException("Marker shader is unavailable.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            root = new GameObject("Tribulation markers"); root.transform.SetParent(parent, false);
        }

        /// <summary>每个表现帧读取权威状态绘制；锁点使用既定中心，不跟随玩家，已清除领域立即移出。</summary>
        /// <remarks>复用相同标识的LineRenderer，回收消失标识；不改变模拟或创建持久资产。</remarks>
        public void Synchronize()
        {
            if (disposed) return; visible.Clear();
            var state = coordinator.Tribulation; var rule = state.Config.RuleId_Ref;
            bool running = state.State != Run.TribulationTrialState.Terminated;
            if (running)
            {
                foreach (var item in rule.Altars)
                    Draw("altar-" + item.Id, item.XPixels, item.YPixels, rule.AltarRadiusPixels,
                        state.SpentAltars.Contains(item.Id) || state.ElapsedMilli < item.AvailableAfterMilli ? spent : altar);
                foreach (var strike in state.Warnings)
                    Draw("warning-" + strike.Sequence, strike.XPixels, strike.YPixels, strike.RadiusPixels, warning);
                if (coordinator.Domains != null)
                    foreach (var field in coordinator.Domains.Domains)
                        Draw("domain-" + field.Id, field.XPixels, field.YPixels, state.Config.SignatureId_Ref.DomainRadiusPixels, domain);
                var player = coordinator.Session.ReadUnit(0);
                if (player.Target.Guard.IsActive(coordinator.Session.Statistics.Tick * coordinator.Session.StepSeconds))
                {
                    float scale = coordinator.Model.Definition.CombatRules.WorldUnitsPerPixel;
                    Draw("guard", player.Position.x / scale, player.Position.y / scale, state.Config.SignatureId_Ref.CounterRadiusPixels, altar);
                }
            }
            foreach (string key in circles.Keys.Where(k => !visible.Contains(k)).ToArray())
            { UnityEngine.Object.Destroy(circles[key].gameObject); circles.Remove(key); }
        }

        /// <summary>按源表逻辑像素设置内存圆环几何和颜色；同一ID只创建一次对象。</summary>
        /// <param name="key">技术复用标识。</param><param name="x">中心横像素。</param><param name="y">中心纵像素。</param>
        /// <param name="radius">表内范围像素。</param><param name="color">已解析表内颜色。</param>
        /// <remarks>只修改内存对象；顶点位于战斗XY平面，排序使用正式角色的同一世界。</remarks>
        private void Draw(string key, double x, double y, double radius, Color color)
        {
            visible.Add(key);
            if (!circles.TryGetValue(key, out var line))
            {
                var item = new GameObject(key); item.transform.SetParent(root.transform, false);
                line = item.AddComponent<LineRenderer>(); line.sharedMaterial = material;
                line.useWorldSpace = true; line.loop = true; line.positionCount = CircleVertices;
                line.sortingOrder = 5; circles.Add(key, line);
            }
            float scale = coordinator.Model.Definition.CombatRules.WorldUnitsPerPixel;
            line.startWidth = line.endWidth = coordinator.Tribulation.Config.RuleId_Ref.LineWidthPixels * scale;
            line.startColor = line.endColor = color;
            for (int i = 0; i < CircleVertices; i++)
            {
                double angle = i * Math.PI * 2 / CircleVertices;
                line.SetPosition(i, new Vector3((float)(x + Math.Cos(angle) * radius) * scale, (float)(y + Math.Sin(angle) * radius) * scale, 0));
            }
        }

#if UNITY_EDITOR
        /// <summary>编辑器试玩期间常驻绘制真实行走场地红框，暂停及升级选择时仍按摄像机保持显示线宽。</summary>
        /// <param name="camera">本局正交摄像机，用于将两个显示像素转换为世界线宽。</param>
        /// <remarks>几何读取 TbMap.arenaHalfWidthPixels/arenaHalfHeightPixels 与 TbCombatRules.worldUnitsPerPixel；红色、两显示像素和最前队列仅为本次临时诊断样式。只创建内存线条与独占材质，由 Dispose 回收；不保存 Scene/Prefab 或生成预览资产，不参与正式构建。</remarks>
        public void SynchronizeArenaBoundary(Camera camera)
        {
            if (disposed || camera == null || camera.pixelHeight <= 0) return;
            if (arenaBoundary == null)
            {
                arenaBoundaryMaterial = new Material(material)
                {
                    name = "Arena Boundary Debug (Editor Only)",
                    hideFlags = HideFlags.HideAndDontSave,
                    renderQueue = 5000
                };
                var item = new GameObject("[Debug] Walkable Arena Boundary") { hideFlags = HideFlags.DontSave };
                item.transform.SetParent(root.transform, false);
                arenaBoundary = item.AddComponent<LineRenderer>();
                arenaBoundary.sharedMaterial = arenaBoundaryMaterial;
                arenaBoundary.useWorldSpace = false;
                arenaBoundary.loop = true;
                arenaBoundary.positionCount = 4;
                arenaBoundary.alignment = LineAlignment.View;
                arenaBoundary.sortingOrder = short.MaxValue;
                arenaBoundary.startColor = arenaBoundary.endColor = Color.red;
                var definition = coordinator.Model.Definition;
                float scale = definition.CombatRules.WorldUnitsPerPixel;
                float x = definition.Map.ArenaHalfWidthPixels * scale;
                float y = definition.Map.ArenaHalfHeightPixels * scale;
                arenaBoundary.SetPositions(new[] { new Vector3(-x, -y, 0), new Vector3(x, -y, 0),
                    new Vector3(x, y, 0), new Vector3(-x, y, 0) });
            }
            arenaBoundary.startWidth = arenaBoundary.endWidth = camera.orthographicSize * 4f / camera.pixelHeight;
        }
#endif

        /// <summary>关闭当前运行器时释放所有临时线条、共享内存材质及编辑器边界诊断材质；重复调用安全。</summary>
        /// <remarks>不删除磁盘资产；先禁用所有者，Unity在帧末销毁对象。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            root.SetActive(false); UnityEngine.Object.Destroy(root); UnityEngine.Object.Destroy(material); circles.Clear(); visible.Clear();
#if UNITY_EDITOR
            if (arenaBoundaryMaterial != null) UnityEngine.Object.Destroy(arenaBoundaryMaterial);
            arenaBoundary = null; arenaBoundaryMaterial = null;
#endif
        }
    }
}
