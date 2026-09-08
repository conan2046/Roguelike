using System;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>战斗单位血条 Prefab 的运行时视图，负责把标准化生命值转换为左对齐填充宽度。</summary>
    public sealed class CombatHealthBarView : MonoBehaviour
    {
        [SerializeField] private int presentationId;
        [SerializeField] private float widthPixels;
        [SerializeField] private float heightPixels;
        [SerializeField, Range(0f, 1f)] private float previewNormalizedHealth = 1f;
        [SerializeField, HideInInspector] private RectTransform canvasRoot;
        [SerializeField] private RectTransform fill;

        /// <summary>返回该编辑 Prefab 对应的 TbCombatPresentation.id。</summary>
        public int PresentationId => presentationId;

        /// <summary>返回策划直接编辑并导出的血条宽度，单位为逻辑像素。</summary>
        public float WidthPixels => widthPixels;

        /// <summary>返回策划直接编辑并导出的血条高度，单位为逻辑像素。</summary>
        public float HeightPixels => heightPixels;

        /// <summary>返回仅用于编辑器观察的血量比例。</summary>
        public float PreviewNormalizedHealth => previewNormalizedHealth;

        /// <summary>返回负责把像素换算为场景显示尺寸的内部画布。</summary>
        public RectTransform CanvasRoot => canvasRoot;

        /// <summary>返回红色填充节点，供运行时绑定器和验收测试读取。</summary>
        public RectTransform Fill => fill;

        /// <summary>构建正式调参 Prefab 时绑定表现 ID、像素尺寸和内部 UI 节点。</summary>
        /// <param name="configuredPresentationId">对应的 TbCombatPresentation.id。</param>
        /// <param name="configuredWidthPixels">TbCombatPresentation.healthBarWidthPixels 直接像素宽度。</param>
        /// <param name="configuredHeightPixels">TbCombatPresentation.healthBarHeightPixels 直接像素高度。</param>
        /// <param name="configuredCanvasRoot">只负责显示换算的 World Space Canvas。</param>
        /// <param name="configuredFill">左对齐的当前血量填充节点。</param>
        /// <remarks>只由编辑器构建器调用；运行时 DOTS 不实例化该 Prefab。</remarks>
        public void Configure(int configuredPresentationId, float configuredWidthPixels,
            float configuredHeightPixels, RectTransform configuredCanvasRoot, RectTransform configuredFill)
        {
            presentationId = configuredPresentationId;
            widthPixels = configuredWidthPixels;
            heightPixels = configuredHeightPixels;
            previewNormalizedHealth = 1f;
            canvasRoot = configuredCanvasRoot;
            fill = configuredFill;
        }

        /// <summary>把当前像素字段同步到内部画布，并按预览生命比例刷新填充。</summary>
        /// <param name="sceneUnitsPerPixel">TbCombatRules.worldUnitsPerPixel 直接比例值，仅用于编辑器把像素画到场景。</param>
        /// <exception cref="InvalidOperationException">像素尺寸、换算比例或内部节点绑定无效时抛出。</exception>
        /// <remarks>根节点始终保持 Scale=1；场景显示换算封装在“显示画布（自动像素换算勿改）”子节点，不写入玩法配置。</remarks>
        public void ApplyPreview(float sceneUnitsPerPixel)
        {
            if (!(widthPixels > 0f) || !float.IsFinite(widthPixels) ||
                !(heightPixels > 0f) || !float.IsFinite(heightPixels))
                throw new InvalidOperationException("血条宽度和高度必须是大于零的像素值。");
            if (!(sceneUnitsPerPixel > 0f) || !float.IsFinite(sceneUnitsPerPixel))
                throw new InvalidOperationException("战斗规则中的像素显示比例必须大于零。");
            if (canvasRoot == null || fill == null)
                throw new InvalidOperationException("CombatHealthBar 缺少内部显示画布或当前血量节点。");

            transform.localScale = Vector3.one;
            if (transform is RectTransform rootRect) rootRect.sizeDelta = new Vector2(widthPixels, heightPixels);
            canvasRoot.localPosition = Vector3.zero;
            canvasRoot.localRotation = Quaternion.identity;
            canvasRoot.localScale = Vector3.one * sceneUnitsPerPixel;
            canvasRoot.sizeDelta = new Vector2(widthPixels, heightPixels);
            SetNormalized(previewNormalizedHealth);
        }

        /// <summary>按当前生命值比例刷新红色填充，0 为空、1 为满。</summary>
        /// <param name="normalizedHealth">已经换算为 0 到 1 的生命值比例。</param>
        /// <exception cref="InvalidOperationException">Prefab 未绑定 Fill 节点时抛出。</exception>
        /// <remarks>只修改本实例 RectTransform，不写配置、不创建资源，也不影响其他单位实例。</remarks>
        public void SetNormalized(float normalizedHealth)
        {
            if (fill == null) throw new InvalidOperationException("Combat health bar prefab is missing its Fill binding.");
            Vector2 maximum = fill.anchorMax;
            maximum.x = Mathf.Clamp01(normalizedHealth);
            fill.anchorMax = maximum;
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
        }
    }
}
