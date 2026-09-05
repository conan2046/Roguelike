using System;
using System.IO;
using Roguelike.Features.Combat.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Ui.Editor
{
    /// <summary>首次创建正式战斗 UI Prefab 与动态中文字体；已有资产保持不变，避免覆盖人工微调。</summary>
    public static class CombatUiPrefabBuilder
    {
        private const string Folder = "Assets/GameContent/UI/Combat";
        private const string FontSourcePath = "Assets/Art/Kapai/Fonts/MicrosoftArial.ttf";
        private const string FontAssetPath = Folder + "/MicrosoftArial.ttf";

        /// <summary>Unity 批处理或菜单调用时只生成当前缺失的正式资产。</summary>
        /// <remarks>生成内容只有任务要求的字体和五个 Prefab；不创建预览纹理、材质副本或场景。</remarks>
        [MenuItem("Roguelike/Combat/生成缺失正式战斗UI")]
        public static void BuildMissing()
        {
            EnsureFolder(Folder);
            Font font = LoadOrCreateFont();
            CreateMissingPrefab(Folder + "/CombatHud.prefab", () => BuildHud(font));
            CreateMissingPrefab(Folder + "/BossHealthBar.prefab", () => BuildBoss(font));
            CreateMissingPrefab(Folder + "/UpgradeChoicePanel.prefab", () => BuildUpgradePanel(font));
            CreateMissingPrefab(Folder + "/UpgradeCard.prefab", () => BuildUpgradeCard(font));
            CreateMissingPrefab(Folder + "/SettlementPanel.prefab", () => BuildSettlement(font));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Roguelike] Missing formal combat UI assets created without overwriting existing Prefabs.");
        }

        /// <summary>加载已存在的正式字体，缺失时复制项目现有 MicrosoftArial 到 GameContent。</summary>
        /// <returns>可直接写入五个 Prefab 的正式字体。</returns>
        /// <exception cref="InvalidOperationException">源字体缺失、复制失败或导入结果无效。</exception>
        private static Font LoadOrCreateFont()
        {
            Font existing = AssetDatabase.LoadAssetAtPath<Font>(FontAssetPath);
            if (existing != null) return existing;
            Font source = AssetDatabase.LoadAssetAtPath<Font>(FontSourcePath);
            if (source == null) throw new InvalidOperationException("Configured source font is missing: " + FontSourcePath);
            if (!AssetDatabase.CopyAsset(FontSourcePath, FontAssetPath))
                throw new InvalidOperationException("Failed to copy the formal combat font into GameContent.");
            AssetDatabase.ImportAsset(FontAssetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Font>(FontAssetPath) ??
                   throw new InvalidOperationException("Copied combat font did not import as UnityEngine.Font.");
        }

        /// <summary>保存一个缺失 Prefab；已存在路径完全跳过以保留用户布局调整。</summary>
        /// <param name="path">正式 Prefab 项目路径。</param>
        /// <param name="factory">创建临时根对象的函数。</param>
        /// <remarks>临时根只存在内存，保存后立即销毁。</remarks>
        private static void CreateMissingPrefab(string path, Func<GameObject> factory)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
            GameObject root = factory();
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>创建全屏 HUD Canvas、顶部信息和底部经验条的正式结构。</summary>
        /// <param name="font">正式 uGUI 字体。</param>
        /// <returns>等待保存的内存根对象。</returns>
        private static GameObject BuildHud(Font font)
        {
            GameObject root = RectObject("CombatHud", null);
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.sortingOrder = 100;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
            CombatHudView view = root.AddComponent<CombatHudView>();

            RectTransform top = CreatePanel(root.transform, "TopBar", new Color32(8, 15, 30, 210));
            SetRect(top, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 104), new Vector2(0, -52));
            Text health = CreateText(top, "HealthText", font, 30, TextAnchor.MiddleLeft);
            SetRect(health.rectTransform, new Vector2(0, 0), new Vector2(0, 1), new Vector2(360, 0), new Vector2(200, 0));
            Text timer = CreateText(top, "TimerText", font, 42, TextAnchor.MiddleCenter);
            SetRect(timer.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(280, 0), Vector2.zero);
            Text level = CreateText(top, "LevelText", font, 28, TextAnchor.MiddleRight);
            SetRect(level.rectTransform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(220, 0), new Vector2(-370, 0));
            Text kills = CreateText(top, "KillsText", font, 28, TextAnchor.MiddleRight);
            SetRect(kills.rectTransform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(220, 0), new Vector2(-140, 0));
            Text wave = CreateText(top, "WaveText", font, 28, TextAnchor.MiddleRight);
            SetRect(wave.rectTransform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(200, 0), new Vector2(-590, 0));

            RectTransform experienceRoot = CreatePanel(root.transform, "Experience", new Color32(8, 15, 30, 210));
            SetRect(experienceRoot, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(920, 54), new Vector2(0, 48));
            Slider experience = CreateSlider(experienceRoot, "ExperienceBar", new Color32(54, 77, 105, 255), new Color32(73, 214, 173, 255));
            SetRect((RectTransform)experience.transform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(-250, -18), new Vector2(90, 0));
            Text experienceText = CreateText(experienceRoot, "ExperienceText", font, 24, TextAnchor.MiddleCenter);
            SetRect(experienceText.rectTransform, new Vector2(0, 0), new Vector2(0, 1), new Vector2(240, 0), new Vector2(125, 0));
            Bind(view, "healthText", health, "timerText", timer, "levelText", level,
                "experienceText", experienceText, "killsText", kills, "waveText", wave,
                "experienceBar", experience);
            return root;
        }

        /// <summary>创建顶部居中的 Boss 名称、血量文本与进度条。</summary>
        /// <param name="font">正式 uGUI 字体。</param>
        /// <returns>等待保存的内存根对象。</returns>
        private static GameObject BuildBoss(Font font)
        {
            GameObject root = RectObject("BossHealthBar", null);
            RectTransform rect = (RectTransform)root.transform;
            SetRect(rect, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(760, 92), new Vector2(0, -150));
            root.AddComponent<Image>().color = new Color32(20, 8, 10, 225);
            BossHealthBarView view = root.AddComponent<BossHealthBarView>();
            Text name = CreateText(rect, "NameText", font, 30, TextAnchor.MiddleLeft);
            SetRect(name.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 1), new Vector2(300, 40), new Vector2(170, -8));
            Text health = CreateText(rect, "HealthText", font, 24, TextAnchor.MiddleRight);
            SetRect(health.rectTransform, new Vector2(1, 0.5f), new Vector2(1, 1), new Vector2(260, 40), new Vector2(-150, -8));
            Slider bar = CreateSlider(rect, "HealthBar", new Color32(62, 24, 30, 255), new Color32(224, 70, 84, 255));
            SetRect((RectTransform)bar.transform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(-40, 24), new Vector2(0, 18));
            Bind(view, "nameText", name, "healthText", health, "healthBar", bar);
            return root;
        }

        /// <summary>创建覆盖全屏的升级遮罩、标题与三卡水平容器。</summary>
        /// <param name="font">正式 uGUI 字体。</param>
        /// <returns>等待保存的内存根对象。</returns>
        private static GameObject BuildUpgradePanel(Font font)
        {
            GameObject root = RectObject("UpgradeChoicePanel", null);
            Stretch((RectTransform)root.transform);
            root.AddComponent<Image>().color = new Color32(5, 9, 18, 225);
            UpgradeChoicePanelView view = root.AddComponent<UpgradeChoicePanelView>();
            Text title = CreateText(root.transform, "TitleText", font, 56, TextAnchor.MiddleCenter);
            SetRect(title.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(900, 100), new Vector2(0, -115));
            GameObject cards = RectObject("Cards", root.transform);
            SetRect((RectTransform)cards.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1040, 460), new Vector2(0, -30));
            HorizontalLayoutGroup layout = cards.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 40;
            layout.padding = new RectOffset(20, 20, 20, 20);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            Bind(view, "titleText", title, "cardContainer", cards.transform);
            return root;
        }

        /// <summary>创建带空图标槽、名称、说明和等级的可点击升级卡。</summary>
        /// <param name="font">正式 uGUI 字体。</param>
        /// <returns>等待保存的内存根对象。</returns>
        private static GameObject BuildUpgradeCard(Font font)
        {
            GameObject root = RectObject("UpgradeCard", null);
            ((RectTransform)root.transform).sizeDelta = new Vector2(300, 420);
            Image background = root.AddComponent<Image>();
            background.color = new Color32(31, 46, 68, 255);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = background;
            UpgradeCardView view = root.AddComponent<UpgradeCardView>();
            Image icon = RectObject("Icon", root.transform).AddComponent<Image>();
            icon.color = new Color32(69, 92, 120, 255);
            SetRect(icon.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(132, 132), new Vector2(0, -92));
            Text name = CreateText(root.transform, "NameText", font, 30, TextAnchor.MiddleCenter);
            SetRect(name.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(-30, 58), new Vector2(0, -185));
            Text description = CreateText(root.transform, "DescriptionText", font, 23, TextAnchor.UpperCenter);
            SetRect(description.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(-44, -250), new Vector2(0, 10));
            Text rank = CreateText(root.transform, "RankText", font, 21, TextAnchor.MiddleCenter);
            SetRect(rank.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(-30, 44), new Vector2(0, 34));
            Bind(view, "selectButton", button, "iconImage", icon, "nameText", name,
                "descriptionText", description, "rankText", rank);
            return root;
        }

        /// <summary>创建胜负标题、单局统计与重开按钮的全屏结算遮罩。</summary>
        /// <param name="font">正式 uGUI 字体。</param>
        /// <returns>等待保存的内存根对象。</returns>
        private static GameObject BuildSettlement(Font font)
        {
            GameObject root = RectObject("SettlementPanel", null);
            Stretch((RectTransform)root.transform);
            root.AddComponent<Image>().color = new Color32(4, 8, 16, 230);
            SettlementPanelView view = root.AddComponent<SettlementPanelView>();
            Text result = CreateText(root.transform, "ResultText", font, 76, TextAnchor.MiddleCenter);
            SetRect(result.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(800, 130), new Vector2(0, 190));
            Text statistics = CreateText(root.transform, "StatisticsText", font, 32, TextAnchor.MiddleCenter);
            SetRect(statistics.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1100, 110), new Vector2(0, 55));
            Button button = CreateButton(root.transform, "RestartButton", new Vector2(360, 88));
            SetRect((RectTransform)button.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(360, 88), new Vector2(0, -115));
            Text restart = CreateText(button.transform, "RestartText", font, 34, TextAnchor.MiddleCenter);
            Stretch(restart.rectTransform);
            Bind(view, "resultText", result, "statisticsText", statistics,
                "restartText", restart, "restartButton", button);
            return root;
        }

        /// <summary>创建标准 RectTransform 节点并保持局部缩放为一。</summary>
        /// <param name="name">层级名称。</param>
        /// <param name="parent">可空父节点。</param>
        /// <returns>新节点。</returns>
        private static GameObject RectObject(string name, Transform parent)
        {
            var result = new GameObject(name, typeof(RectTransform));
            if (parent != null) result.transform.SetParent(parent, false);
            return result;
        }

        /// <summary>创建带背景颜色的矩形面板。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">层级名称。</param>
        /// <param name="color">Prefab 中保存的初始颜色。</param>
        /// <returns>面板 RectTransform。</returns>
        private static RectTransform CreatePanel(Transform parent, string name, Color color)
        {
            GameObject panel = RectObject(name, parent);
            panel.AddComponent<Image>().color = color;
            return (RectTransform)panel.transform;
        }

        /// <summary>创建 uGUI 文本并写入正式字体，文本内容由运行时 Luban 配置填充。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">层级名称。</param>
        /// <param name="font">正式字体。</param>
        /// <param name="size">Prefab 初始字号。</param>
        /// <param name="alignment">文本对齐。</param>
        /// <returns>空内容文本组件。</returns>
        private static Text CreateText(Transform parent, string name, Font font, int size,
            TextAnchor alignment)
        {
            Text text = RectObject(name, parent).AddComponent<Text>();
            text.text = string.Empty;
            text.font = font;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = alignment;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>创建无外部贴图依赖的标准 Slider 层级。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">层级名称。</param>
        /// <param name="backgroundColor">背景初始颜色。</param>
        /// <param name="fillColor">填充初始颜色。</param>
        /// <returns>已绑定 fillRect 的 Slider。</returns>
        private static Slider CreateSlider(Transform parent, string name, Color backgroundColor, Color fillColor)
        {
            GameObject root = RectObject(name, parent);
            Slider slider = root.AddComponent<Slider>();
            slider.minValue = 0;
            slider.maxValue = 1;
            slider.value = 1;
            slider.interactable = false;
            Image background = RectObject("Background", root.transform).AddComponent<Image>();
            background.color = backgroundColor;
            Stretch(background.rectTransform);
            GameObject fillArea = RectObject("Fill Area", root.transform);
            Stretch((RectTransform)fillArea.transform);
            Image fill = RectObject("Fill", fillArea.transform).AddComponent<Image>();
            fill.color = fillColor;
            Stretch(fill.rectTransform);
            slider.fillRect = fill.rectTransform;
            slider.targetGraphic = background;
            return slider;
        }

        /// <summary>创建无贴图依赖的可点击按钮。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">层级名称。</param>
        /// <param name="size">Prefab 初始尺寸。</param>
        /// <returns>以纯色 Image 为目标的 Button。</returns>
        private static Button CreateButton(Transform parent, string name, Vector2 size)
        {
            GameObject root = RectObject(name, parent);
            ((RectTransform)root.transform).sizeDelta = size;
            Image image = root.AddComponent<Image>();
            image.color = new Color32(52, 143, 235, 255);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = image;
            return button;
        }

        /// <summary>设置 RectTransform 锚点、尺寸和锚定位置。</summary>
        /// <param name="rect">待调整节点。</param>
        /// <param name="anchorMin">最小锚点。</param>
        /// <param name="anchorMax">最大锚点。</param>
        /// <param name="size">sizeDelta。</param>
        /// <param name="position">anchoredPosition。</param>
        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 size, Vector2 position)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        /// <summary>让 RectTransform 四边贴合父节点。</summary>
        /// <param name="rect">待拉伸节点。</param>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>使用 SerializedObject 把 Prefab 私有序列化字段绑定到新建组件。</summary>
        /// <param name="target">View 组件。</param>
        /// <param name="pairs">字段名与 Unity 对象交替排列。</param>
        /// <exception cref="InvalidOperationException">字段名不存在或参数数量非法。</exception>
        private static void Bind(UnityEngine.Object target, params object[] pairs)
        {
            if (pairs.Length % 2 != 0) throw new InvalidOperationException("Serialized binding pairs are incomplete.");
            var serialized = new SerializedObject(target);
            for (int index = 0; index < pairs.Length; index += 2)
            {
                string propertyName = (string)pairs[index];
                SerializedProperty property = serialized.FindProperty(propertyName);
                if (property == null) throw new InvalidOperationException("Missing serialized field: " + propertyName);
                property.objectReferenceValue = pairs[index + 1] as UnityEngine.Object;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>逐级创建 Unity 资产目录。</summary>
        /// <param name="path">Assets 开头的目标目录。</param>
        private static void EnsureFolder(string path)
        {
            string current = "Assets";
            foreach (string segment in path.Substring("Assets/".Length).Split('/'))
            {
                string next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segment);
                current = next;
            }
            Directory.CreateDirectory(path);
        }
    }
}
