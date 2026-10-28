using System;
using System.IO;
using cfg;
using ProjectX.Migration;
using Roguelike.Animation;
using Roguelike.Animation.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>单张审核卡片的内存播放器；窗口与场景共用解析帧，不序列化任何外观对象。</summary>
    public sealed class SkillEffectReviewPlayback : IDisposable
    {
        private Texture2D texture;
        private Material material;
        private Mesh[] meshes;
        private AniQuad[][] frames;
        private double[] ends;
        private GameObject root;
        private MeshFilter filter;
        private MeshRenderer renderer;
        private Bounds bounds;
        public int FrameIndex { get; private set; }
        public int FrameCount => frames?.Length ?? 0;
        public double Duration => ends == null ? 0 : ends[ends.Length - 1];
        public int ActionCount { get; private set; }
        public string Error { get; private set; }
        public Bounds GeometryBounds => bounds;
        public double GroundContactSeconds { get; private set; } = -1;
        public bool AppliedFlipX { get; private set; }
        private int sceneSlot;

        /// <summary>片段切换时读取 TbAnimationClip 指定 ANI/PNG；时间解释与过滤方式来自 TbCombatPresentation。</summary>
        /// <param name="clip">正式表中选定片段。</param><param name="presentation">正式表现方案。</param>
        /// <param name="actionIndex">ANI 动作索引；允许逐方向检查，避免只检查第一动作。</param>
        /// <param name="scene">审核场景；无效时只绘制窗口。</param><param name="slot">四格布局中的索引。</param>
        /// <param name="extraFlipX">用户审核记录中的额外水平翻转，与正式 TbAnimationClip.flipX 异或，不修改源图。</param>
        /// <remarks>本实例只持有一个片段的内存网格、材质与纹理；组合播放器统一管理各阶段。异常立即清理，Dispose 释放全部所有权。</remarks>
        public void Load(AnimationClipConfig clip, CombatPresentationConfig presentation, int actionIndex, Scene scene, int slot, bool extraFlipX = false)
        {
            Dispose(); Error = null;
            AppliedFlipX = clip.FlipX ^ extraFlipX;
            try
            {
                var data = CocosAniData.Parse(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, clip.AniResourceId_Ref.Path)));
                ActionCount = data.actions.Length;
                actionIndex = Mathf.Clamp(actionIndex, 0, ActionCount - 1);
                texture = new Texture2D(2, 2) { name = "SkillReview_MemoryTexture", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), presentation.TextureFilterMode), wrapMode = TextureWrapMode.Clamp };
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, clip.TextureResourceId_Ref.Path))))
                    throw new InvalidDataException("PNG 解码失败。");
                AniFrameLayout.ValidateAtlas(data, texture.width, texture.height);
                var action = data.actions[actionIndex];
                frames = new AniQuad[action.frames.Length][];
                meshes = new Mesh[frames.Length]; ends = new double[frames.Length];
                bool first = true;
                for (int index = 0; index < frames.Length; index++)
                {
                    frames[index] = AniFrameLayout.Build(data, actionIndex, index, texture.width, texture.height);
                    for (int part = 0; part < frames[index].Length; part++)
                    {
                        if (AppliedFlipX)
                        {
                            frames[index][part].CenterX = -frames[index][part].CenterX;
                            frames[index][part].U += frames[index][part].UWidth;
                            frames[index][part].UWidth = -frames[index][part].UWidth;
                        }
                        var quad = frames[index][part];
                        var partBounds = new Bounds(new Vector3(quad.CenterX, quad.CenterY), new Vector3(quad.Width, quad.Height));
                        if (first) { bounds = partBounds; first = false; } else bounds.Encapsulate(partBounds);
                    }
                    if (frames[index].Length > 0)
                    {
                        meshes[index] = AniMeshFactory.CreateFrame(frames[index], 1f);
                        meshes[index].hideFlags = HideFlags.HideAndDontSave;
                    }
                    int units = action.durations[index] == 1 ? presentation.DurationOneUnits : action.durations[index];
                    ends[index] = (index == 0 ? 0 : ends[index - 1]) + Math.Max(presentation.MinDurationUnits, units) / (double)presentation.SourceTickHz;
                }
                if (first || Duration <= 0) throw new InvalidDataException("片段没有可显示的模块或有效时长。");
                GroundContactSeconds = FindGroundContact();
                if (scene.IsValid() && scene.isLoaded) CreateSceneObject(scene, slot, presentation);
                Sample(0);
            }
            catch (Exception exception) { Dispose(); Error = exception.Message; }
        }

        /// <summary>为审核场景建立独立 DontSave 根，按完整动作包围盒等比适配四格，不改正式比例。</summary>
        /// <param name="scene">用户明确创建的审核场景。</param><param name="slot">当前四格索引。</param>
        /// <param name="presentation">TbCombatPresentation.shaderName。</param>
        /// <remarks>根与外观对象均不保存到 Scene，清场或域重载时销毁；仅查看资源动画，不模拟技能结算。</remarks>
        private void CreateSceneObject(Scene scene, int slot, CombatPresentationConfig presentation)
        {
            sceneSlot = slot;
            var shader = Shader.Find(presentation.ShaderName);
            if (shader == null) throw new InvalidDataException("正式表现 Shader 不存在：" + presentation.ShaderName);
            material = new Material(shader) { name = "SkillReview_MemoryMaterial", hideFlags = HideFlags.HideAndDontSave,
                renderQueue = (int)RenderQueue.Transparent };
            material.SetTexture("_BaseMap", texture); material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 1); material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); material.SetFloat("_ZWrite", 0);
            material.SetFloat("_Cull", (float)CullMode.Off); material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            root = new GameObject("SkillReview_Memory_" + slot) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(root, scene);
            filter = root.AddComponent<MeshFilter>(); renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            SetSceneLayout(bounds, Vector2.zero, true);
        }

        /// <summary>加载时检查原始 ANI 是否从地面上方向下接触锚点，用于审核工具衔接落点附加层。</summary>
        /// <returns>首次接触锚点的帧开始时刻；不属于此种下降轨迹时返回负值。</returns>
        /// <remarks>这里只判定资源原生布局，不作为正式命中规则，不修改原始坐标。</remarks>
        private double FindGroundContact()
        {
            if (frames[0].Length == 0) return -1;
            foreach (var quad in frames[0]) if (quad.CenterY - quad.Height * .5f <= 0) return -1;
            for (int frame = 1; frame < frames.Length; frame++)
                foreach (var quad in frames[frame])
                    if (quad.CenterY - quad.Height * .5f <= 0) return ends[frame - 1];
            return -1;
        }

        /// <summary>组合播放推进或显示阶段改变时，将各层对齐到同一取景、同一原始锚点。</summary>
        /// <param name="viewBounds">整条组合流程共用的像素取景范围。</param>
        /// <param name="pixelOffset">仅审核演示使用的位置偏移；飞行段从起点移向命中点。</param>
        /// <param name="visible">当前阶段是否仍在播放。</param>
        /// <remarks>仅更新临时根与 Renderer，不写入正式资源或场景。</remarks>
        public void SetSceneLayout(Bounds viewBounds, Vector2 pixelOffset, bool visible)
        {
            if (root == null) return;
            float scale = 3f / Math.Max(1f, Math.Max(viewBounds.size.x, viewBounds.size.y));
            root.transform.localScale = Vector3.one * scale;
            root.transform.position = new Vector3(sceneSlot % 2 == 0 ? -2.5f : 2.5f, sceneSlot < 2 ? 2f : -2f) +
                ((Vector3)pixelOffset - viewBounds.center) * scale;
            renderer.enabled = visible && meshes != null && meshes[FrameIndex] != null;
        }

        /// <summary>编辑器时钟推进时按正式时长采样动作；循环只为反复审核，不改变片段的正式 Loop 字段。</summary>
        /// <param name="seconds">当前播放时间；非负。</param>
        /// <remarks>仅更新内存 MeshFilter 引用，不产生逐帧网格。</remarks>
        public void Sample(double seconds)
        {
            SampleFrame(seconds, true);
        }

        /// <summary>组合中的起手、落下和命中段单次采样，结束后停在末帧，由组合调度决定何时隐藏。</summary>
        /// <param name="seconds">本段已播放的非负时间。</param>
        /// <remarks>不循环回第一帧，不创建任何新资源。</remarks>
        public void SampleOnce(double seconds) => SampleFrame(seconds, false);

        /// <summary>依据正式帧时长采样共享帧缓存，分别支持演示飞行循环与一次性命中。</summary>
        /// <param name="seconds">本段播放时间。</param><param name="loop">是否按本段时长循环。</param>
        /// <remarks>仅切换内存网格引用；帧几何保持原生坐标。</remarks>
        private void SampleFrame(double seconds, bool loop)
        {
            if (ends == null) return;
            double time = loop ? Math.Max(0, seconds) % Duration : Math.Max(0, seconds);
            int index = Array.BinarySearch(ends, time);
            FrameIndex = Mathf.Clamp(index < 0 ? ~index : index + 1, 0, ends.Length - 1);
            if (filter != null) { filter.sharedMesh = meshes[FrameIndex]; renderer.enabled = meshes[FrameIndex] != null; }
        }

        /// <summary>窗口卡片重绘时依照共享模块布局绘制整张动画，固定完整动作包围盒以避免逐帧缩放抖动。</summary>
        /// <param name="rect">卡片中的播放区域。</param><param name="zoom">用户审核缩放，不导出正式数据。</param>
        public void Draw(Rect rect, float zoom)
        {
            EditorGUI.DrawRect(rect, new Color(0.10f, 0.12f, 0.16f));
            if (Error != null) { GUI.Label(rect, "无法播放：" + Error, EditorStyles.wordWrappedLabel); return; }
            DrawLayer(rect, zoom, bounds, Vector2.zero);
        }

        /// <summary>组合窗口重绘时在统一取景中叠加本段，不清屏且保留原始锚点及相对大小。</summary>
        /// <param name="rect">组合播放区域。</param><param name="zoom">审核缩放。</param>
        /// <param name="viewBounds">组合共用包围盒。</param><param name="pixelOffset">本段演示位移。</param>
        public void DrawLayer(Rect rect, float zoom, Bounds viewBounds, Vector2 pixelOffset)
        {
            if (texture == null || frames == null) return;
            GUI.BeginClip(rect);
            float scale = Math.Min((rect.width - 20) / Math.Max(1f, viewBounds.size.x), (rect.height - 20) / Math.Max(1f, viewBounds.size.y)) * zoom;
            foreach (var quad in frames[FrameIndex])
            {
                var destination = new Rect(rect.width * .5f + (quad.CenterX + pixelOffset.x - viewBounds.center.x - quad.Width * .5f) * scale,
                    rect.height * .5f - (quad.CenterY + pixelOffset.y - viewBounds.center.y + quad.Height * .5f) * scale, quad.Width * scale, quad.Height * scale);
                GUI.DrawTextureWithTexCoords(destination, texture, new Rect(quad.U, quad.V, quad.UWidth, quad.VHeight), true);
            }
            GUI.EndClip();
        }

        /// <summary>翻页、切换片段、关闭工具或域重载时释放此卡片独占的所有 Unity 内存对象。</summary>
        /// <remarks>不删除或修改正式磁盘资产；允许重复调用。</remarks>
        public void Dispose()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (meshes != null) foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            if (material != null) Object.DestroyImmediate(material);
            if (texture != null) Object.DestroyImmediate(texture);
            root = null; filter = null; renderer = null; material = null; texture = null;
            meshes = null; frames = null; ends = null; FrameIndex = 0; ActionCount = 0; GroundContactSeconds = -1;
        }
    }
}
