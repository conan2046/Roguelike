using System;
using cfg;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>战斗运行器独占的实际行走边界显示；正式与测试战斗共用，不依赖职业或编辑器 Gizmos。</summary>
    public sealed class CombatArenaBoundaryVisuals : IDisposable
    {
        private readonly LineRenderer line;
        private readonly Material material;
        private readonly float widthPixels;
        private bool disposed;
        public GameObject Root { get; }
        public bool Visible => !disposed && Root != null && Root.activeSelf;

        /// <summary>相机创建后从 ECS 会话实际半边界建立矩形，样式只消费 TbCombatPresentation.arenaBoundary 字段。</summary>
        /// <param name="halfSize">与 CombatTickJob.Arena 同源的世界坐标移动半边界。</param>
        /// <param name="config">正式默认开关、颜色、显示线宽、Shader 与排序配置。</param>
        /// <param name="parent">本局运行器；几何仍使用移动系统的世界坐标，不随容器偏移。</param>
        /// <remarks>只创建正式运行所需的内存 GameObject／独占材质；不保存资源；调用方必须 Dispose。</remarks>
        /// <exception cref="InvalidOperationException">边界或正式显示配置非法，或 Shader 不存在。</exception>
        public CombatArenaBoundaryVisuals(float2 halfSize, CombatPresentationConfig config, Transform parent)
        {
            if (config == null || !math.all(math.isfinite(halfSize)) || math.any(halfSize <= 0) ||
                !ColorUtility.TryParseHtmlString(config.ArenaBoundaryColor, out var color) ||
                float.IsNaN(config.ArenaBoundaryWidthPixels) || float.IsInfinity(config.ArenaBoundaryWidthPixels) || config.ArenaBoundaryWidthPixels <= 0 ||
                config.ArenaBoundarySortingOrder < short.MinValue || config.ArenaBoundarySortingOrder > short.MaxValue ||
                config.ArenaBoundaryRenderQueue < 0 || config.ArenaBoundaryRenderQueue > 5000 || string.IsNullOrWhiteSpace(config.ArenaBoundaryShaderName))
                throw new InvalidOperationException("TbCombatPresentation: invalid arena boundary configuration.");
            var shader = Shader.Find(config.ArenaBoundaryShaderName);
            if (shader == null) throw new InvalidOperationException("TbCombatPresentation: arena boundary shader is unavailable.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = config.ArenaBoundaryRenderQueue };
            Root = new GameObject("Walkable Arena Boundary") { hideFlags = HideFlags.DontSave };
            Root.transform.SetParent(parent, false);
            line = Root.AddComponent<LineRenderer>(); line.sharedMaterial = material;
            line.useWorldSpace = true; line.loop = true; line.positionCount = 4; line.alignment = LineAlignment.View;
            line.sortingOrder = config.ArenaBoundarySortingOrder; line.startColor = line.endColor = color;
            line.SetPositions(new[] { new Vector3(-halfSize.x, -halfSize.y, 0), new Vector3(halfSize.x, -halfSize.y, 0),
                new Vector3(halfSize.x, halfSize.y, 0), new Vector3(-halfSize.x, halfSize.y, 0) });
            widthPixels = config.ArenaBoundaryWidthPixels; Root.SetActive(config.ArenaBoundaryDefaultVisible);
        }

        /// <summary>每个表现帧按当前相机高度换算配置中的显示像素线宽；暂停仍可同步，隐藏时不做更新。</summary>
        /// <param name="camera">本局正交相机；视口未就绪时暂不绘制线宽。</param>
        public void Synchronize(Camera camera)
        {
            if (!Visible || camera == null || camera.pixelHeight <= 0) return;
            float width = camera.orthographicSize * 2f * widthPixels / camera.pixelHeight;
            if (line.startWidth != width) line.startWidth = line.endWidth = width;
        }

        /// <summary>运行时设置入口只切换线条根节点，不修改传入移动边界或任何规则配置。</summary>
        /// <param name="visible">期望可见状态。</param>
        /// <remarks>无磁盘副作用；同实例重开继续沿用，Dispose 后调用为空操作。</remarks>
        public void SetVisible(bool visible) { if (!disposed && Root != null) Root.SetActive(visible); }

        /// <summary>退出、切图或部分初始化失败时立即隐藏并销毁自有线条与材质，重复释放安全。</summary>
        /// <remarks>运行时帧末回收，编辑测试立即回收；不影响资源服务或其他运行器。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (Root != null) Root.SetActive(false);
            DestroyOwned(Root); DestroyOwned(material);
        }

        /// <summary>按 Unity 当前执行模式回收本可视对象独占的内存对象。</summary>
        /// <param name="value">本实例创建的对象，允许为空。</param>
        /// <remarks>不删除资产，也不写 Scene／Prefab。</remarks>
        private static void DestroyOwned(UnityEngine.Object value)
        { if (value == null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
    }
}
