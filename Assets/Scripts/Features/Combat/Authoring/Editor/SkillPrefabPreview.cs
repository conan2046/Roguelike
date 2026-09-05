using System;
using System.IO;
using System.Linq;
using cfg;
using ProjectX.Migration;
using Roguelike.Animation;
using Roguelike.Animation.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>打开技能 Prefab Stage 时从原始 ANI/PNG 创建内存预览；不生成、复制或保存任何预览资产。</summary>
    [InitializeOnLoad]
    public static class SkillPrefabPreview
    {
        private static GameObject preview;
        private static Mesh mesh;
        private static Material material;
        private static Texture2D texture;
        private static PrefabStage observedStage;
        private static bool creationFailed;

        /// <summary>Unity 编辑器域加载后注册预制体打开/关闭事件；重载前释放临时资源。</summary>
        /// <remarks>仅编辑器事件订阅；不更改正式场景或运行时资源。</remarks>
        static SkillPrefabPreview()
        {
            PrefabStage.prefabStageOpened += QueueOpen;
            PrefabStage.prefabStageClosing += Close;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
            EditorApplication.update += EnsureCurrent;
            EditorApplication.delayCall += RefreshCurrent;
        }

        /// <summary>编辑器更新时检查隔离场景是否已恢复；修复域重载后 Stage 晚于 delayCall 恢复而漏建预览的问题。</summary>
        /// <remarks>仅场景变化或临时对象丢失时读取配置与资源；失败后等待手动刷新或切换，避免逐帧重复报错。</remarks>
        private static void EnsureCurrent()
        {
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != observedStage)
            {
                observedStage = stage;
                creationFailed = false;
                Clear();
            }
            if (stage == null || preview != null || creationFailed || stage.prefabContentsRoot == null) return;
            creationFailed = true;
            if (stage.prefabContentsRoot.GetComponentInChildren<SkillCollisionAuthoring>(true) != null) Open(stage);
        }

        /// <summary>Unity 打开预制体后延迟到隔离场景与视图初始化完成，再读取当前 Stage 创建预览。</summary>
        /// <param name="stage">刚打开的隔离场景；回调执行时重新取当前 Stage，避免快速切换后访问已关闭场景。</param>
        /// <remarks>合并尚未执行的刷新；不保存或修改预制体内容。</remarks>
        private static void QueueOpen(PrefabStage stage)
        {
            EditorApplication.delayCall -= RefreshCurrent;
            EditorApplication.delayCall += RefreshCurrent;
        }

        /// <summary>域重载后为仍处于打开状态的技能预制体恢复临时预览。</summary>
        /// <remarks>仅在 Prefab Stage 中创建 DontSave 对象，不保存当前编辑内容。</remarks>
        public static void RefreshCurrent()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) Open(stage);
            else Clear();
        }

        /// <summary>Unity 打开技能预制体时解析 TbSkill.visualSetId 首片段，在预览场景中创建独立临时根。</summary>
        /// <param name="stage">Unity 当前预制体隔离编辑场景。</param>
        /// <remarks>对象与网格、纹理、材质均不保存到磁盘；作为独立根避免写入预制体层级，关闭或重载时释放。</remarks>
        private static void Open(PrefabStage stage)
        {
            observedStage = stage;
            creationFailed = true;
            Clear();
            var shape = stage.prefabContentsRoot.GetComponentInChildren<SkillCollisionAuthoring>(true);
            if (shape == null) return;
            try
            {
                var tables = CombatCylinderPipeline.ReadTables(); var skill = tables.TbSkill.Get(shape.SkillId);
                var scenarios = tables.TbPerformanceScenario.DataList.Where(s => s.Kind == EPerformanceKind.Combat).ToArray();
                if (scenarios.Length == 0 || scenarios.Select(s => s.CombatRulesId).Distinct().Count() != 1 ||
                    scenarios.Select(s => s.PresentationId_Ref.ShaderName).Distinct().Count() != 1)
                    throw new InvalidOperationException("技能预览需要唯一坐标比例与 Shader。");
                var scenario = scenarios[0]; var clip = skill.VisualSetId_Ref.ClipIds_Ref.First();
                var data = CocosAniData.Parse(File.ReadAllBytes("Assets/StreamingAssets/" + clip.AniResourceId_Ref.Path));
                texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave,
                    filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), scenario.PresentationId_Ref.TextureFilterMode) };
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes("Assets/StreamingAssets/" + clip.TextureResourceId_Ref.Path)))
                    throw new InvalidOperationException("技能原始纹理解码失败。");
                mesh = AniMeshFactory.CreateFrame(AniFrameLayout.Build(data, 0, 0, texture.width, texture.height),
                    scenario.CombatRulesId_Ref.WorldUnitsPerPixel * skill.VisualSetId_Ref.ScalePermille / 1000f);
                mesh.hideFlags = HideFlags.HideAndDontSave;
                var shader = Shader.Find(scenario.PresentationId_Ref.ShaderName);
                if (shader == null) throw new InvalidOperationException("技能预览 Shader 不存在。");
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = (int)RenderQueue.Transparent };
                material.SetTexture("_BaseMap", texture); material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Surface", 1); material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); material.SetFloat("_ZWrite", 0);
                material.SetFloat("_Cull", (float)CullMode.Off); material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                preview = new GameObject("SkillPreview_Temporary") { hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild };
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(preview, stage.scene);
                // ANI 网格位于 XY；编辑碰撞采用 XZ，将图像上方映射为技能局部前向。
                preview.transform.rotation = Quaternion.Euler(90, 0, 0);
                preview.AddComponent<MeshFilter>().sharedMesh = mesh; preview.AddComponent<MeshRenderer>().sharedMaterial = material;
                FrameCurrent();
                creationFailed = false;
                SceneView.RepaintAll();
            }
            catch (Exception exception) { Clear(); Debug.LogException(exception); }
        }

        /// <summary>预览创建后或用户点击俯视按钮时，将 Scene 视图对准 XZ 平面的图像与碰撞圆。</summary>
        /// <remarks>只调整编辑器相机；不改变节点坐标、半径或运行时表现。镜头范围由现有网格与节点计算。</remarks>
        public static void FrameCurrent()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var view = SceneView.lastActiveSceneView;
            if (stage == null || preview == null || view == null) return;
            var shape = stage.prefabContentsRoot.GetComponentInChildren<SkillCollisionAuthoring>(true);
            if (shape == null) return;
            var bounds = preview.GetComponent<MeshRenderer>().bounds;
            bounds.Encapsulate(new Bounds(shape.transform.position, new Vector3(shape.Radius * 2, 0, shape.Radius * 2)));
            view.in2DMode = false;
            view.orthographic = true;
            view.LookAtDirect(bounds.center, Quaternion.Euler(90, 0, 0), bounds.extents.magnitude);
        }

        /// <summary>Unity 关闭技能预制体编辑场景时释放全部临时显示资源。</summary>
        /// <param name="stage">即将关闭的预制体场景。</param>
        private static void Close(PrefabStage stage)
        {
            EditorApplication.delayCall -= RefreshCurrent;
            Clear();
        }

        /// <summary>关闭预览或程序集重载前销毁临时对象及其独占材质/网格/纹理，可重复调用。</summary>
        /// <remarks>仅销毁本类拥有的内存对象，不触碰源图集或任何持久化资产。</remarks>
        public static void Clear()
        {
            if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            preview = null; mesh = null; material = null; texture = null;
        }
    }
}
