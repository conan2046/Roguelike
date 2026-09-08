using System;
using System.Linq;
using cfg;
using Roguelike.Features.Combat.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>从正式战斗 UI Sprite 生成可编辑的血条进度条 Prefab。</summary>
    public static class CombatHealthBarPrefabBuilder
    {
        private const string FrameSpritePath = "Assets/GameContent/UI/Combat/battleBar.png";
        private const string FillSpritePath = "Assets/GameContent/UI/Combat/battleBar_hp.png";
        private const string PrefabDirectory = "Assets/GameContent/UI/Combat/Prefabs";
        private const string PrefabPath = PrefabDirectory + "/CombatHealthBar.prefab";
        private static bool suppressAutomaticOpen;

        /// <summary>脚本重载后仅升级仍把 World Space Canvas 放在根节点的旧版血条 Prefab。</summary>
        /// <remarks>通过延迟回调等待 AssetDatabase 就绪；新结构不重建，避免覆盖用户后续调好的像素字段。</remarks>
        [InitializeOnLoadMethod]
        private static void UpgradeExistingPrefabAfterReload()
        {
            EditorApplication.delayCall += () =>
            {
                GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                CombatHealthBarView view = existing == null ? null : existing.GetComponent<CombatHealthBarView>();
                if (view != null && view.CanvasRoot != null && existing.GetComponent<Canvas>() == null) return;
                suppressAutomaticOpen = true;
                try
                {
                    Build();
                }
                finally
                {
                    suppressAutomaticOpen = false;
                }
            };
        }

        /// <summary>菜单或命令行调用时重建正式血条 Prefab，并选中生成结果。</summary>
        /// <exception cref="InvalidOperationException">正式 Sprite 缺失或 Prefab 保存失败时抛出。</exception>
        /// <remarks>只覆盖 PrefabPath 指定的正式资产；不修改 Scene，不创建预览纹理、材质或缓存资产。</remarks>
        [MenuItem("Roguelike/资源/重建战斗血条 Prefab")]
        public static void Build()
        {
            Sprite frameSprite = AssetDatabase.LoadAssetAtPath<Sprite>(FrameSpritePath);
            Sprite fillSprite = AssetDatabase.LoadAssetAtPath<Sprite>(FillSpritePath);
            if (frameSprite == null || fillSprite == null)
                throw new InvalidOperationException("Combat health bar source sprites are missing or not imported as Sprite.");

            Tables tables = CombatCylinderPipeline.ReadTables();
            var bindings = tables.TbStage.DataList
                .Select(stage => new { stage.PresentationId, stage.CombatRulesId })
                .Distinct()
                .ToArray();
            if (bindings.Length != 1)
                throw new InvalidOperationException("CombatHealthBar 需要唯一的正式关卡表现方案与战斗规则。");
            CombatPresentationConfig presentation = tables.TbCombatPresentation.Get(bindings[0].PresentationId);
            float sceneUnitsPerPixel = tables.TbCombatRules.Get(bindings[0].CombatRulesId).WorldUnitsPerPixel;
            if (!(presentation.HealthBarWidthPixels > 0f) || !(presentation.HealthBarHeightPixels > 0f))
                throw new InvalidOperationException("TbCombatPresentation 血条像素宽高必须大于零。");

            if (!AssetDatabase.IsValidFolder(PrefabDirectory))
                AssetDatabase.CreateFolder("Assets/GameContent/UI/Combat", "Prefabs");

            var root = new GameObject("CombatHealthBar", typeof(RectTransform), typeof(CombatHealthBarView));
            try
            {
                var rootRect = (RectTransform)root.transform;
                rootRect.sizeDelta = new Vector2(presentation.HealthBarWidthPixels,
                    presentation.HealthBarHeightPixels);
                rootRect.localScale = Vector3.one;

                var canvasObject = new GameObject("显示画布（自动像素换算勿改）", typeof(RectTransform),
                    typeof(Canvas), typeof(CanvasScaler));
                var canvasRect = (RectTransform)canvasObject.transform;
                canvasRect.SetParent(rootRect, false);
                canvasRect.sizeDelta = rootRect.sizeDelta;
                canvasRect.localScale = Vector3.one * sceneUnitsPerPixel;

                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 100;

                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;
                scaler.referencePixelsPerUnit = frameSprite.pixelsPerUnit;
                scaler.dynamicPixelsPerUnit = frameSprite.pixelsPerUnit;

                RectTransform background = CreateImage("血条底框", canvasRect, frameSprite);
                background.anchorMin = Vector2.zero;
                background.anchorMax = Vector2.one;
                background.offsetMin = Vector2.zero;
                background.offsetMax = Vector2.zero;

                RectTransform fill = CreateImage("当前血量", canvasRect, fillSprite);
                fill.anchorMin = Vector2.zero;
                fill.anchorMax = Vector2.one;
                fill.pivot = new Vector2(0f, 0.5f);
                fill.offsetMin = Vector2.zero;
                fill.offsetMax = Vector2.zero;

                CombatHealthBarView view = root.GetComponent<CombatHealthBarView>();
                view.Configure(presentation.Id, presentation.HealthBarWidthPixels,
                    presentation.HealthBarHeightPixels, canvasRect, fill);
                view.ApplyPreview(sceneUnitsPerPixel);

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                if (prefab == null) throw new InvalidOperationException("Unity failed to save " + PrefabPath);
                AssetDatabase.SaveAssets();
                Selection.activeObject = prefab;
                if (!Application.isBatchMode && !suppressAutomaticOpen) AssetDatabase.OpenAsset(prefab);
                Debug.Log("Created editable combat health bar prefab: " + PrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>创建一个不拦截射线、保持原图颜色的 UI Image 子节点。</summary>
        /// <param name="name">Hierarchy 节点名。</param>
        /// <param name="parent">血条根 RectTransform。</param>
        /// <param name="sprite">正式战斗 UI Sprite。</param>
        /// <returns>新建 Image 的 RectTransform。</returns>
        /// <remarks>对象只存在于临时 Prefab 构建树中，最终由 Build 保存并整体销毁临时根。</remarks>
        private static RectTransform CreateImage(string name, RectTransform parent, Sprite sprite)
        {
            var child = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)child.transform;
            rect.SetParent(parent, false);
            var image = child.GetComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            image.raycastTarget = false;
            image.preserveAspect = false;
            return rect;
        }
    }
}
