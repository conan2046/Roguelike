using System;
using System.IO;
using System.Linq;
using cfg;
using Luban;
using Roguelike.Features.Combat.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Ui.Editor
{
    public static partial class CombatUiPrefabBuilder
    {
        /// <summary>按正式入口表只创建本次需要且尚不存在的选角/HUD Prefab；绝不覆盖既有手调资产。</summary>
        /// <remarks>全部布局在编辑期序列化。仅保存两个正式Prefab，不生成预览资源、不修改Scene；业务槽数来自TbTribulationUi/Slice。</remarks>
        /// <exception cref="InvalidOperationException">入口唯一性、候选数量或正式字体不合法。</exception>
        [MenuItem("Roguelike/Combat/生成缺失借劫切片UI")]
        public static void BuildMissingTribulation()
        {
            var tables = new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config/Luban", name + ".bytes"))));
            var entry = tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry);
            var slices = entry.StageIds_Ref.Select(s => s.TribulationSliceId_Ref).ToArray();
            if (slices.Length == 0 || slices.Any(s => s == null) || slices.Select(s => s.RefinementIds.Count).Distinct().Count() != 1)
                throw new InvalidOperationException("Entry requires roles with a consistent refinement choice count.");
            var font = LoadOrCreateFont();
            CreateMissingPrefab("Assets/" + entry.LobbyPrefabResourceId_Ref.Path, () => BuildTribulationLobby(font, slices.Length));
            CreateMissingPrefab("Assets/" + entry.HudPrefabResourceId_Ref.Path, () => BuildTribulationHud(font, slices[0].RefinementIds.Count));
            AssetDatabase.SaveAssets();
        }

        /// <summary>建立正式切片Canvas；所有显示布局仅在此编辑器工厂中设置。</summary>
        /// <param name="name">正式层级名。</param><param name="lobby">选角使用Overlay，局内沿用战斗相机。</param><returns>待保存Canvas根。</returns>
        /// <remarks>只创建内存节点，由CreateMissingPrefab保存并销毁。</remarks>
        private static GameObject TribulationCanvas(string name, bool lobby)
        {
            var root = RectObject(name, null); var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = lobby ? RenderMode.ScreenSpaceOverlay : RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 1; canvas.sortingOrder = 101;
            var scaler = root.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f; root.AddComponent<GraphicRaycaster>();
            if (lobby)
            {
                var cameraRoot = new GameObject("Lobby camera", typeof(Camera)); cameraRoot.transform.SetParent(root.transform, false);
                var camera = cameraRoot.GetComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color32(8, 15, 30, 255); camera.cullingMask = 0; camera.depth = -1;
            }
            return root;
        }

        /// <summary>创建入口职业卡与说明；数量由TbTribulationUi.stageIds提供。</summary>
        /// <param name="font">既有正式字体。</param><param name="count">源表职业数量。</param><returns>正式选角根。</returns>
        private static GameObject BuildTribulationLobby(Font font, int count)
        {
            var root = TribulationCanvas("TribulationLobby", true); var view = root.AddComponent<TribulationLobbyView>();
            var background = root.AddComponent<Image>(); background.color = new Color32(8, 15, 30, 255);
            view.TitleText = TribulationText(root.transform, "Title", font, 66, new Vector2(1600, 110), new Vector2(0, 375), TextAnchor.MiddleCenter);
            view.IntroText = TribulationText(root.transform, "Intro", font, 28, new Vector2(1480, 115), new Vector2(0, 245), TextAnchor.MiddleCenter);
            view.ErrorText = TribulationText(root.transform, "Error", font, 28, new Vector2(1500, 70), new Vector2(0, -420), TextAnchor.MiddleCenter);
            view.RoleButtons = new Button[count]; view.RoleNames = new Text[count]; view.RoleDescriptions = new Text[count]; view.RoleButtonLabels = new Text[count];
            for (int i = 0; i < count; i++)
            {
                var card = CreatePanel(root.transform, "Role-" + i, new Color32(21, 36, 54, 255));
                SetRect(card, Vector2.one * 0.5f, Vector2.one * 0.5f, new Vector2(620, 460), new Vector2((i - (count - 1) * 0.5f) * 680, -70));
                view.RoleNames[i] = TribulationText(card, "Name", font, 42, new Vector2(540, 80), new Vector2(0, 150), TextAnchor.MiddleCenter);
                view.RoleDescriptions[i] = TribulationText(card, "Description", font, 28, new Vector2(530, 185), new Vector2(0, 5), TextAnchor.MiddleLeft);
                view.RoleButtons[i] = CreateButton(card, "Enter", new Vector2(280, 80));
                SetRect((RectTransform)view.RoleButtons[i].transform, Vector2.one * 0.5f, Vector2.one * 0.5f, new Vector2(280, 80), new Vector2(0, -150));
                view.RoleButtonLabels[i] = TribulationText(view.RoleButtons[i].transform, "Label", font, 32, new Vector2(260, 70), Vector2.zero, TextAnchor.MiddleCenter);
            }
            return root;
        }

        /// <summary>创建附加职业状态、主动引劫、返回与炼器面板；候选卡数量由TbTribulationSlice提供。</summary>
        /// <param name="font">正式字体。</param><param name="choices">源表炼器候选数量。</param><returns>正式HUD根。</returns>
        private static GameObject BuildTribulationHud(Font font, int choices)
        {
            var root = TribulationCanvas("TribulationHud", false); var view = root.AddComponent<TribulationHudView>();
            var panel = CreatePanel(root.transform, "SliceStatus", new Color32(8, 15, 30, 205));
            panel.GetComponent<Image>().raycastTarget = false;
            SetRect(panel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(650, 235), new Vector2(345, -245));
            view.TitleText = TribulationText(panel, "Title", font, 30, new Vector2(610, 40), new Vector2(0, 88), TextAnchor.MiddleLeft);
            view.MechanismText = TribulationText(panel, "Mechanism", font, 24, new Vector2(610, 42), new Vector2(0, 42), TextAnchor.MiddleLeft);
            view.AltarText = TribulationText(panel, "Altar", font, 24, new Vector2(610, 66), new Vector2(0, -12), TextAnchor.MiddleLeft);
            view.RefinementText = TribulationText(panel, "Refinement", font, 23, new Vector2(610, 38), new Vector2(0, -67), TextAnchor.MiddleLeft);
            view.BossText = TribulationText(panel, "BossPhase", font, 23, new Vector2(610, 36), new Vector2(0, -101), TextAnchor.MiddleLeft);
            view.ActivateButton = CreateButton(root.transform, "ActivateAltar", new Vector2(330, 80));
            SetRect((RectTransform)view.ActivateButton.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(330, 80), new Vector2(0, 150));
            view.ActivateLabel = TribulationText(view.ActivateButton.transform, "Label", font, 30, new Vector2(310, 70), Vector2.zero, TextAnchor.MiddleCenter);
            view.ReturnButton = CreateButton(root.transform, "ReturnToLobby", new Vector2(220, 58));
            SetRect((RectTransform)view.ReturnButton.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(220, 58), new Vector2(-135, -145));
            view.ReturnLabel = TribulationText(view.ReturnButton.transform, "Label", font, 24, new Vector2(210, 54), Vector2.zero, TextAnchor.MiddleCenter);
            view.RewardRoot = RectObject("RefinementReward", root.transform); Stretch((RectTransform)view.RewardRoot.transform);
            view.RewardRoot.AddComponent<Image>().color = new Color32(4, 8, 16, 240);
            view.RewardTitleText = TribulationText(view.RewardRoot.transform, "Title", font, 50, new Vector2(1600, 90), new Vector2(0, 350), TextAnchor.MiddleCenter);
            view.RewardIntroText = TribulationText(view.RewardRoot.transform, "Intro", font, 26, new Vector2(1550, 90), new Vector2(0, 245), TextAnchor.MiddleCenter);
            view.RefinementButtons = new Button[choices]; view.RefinementNames = new Text[choices]; view.RefinementDescriptions = new Text[choices];
            for (int i = 0; i < choices; i++)
            {
                var button = CreateButton(view.RewardRoot.transform, "Choice-" + i, new Vector2(440, 420));
                SetRect((RectTransform)button.transform, Vector2.one * 0.5f, Vector2.one * 0.5f, new Vector2(440, 420), new Vector2((i - (choices - 1) * 0.5f) * 475, -65));
                button.GetComponent<Image>().color = new Color32(30, 59, 79, 255); view.RefinementButtons[i] = button;
                view.RefinementNames[i] = TribulationText(button.transform, "Name", font, 34, new Vector2(395, 90), new Vector2(0, 135), TextAnchor.MiddleCenter);
                view.RefinementDescriptions[i] = TribulationText(button.transform, "Description", font, 27, new Vector2(375, 240), new Vector2(0, -40), TextAnchor.MiddleLeft);
            }
            view.RewardRoot.SetActive(false);
            view.ReturnButton.transform.SetAsLastSibling();
            return root;
        }

        /// <summary>将新文本的字号和RectTransform写入待保存Prefab，运行时不再计算布局。</summary>
        /// <param name="parent">父节点。</param><param name="name">绑定名。</param><param name="font">正式字体。</param><param name="size">字号。</param>
        /// <param name="dimensions">固定尺寸。</param><param name="position">锚定偏移。</param><param name="alignment">对齐。</param><returns>新建空文本。</returns>
        private static Text TribulationText(Transform parent, string name, Font font, int size, Vector2 dimensions, Vector2 position, TextAnchor alignment)
        {
            var text = CreateText(parent, name, font, size, alignment);
            SetRect(text.rectTransform, Vector2.one * 0.5f, Vector2.one * 0.5f, dimensions, position);
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }
    }
}
