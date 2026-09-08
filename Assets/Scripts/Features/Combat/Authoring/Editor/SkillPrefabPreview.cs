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
        private static SkillVisualPlacementAuthoring previewPlacement;
        private static Transform previewRoot;
        private static bool previewIsProjectile;
        private static bool creationFailed;

        /// <summary>Unity 编辑器域加载后注册预制体打开/关闭事件；重载前释放临时资源。</summary>
        /// <remarks>仅编辑器事件订阅；不更改正式场景或运行时资源。</remarks>
        static SkillPrefabPreview()
        {
            PrefabStage.prefabStageOpened += QueueOpen;
            PrefabStage.prefabStageClosing += Close;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
            Selection.selectionChanged += QueueSelectionRefresh;
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
            if (stage != null && preview != null) SynchronizePreviewTransform();
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

        /// <summary>用户切换技能表现节点时延迟刷新对应弹丸、命中或范围片段。</summary>
        /// <remarks>只在当前技能 Prefab Stage 内重建内存预览，不修改选择或持久化对象。</remarks>
        private static void QueueSelectionRefresh()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage()?.prefabContentsRoot?.GetComponentInChildren<SkillCollisionAuthoring>(true) == null)
                return;
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
                var scenario = scenarios[0];
                SkillVisualPlacementAuthoring placement = SelectedPlacement(stage, skill);
                AnimationClipConfig clip = SelectClip(skill, placement);
                var data = CocosAniData.Parse(File.ReadAllBytes("Assets/StreamingAssets/" + clip.AniResourceId_Ref.Path));
                texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave,
                    filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), scenario.PresentationId_Ref.TextureFilterMode) };
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes("Assets/StreamingAssets/" + clip.TextureResourceId_Ref.Path)))
                    throw new InvalidOperationException("技能原始纹理解码失败。");
                var quads = AniFrameLayout.Build(data, 0, 0, texture.width, texture.height);
                if (clip.FlipX)
                    for (int index = 0; index < quads.Length; index++)
                    {
                        quads[index].CenterX = -quads[index].CenterX;
                        quads[index].U += quads[index].UWidth;
                        quads[index].UWidth = -quads[index].UWidth;
                    }
                // 网格只烘焙像素比例；Prefab 根和表现节点缩放由临时对象实时继承，保存前即可看到最终大小。
                mesh = AniMeshFactory.CreateFrame(quads, scenario.CombatRulesId_Ref.WorldUnitsPerPixel);
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
                previewRoot = stage.prefabContentsRoot.transform;
                previewPlacement = placement;
                previewIsProjectile = placement?.Kind == SkillVisualPlacementAuthoring.VisualKind.Projectile ||
                    (placement == null && skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile);
                SynchronizePreviewTransform();
                preview.AddComponent<MeshFilter>().sharedMesh = mesh; preview.AddComponent<MeshRenderer>().sharedMaterial = material;
                FrameCurrent();
                creationFailed = false;
                SceneView.RepaintAll();
            }
            catch (Exception exception) { Clear(); Debug.LogException(exception); }
        }

        /// <summary>把临时 ANI 首帧实时对齐到 Prefab 根缩放和当前表现节点，使 Scene 视图与运行时共用同一大小、位置和朝向。</summary>
        /// <remarks>弹丸以 Prefab 上方向为前向，因此把运行时 ANI 的局部 X 前向旋转到编辑器 Y；命中与范围表现保持原始 XY。</remarks>
        private static void SynchronizePreviewTransform()
        {
            if (preview == null || previewRoot == null) return;
            Transform placementTransform = previewPlacement == null ? null : previewPlacement.transform;
            preview.transform.position = placementTransform == null ? previewRoot.position : placementTransform.position;
            preview.transform.rotation = (placementTransform == null ? previewRoot.rotation : placementTransform.rotation) *
                (previewIsProjectile ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity);
            float rootScale = SkillCollisionPipeline.UniformRootScale(previewRoot);
            float placementScale = placementTransform == null ? 1f : placementTransform.localScale.x;
            preview.transform.localScale = Vector3.one * rootScale * placementScale;
        }

        /// <summary>确定当前应预览的表现节点；优先使用用户选中的节点，否则按技能投递选择默认节点。</summary>
        /// <param name="stage">当前技能 Prefab Stage。</param>
        /// <param name="skill">TbSkill 当前行。</param>
        /// <returns>当前表现节点；旧 Prefab 尚未迁移时可为空。</returns>
        private static SkillVisualPlacementAuthoring SelectedPlacement(PrefabStage stage, SkillConfig skill)
        {
            SkillVisualPlacementAuthoring selected = Selection.activeGameObject == null
                ? null
                : Selection.activeGameObject.GetComponent<SkillVisualPlacementAuthoring>();
            if (selected != null && selected.transform.IsChildOf(stage.prefabContentsRoot.transform)) return selected;
            SkillVisualPlacementAuthoring.VisualKind kind = skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea
                ? SkillVisualPlacementAuthoring.VisualKind.Area
                : SkillVisualPlacementAuthoring.VisualKind.Projectile;
            return stage.prefabContentsRoot.GetComponentsInChildren<SkillVisualPlacementAuthoring>(true)
                .FirstOrDefault(item => item.Kind == kind);
        }

        /// <summary>按表现节点用途选择 TbSkill 指定的正式 ANI 片段。</summary>
        /// <param name="skill">TbSkill 当前行。</param>
        /// <param name="placement">当前表现节点；为空时沿用视觉集合首片段兼容旧 Prefab。</param>
        /// <returns>需要显示首帧的动画片段。</returns>
        /// <exception cref="InvalidOperationException">节点用途没有对应正式片段。</exception>
        private static AnimationClipConfig SelectClip(SkillConfig skill, SkillVisualPlacementAuthoring placement)
        {
            if (placement == null) return skill.VisualSetId_Ref.ClipIds_Ref.First();
            AnimationClipConfig clip = placement.Kind switch
            {
                SkillVisualPlacementAuthoring.VisualKind.Projectile => skill.ProjectileClipId_Ref,
                SkillVisualPlacementAuthoring.VisualKind.Impact => skill.ImpactClipId_Ref,
                SkillVisualPlacementAuthoring.VisualKind.Area => skill.AreaClipIds_Ref?.FirstOrDefault(),
                _ => null
            };
            return clip ?? throw new InvalidOperationException(skill.Id + " 的当前表现节点没有对应 ANI 片段。");
        }

        /// <summary>预览创建后或用户点击对准按钮时，将 Scene 视图对准 XY 平面的图像与碰撞圆。</summary>
        /// <remarks>只调整编辑器相机；不改变节点坐标、半径或运行时表现。镜头范围由现有网格与节点计算。</remarks>
        public static void FrameCurrent()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var view = SceneView.lastActiveSceneView;
            if (stage == null || preview == null || view == null) return;
            var shape = stage.prefabContentsRoot.GetComponentInChildren<SkillCollisionAuthoring>(true);
            if (shape == null) return;
            var bounds = preview.GetComponent<MeshRenderer>().bounds;
            CircleCollider2D circle = shape.GetComponent<CircleCollider2D>();
            float scale = SkillCollisionEditor.PixelScale();
            float rootScale = SkillCollisionPipeline.UniformRootScale(stage.prefabContentsRoot.transform);
            float x = circle == null ? shape.OffsetXPixels * scale : (shape.transform.localPosition.x + circle.offset.x) * rootScale;
            float y = circle == null ? shape.OffsetYPixels * scale : (shape.transform.localPosition.y + circle.offset.y) * rootScale;
            float radius = circle == null ? shape.RadiusPixels * scale : circle.radius * rootScale;
            Vector3 center = stage.prefabContentsRoot.transform.position + new Vector3(x, y, 0f);
            bounds.Encapsulate(new Bounds(center, new Vector3(radius * 2f, radius * 2f, 0f)));
            view.in2DMode = true;
            view.orthographic = true;
            view.LookAtDirect(bounds.center, Quaternion.identity, Mathf.Max(0.1f, bounds.extents.magnitude));
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
            previewPlacement = null; previewRoot = null; previewIsProjectile = false;
        }
    }
}
