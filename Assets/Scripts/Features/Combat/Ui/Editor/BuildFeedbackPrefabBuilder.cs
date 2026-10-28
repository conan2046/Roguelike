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
        /// <summary>通过本工程MCP调用时只创建本功能正式反馈Prefab，并为既有选角补目标／开关绑定。</summary>
        /// <remarks>不保存Scene，不覆盖已有反馈Prefab，不生成任何预览资产；修改选角前备份且拒绝未保存的编辑。</remarks>
        /// <exception cref="InvalidOperationException">配置、Prefab或既有绑定无效。</exception>
        public static void BuildFeedbackAssets()
        {
            var tables = new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config/Luban", name + ".bytes"))));
            var config = tables.TbBuildFeedbackUi.DataList.Single(); var font = LoadOrCreateFont();
            CreateMissingPrefab("Assets/" + config.PrefabResourceId_Ref.Path, () => BuildFeedbackPanel(font));
            var entry = tables.TbTribulationUi.DataList.Single(e => e.IsEntry);
            string lobbyPath = "Assets/" + entry.LobbyPrefabResourceId_Ref.Path;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(lobbyPath);
            if (asset == null || EditorUtility.IsDirty(asset)) throw new InvalidOperationException("Lobby asset is missing or has unsaved edits.");
            string backup = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".local/build-feedback-p0-20261027/before", lobbyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)); if (!File.Exists(backup)) File.Copy(lobbyPath, backup);
            var root = PrefabUtility.LoadPrefabContents(lobbyPath);
            try
            {
                var view = root.GetComponent<TribulationLobbyView>(); int count = entry.StageIds.Count;
                if (view == null || view.RoleNames.Length != count) throw new InvalidOperationException("Lobby role bindings are invalid.");
                if (view.ContentToggles != null && view.ContentToggles.Length == count && view.ContentToggles.All(t => t != null)) return;
                view.GoalDescriptions = new Text[count]; view.ContentLabels = new Text[count]; view.ContentToggles = new Toggle[count];
                for (int i = 0; i < count; i++)
                {
                    Transform card = view.RoleNames[i].transform.parent;
                    ((RectTransform)card).sizeDelta = new Vector2(620, 540);
                    SetRect(view.RoleDescriptions[i].rectTransform, Vector2.one * .5f, Vector2.one * .5f, new Vector2(530, 130), new Vector2(0,45));
                    view.RoleDescriptions[i].fontSize = 24;
                    view.GoalDescriptions[i] = TribulationText(card, "ContentGoal", font, 21, new Vector2(530,95), new Vector2(0,-75), TextAnchor.MiddleLeft);
                    SetRect((RectTransform)view.RoleButtons[i].transform, Vector2.one*.5f, Vector2.one*.5f, new Vector2(280,70), new Vector2(0,-165));
                    var toggleRoot = RectObject("ContentEnabled",card); var toggle = toggleRoot.AddComponent<Toggle>();
                    SetRect((RectTransform)toggleRoot.transform, Vector2.one*.5f, Vector2.one*.5f, new Vector2(540,45), new Vector2(0,-235));
                    var background = RectObject("Background",toggleRoot.transform).AddComponent<Image>();
                    SetRect(background.rectTransform,Vector2.one*.5f,Vector2.one*.5f,new Vector2(30,30),new Vector2(-245,0));
                    background.color = new Color32(65,78,95,255);
                    var mark = RectObject("Checkmark",background.transform).AddComponent<Image>();
                    SetRect(mark.rectTransform,Vector2.one*.5f,Vector2.one*.5f,new Vector2(22,22),Vector2.zero);
                    mark.color = new Color32(100,220,145,255); toggle.targetGraphic=background; toggle.graphic=mark; toggle.isOn=false;
                    view.ContentToggles[i]=toggle;
                    view.ContentLabels[i]=TribulationText(toggleRoot.transform,"Label",font,18,new Vector2(470,40),new Vector2(25,0),TextAnchor.MiddleLeft);
                }
                PrefabUtility.SaveAsPrefabAsset(root,lobbyPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>创建独立正式反馈画布，保留既有HUD和结算重开按钮；不复制纹理或材质。</summary>
        /// <param name="font">已有正式中文字体。</param><returns>由调用方保存并销毁的内存Prefab根。</returns>
        private static GameObject BuildFeedbackPanel(Font font)
        {
            var root=TribulationCanvas("BuildFeedbackPanel",false); var canvas=root.GetComponent<Canvas>();
            canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.pixelPerfect=true;canvas.sortingOrder=150;
            var view=root.AddComponent<BuildFeedbackPanelView>();
            view.HudText=TribulationText(root.transform,"MechanismFeedback",font,22,new Vector2(600,75),new Vector2(0,385),TextAnchor.MiddleCenter);
            var draft=CreatePanel(root.transform,"DraftControls",new Color32(15,25,42,245));
            SetRect(draft,Vector2.one*.5f,Vector2.one*.5f,new Vector2(950,85),new Vector2(0,-300));view.DraftControls=draft.gameObject;
            view.RerollButton=FeedbackButton(draft,"Reroll",font,new Vector2(-280,10),new Vector2(260,50),out view.RerollText);
            view.SkipButton=FeedbackButton(draft,"Skip",font,new Vector2(220,10),new Vector2(520,50),out view.SkipText);
            view.DraftStatus=TribulationText(draft,"DraftStatus",font,18,new Vector2(900,28),new Vector2(0,-28),TextAnchor.MiddleCenter);
            var settlement=CreatePanel(root.transform,"BuildSummary",new Color32(15,25,42,255));
            SetRect(settlement,Vector2.one*.5f,Vector2.one*.5f,new Vector2(1100,240),new Vector2(0,20));view.SettlementRoot=settlement.gameObject;
            var viewport=RectObject("SummaryViewport",settlement);SetRect((RectTransform)viewport.transform,Vector2.one*.5f,Vector2.one*.5f,new Vector2(1030,155),new Vector2(0,28));
            var image=viewport.AddComponent<Image>();image.color=Color.white;var mask=viewport.AddComponent<Mask>();mask.showMaskGraphic=false;
            var scroll=viewport.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.vertical=true;scroll.viewport=(RectTransform)viewport.transform;
            view.SummaryText=CreateText(viewport.transform,"Summary",font,18,TextAnchor.UpperLeft);
            SetRect(view.SummaryText.rectTransform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(1000,400),Vector2.zero);
            view.SummaryText.rectTransform.pivot=new Vector2(.5f,1);scroll.content=view.SummaryText.rectTransform;
            view.DetailsButton=FeedbackButton(settlement,"Details",font,new Vector2(-350,-80),new Vector2(250,35),out view.DetailsText);
            view.RetryButton=FeedbackButton(settlement,"RetrySave",font,new Vector2(350,-80),new Vector2(250,35),out view.RetryText);
            view.SaveText=TribulationText(settlement,"SaveStatus",font,18,new Vector2(400,30),new Vector2(0,-80),TextAnchor.MiddleCenter);
            return root;
        }

        /// <summary>为正式Prefab创建带文本绑定的按钮，位置只作为资产初始布局保存。</summary>
        /// <param name="parent">所属面板。</param><param name="name">技术节点名称。</param><param name="font">正式字体。</param>
        /// <param name="position">Prefab局部位置。</param><param name="size">Prefab尺寸。</param><param name="label">运行时填充的文本绑定。</param>
        /// <returns>不含业务文本或次数默认值的按钮。</returns>
        private static Button FeedbackButton(Transform parent,string name,Font font,Vector2 position,Vector2 size,out Text label)
        {
            var button=CreateButton(parent,name,size);SetRect((RectTransform)button.transform,Vector2.one*.5f,Vector2.one*.5f,size,position);
            label=TribulationText(button.transform,"Label",font,20,size-Vector2.one*8,Vector2.zero,TextAnchor.MiddleCenter);return button;
        }

        /// <summary>本功能初次联调时只修正自有反馈Prefab的画布模式，兼容当前正式Overlay HUD，不改用户HUD。</summary>
        /// <remarks>仅通过已核验本工程MCP调用；拒绝覆盖编辑器内未保存的修改，不保存Scene。</remarks>
        /// <exception cref="InvalidOperationException">正式资源不存在或正在手工编辑。</exception>
        public static void AlignFeedbackCanvasWithFormalHud()
        {
            var tables=new Tables(name=>new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,"Config/Luban",name+".bytes"))));
            string path="Assets/"+tables.TbBuildFeedbackUi.DataList.Single().PrefabResourceId_Ref.Path;
            var asset=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if(asset==null||EditorUtility.IsDirty(asset))throw new InvalidOperationException("Feedback prefab has unsaved edits.");
            var root=PrefabUtility.LoadPrefabContents(path);
            try { var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.pixelPerfect=true;canvas.worldCamera=null;
                root.transform.Find("BuildSummary/SummaryViewport").GetComponent<Image>().color=Color.white;
                SetRect((RectTransform)root.transform.Find("DraftControls"),Vector2.one*.5f,Vector2.one*.5f,new Vector2(950,85),new Vector2(0,-300));
                ((RectTransform)root.transform.Find("DraftControls/DraftStatus")).anchoredPosition=new Vector2(0,-28);
                PrefabUtility.SaveAsPrefabAsset(root,path); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
