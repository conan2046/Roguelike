using System;
using Roguelike.Features.Combat.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Ui.Editor
{
    public static partial class CombatUiPrefabBuilder
    {
        /// <summary>事件功能数据完成后只创建三份缺失的正式 UI 资产，已有布局完全保留。</summary>
        /// <remarks>新面板复用现有升级面板的布局副本；仅生成任务要求的 Prefab，不生成纹理、网格或预览副本。</remarks>
        public static void BuildEventAssets()
        {
            Font font = LoadOrCreateFont();
            CreateMissingPrefab(Folder + "/ChestRewardPanel.prefab", () => BuildChestPanel(font));
            CreateMissingPrefab(Folder + "/RunEventHud.prefab", () => BuildEventHud(font));
            CreateMissingPrefab(Folder + "/CombatChestWorld.prefab", () => BuildChestMarker(font));
            AssetDatabase.SaveAssets();
        }

        /// <summary>复制已有正式升级面板布局作为宝箱面板，并增加单项进化或耗尽奖励的绑定。</summary>
        /// <param name="font">既有正式中文字体。</param>
        /// <returns>待保存的独立正式 Prefab 内存根。</returns>
        /// <remarks>不修改源升级面板，不保存场景；调用方保存后销毁内存对象。</remarks>
        /// <exception cref="InvalidOperationException">既有面板不存在或绑定不完整。</exception>
        private static GameObject BuildChestPanel(Font font)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/UpgradeChoicePanel.prefab");
            if (source == null) throw new InvalidOperationException("Formal upgrade panel is required as chest layout source.");
            GameObject root = UnityEngine.Object.Instantiate(source);
            if (PrefabUtility.IsPartOfPrefabInstance(root)) PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = "ChestRewardPanel";
            UpgradeChoicePanelView old = root.GetComponent<UpgradeChoicePanelView>();
            if (old == null || old.TitleText == null || old.CardContainer == null) throw new InvalidOperationException("Upgrade panel layout bindings are incomplete.");
            Text title = old.TitleText;
            Transform cards = old.CardContainer;
            UnityEngine.Object.DestroyImmediate(old);
            ChestRewardPanelView view = root.AddComponent<ChestRewardPanelView>();
            GameObject single = RectObject("SingleReward", root.transform);
            SetRect((RectTransform)single.transform, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(1100,480), new Vector2(0,-20));
            Text name = CreateText(single.transform, "RewardName", font, 60, TextAnchor.MiddleCenter);
            name.color = new Color32(255,216,125,255);
            SetRect(name.rectTransform,new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(1000,110),new Vector2(0,140));
            Text description = CreateText(single.transform,"Description",font,32,TextAnchor.MiddleCenter);
            SetRect(description.rectTransform,new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(1000,150),new Vector2(0,0));
            Button claim = CreateButton(single.transform,"ClaimButton",new Vector2(360,88));
            SetRect((RectTransform)claim.transform,new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(360,88),new Vector2(0,-170));
            Text label = CreateText(claim.transform,"ClaimText",font,34,TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            Bind(view,"titleText",title,"rewardNameText",name,"descriptionText",description,"claimText",label,
                "claimButton",claim,"cardContainer",cards,"singleRewardRoot",single);
            return root;
        }

        /// <summary>创建事件状态与世界宝箱投影容器，位置、字体和尺寸固定保存在正式 Prefab。</summary>
        /// <param name="font">既有正式中文字体。</param>
        /// <returns>待保存内存根。</returns>
        /// <remarks>临时根由保存调用方销毁，不附带任何预览资产。</remarks>
        private static GameObject BuildEventHud(Font font)
        {
            GameObject root = RectObject("RunEventHud",null);
            Stretch((RectTransform)root.transform);
            RunEventHudView view = root.AddComponent<RunEventHudView>();
            GameObject markers = RectObject("ChestMarkers",root.transform);
            Stretch((RectTransform)markers.transform);
            Text notice = CreateText(root.transform,"Notice",font,30,TextAnchor.MiddleCenter);
            notice.color = new Color32(255,220,138,255);
            SetRect(notice.rectTransform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(1700,46),new Vector2(0,-128));
            Text elite = CreateText(root.transform,"EliteStatus",font,26,TextAnchor.MiddleCenter);
            elite.color = new Color32(255,155,145,255);
            SetRect(elite.rectTransform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(1700,42),new Vector2(0,-171));
            Text status = CreateText(root.transform,"BuildStatus",font,25,TextAnchor.MiddleCenter);
            SetRect(status.rectTransform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(1700,42),new Vector2(0,-212));
            Bind(view,"noticeText",notice,"eliteText",elite,"statusText",status,"chestMarkerContainer",markers.transform);
            return root;
        }

        /// <summary>用正式 uGUI 图形构成可辨识宝箱图标；名称由 TbChestProfile 运行时填充。</summary>
        /// <param name="font">正式中文字体。</param>
        /// <returns>待保存的场景宝箱投影 Prefab 根。</returns>
        /// <remarks>图形布局在资产中序列化；运行时只更新世界投影位置，不生成替代贴图。</remarks>
        private static GameObject BuildChestMarker(Font font)
        {
            GameObject root = RectObject("CombatChestWorld",null);
            ((RectTransform)root.transform).sizeDelta = new Vector2(100,90);
            CombatChestWorldView view = root.AddComponent<CombatChestWorldView>();
            ChestRect(root.transform,"Shadow",new Vector2(76,48),new Vector2(0,-10),new Color32(26,20,12,210));
            ChestRect(root.transform,"Body",new Vector2(64,40),new Vector2(0,-6),new Color32(132,77,27,255));
            ChestRect(root.transform,"Lid",new Vector2(70,16),new Vector2(0,18),new Color32(213,156,56,255));
            ChestRect(root.transform,"LeftBand",new Vector2(7,39),new Vector2(-23,-5),new Color32(244,198,85,255));
            ChestRect(root.transform,"RightBand",new Vector2(7,39),new Vector2(23,-5),new Color32(244,198,85,255));
            ChestRect(root.transform,"Lock",new Vector2(12,16),new Vector2(0,3),new Color32(255,222,108,255));
            Text name = CreateText(root.transform,"Name",font,20,TextAnchor.MiddleCenter);
            SetRect(name.rectTransform,new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(220,34),new Vector2(0,49));
            name.color = new Color32(255,226,153,255);
            Bind(view,"nameText",name);
            return root;
        }

        /// <summary>在正式宝箱 Prefab 中增加一块不可拦截指针的固定图形。</summary>
        /// <param name="parent">宝箱内存根。</param><param name="name">编辑节点名。</param>
        /// <param name="size">序列化尺寸。</param><param name="position">序列化位置。</param><param name="color">美术样式色。</param>
        /// <remarks>只由编辑器生成正式 Prefab 时调用，运行时不调整布局。</remarks>
        private static void ChestRect(Transform parent,string name,Vector2 size,Vector2 position,Color32 color)
        {
            Image image = RectObject(name,parent).AddComponent<Image>();
            image.color=color; image.raycastTarget=false;
            SetRect(image.rectTransform,new Vector2(.5f,.5f),new Vector2(.5f,.5f),size,position);
        }
    }
}
