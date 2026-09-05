using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using cfg;
using ProjectX.Migration;
using Roguelike.Animation;
using Roguelike.Animation.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>从 Luban 和源 PNG/ANI 按需生成编辑外观；仅内存缓存，绝不序列化纹理副本。</summary>
    [InitializeOnLoad]
    public static class CombatAuthoringPreview
    {
        /// <summary>由缓存独占的临时 GPU 资源；Prefab 不保存任何引用。</summary>
        public sealed class Appearance
        {
            public Mesh Mesh;
            public Material Material;
            public Texture2D Texture;
        }

        private static readonly Dictionary<string, Appearance> Cache = new Dictionary<string, Appearance>();
        private static readonly HashSet<int> Failures = new HashSet<int>();
        private static Tables tables;

        /// <summary>编辑器载入程序集时注册资源回收；源资产变化后下次绘制重新读取配置。</summary>
        /// <remarks>域重载、退出和项目变更均销毁本工具自有的临时资源，不修改 Scene/Prefab。</remarks>
        static CombatAuthoringPreview()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
            EditorApplication.projectChanged += Clear;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <summary>切换 Play 状态时释放编辑外观，避免编辑资源常驻正式运行会话。</summary>
        /// <param name="state">Unity 发出的编辑器状态转换。</param>
        /// <remarks>只销毁本工具内存缓存，返回编辑模式后按需重建。</remarks>
        private static void OnPlayModeChanged(PlayModeStateChange state) => Clear();

        /// <summary>源资源变化或编辑器关闭时回收全部缓存；可重复调用。</summary>
        /// <remarks>仅销毁 HideAndDontSave 对象，不访问磁盘资产的生命周期。</remarks>
        public static void Clear()
        {
            foreach (var value in Cache.Values)
            {
                UnityEngine.Object.DestroyImmediate(value.Mesh);
                UnityEngine.Object.DestroyImmediate(value.Material);
                UnityEngine.Object.DestroyImmediate(value.Texture);
            }
            Cache.Clear(); Failures.Clear(); tables = null;
        }

        /// <summary>按 TbVisualSet、TbCombatRules 与 TbCombatPresentation 生成首帧；同一配置共享资源。</summary>
        /// <param name="visual">外观表的片段引用及缩放。</param>
        /// <param name="rules">世界单位换算来源。</param>
        /// <param name="shaderName">表现表指定的 Shader。</param>
        /// <returns>本工具独占的缓存，调用方不得持久化或销毁。</returns>
        /// <exception cref="InvalidOperationException">源 PNG 无法解析或 Shader 缺失。</exception>
        /// <remarks>只读取源资源；异常路径同样回收已创建资源。</remarks>
        public static Appearance Get(VisualSetConfig visual, CombatRulesConfig rules, string shaderName)
        {
            string key = visual.Id + ":" + rules.Id + ":" + shaderName;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var value = new Appearance();
            try
            {
                var clip = visual.StandClipId_Ref ?? visual.ClipIds_Ref.First();
                var data = CocosAniData.Parse(File.ReadAllBytes("Assets/StreamingAssets/" + clip.AniResourceId_Ref.Path));
                value.Texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                if (!ImageConversion.LoadImage(value.Texture, File.ReadAllBytes("Assets/StreamingAssets/" + clip.TextureResourceId_Ref.Path), true))
                    throw new InvalidOperationException("无法解析编辑预览 PNG。");
                value.Mesh = AniMeshFactory.CreateFrame(AniFrameLayout.Build(data, 0, 0, value.Texture.width, value.Texture.height),
                    rules.WorldUnitsPerPixel * visual.ScalePermille / 1000f);
                value.Mesh.hideFlags = HideFlags.HideAndDontSave;
                var shader = Shader.Find(shaderName);
                if (shader == null) throw new InvalidOperationException("编辑预览 Shader 缺失：" + shaderName);
                value.Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = (int)RenderQueue.Transparent };
                value.Material.SetTexture("_BaseMap", value.Texture); value.Material.SetColor("_BaseColor", Color.white);
                value.Material.SetFloat("_Surface", 1); value.Material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                value.Material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); value.Material.SetFloat("_ZWrite", 0);
                value.Material.SetFloat("_Cull", (float)CullMode.Off); value.Material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                Cache.Add(key, value);
                return value;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(value.Mesh);
                UnityEngine.Object.DestroyImmediate(value.Material);
                UnityEngine.Object.DestroyImmediate(value.Texture);
                throw;
            }
        }

        /// <summary>Scene/Prefab 视图绘制本体外观，使用角色或怪物的表引用。</summary>
        /// <param name="shape">保留用户微调的圆柱组件。</param><param name="flags">Unity 绘制选择状态。</param>
        /// <remarks>Gizmos 关闭时不绘制，不创建节点或污染保存状态。</remarks>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawActor(CombatCylinderAuthoring shape, GizmoType flags)
        {
            Draw(shape, shape.transform.parent, shape.ConfigId, shape.IsHero, false, shape.CombatRulesId);
        }

        /// <summary>Scene/Prefab 视图绘制独立武器；其所有者和外观来自 TbHeroWeapon。</summary>
        /// <param name="weapon">武器表标识。</param><param name="flags">Unity 绘制选择状态。</param>
        /// <remarks>不添加碰撞，不修改武器节点。</remarks>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawWeapon(HeroWeaponAuthoring weapon, GizmoType flags)
        {
            Draw(weapon, weapon.transform, weapon.WeaponConfigId, false, true, null);
        }

        /// <summary>编辑视图按真实预览节点矩阵绘制；缺失资源仅报告一次，源文件更新后重试。</summary>
        /// <param name="owner">用于归属诊断的组件。</param><param name="root">预制体根节点。</param>
        /// <param name="id">角色、怪物或武器 ID。</param><param name="hero">是否角色本体。</param>
        /// <param name="weapon">是否独立武器。</param><param name="rulesId">圆柱记录的规则 ID，武器使用战斗场景规则。</param>
        /// <remarks>不在 Play 模式加载编辑缓存，不写任何序列化字段。</remarks>
        private static void Draw(Component owner, Transform root, int id, bool hero, bool weapon, int? rulesId)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || root == null || Failures.Contains(owner.GetInstanceID())) return;
            try
            {
                tables = tables ?? CombatCylinderPipeline.ReadTables();
                var scenario = tables.TbPerformanceScenario.DataList.First(s => s.Kind == EPerformanceKind.Combat);
                var visual = weapon ? tables.TbHeroWeapon.Get(id).VisualSetId_Ref : hero ? tables.TbCharacter.Get(id).VisualSetId_Ref : tables.TbMonster.Get(id).VisualSetId_Ref;
                var appearance = Get(visual, rulesId.HasValue ? tables.TbCombatRules.Get(rulesId.Value) : scenario.CombatRulesId_Ref, scenario.PresentationId_Ref.ShaderName);
                var preview = root.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("VisualPreview", StringComparison.Ordinal));
                if (appearance.Material.SetPass(0)) Graphics.DrawMeshNow(appearance.Mesh, (preview ?? root).localToWorldMatrix);
            }
            catch (Exception error)
            {
                Failures.Add(owner.GetInstanceID());
                Debug.LogException(error, owner);
            }
        }
    }
}
