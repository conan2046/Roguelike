using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>全部 Hero/Monster 圆柱预制体的唯一编辑管理入口，汇总待导出差异。</summary>
    public sealed class CombatCylinderWindow : EditorWindow
    {
        private readonly List<string> pending = new List<string>();
        private int heroCount, monsterCount;
        private string error;
        private Vector2 scroll;

        /// <summary>菜单打开集中窗口，不修改任何资产或配置。</summary>
        [MenuItem("Roguelike/Combat/圆柱碰撞配置")]
        public static void Open() => GetWindow<CombatCylinderWindow>("圆柱碰撞配置");

        /// <summary>窗口获得焦点时重新读取保存资产和生成表，显示其他窗口刚保存的调整。</summary>
        private void OnFocus() => RefreshStatus();

        /// <summary>集中显示资源数量、未导出项及批量操作；不在重绘期间扫描资产。</summary>
        /// <remarks>按钮操作由用户显式触发；运行时禁用导出与生成，保护会话配置快照。</remarks>
        private void OnGUI()
        {
            EditorGUILayout.LabelField("全部角色与怪物", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Hero: " + heroCount + "    Monster: " + monsterCount);
            EditorGUILayout.HelpBox("先保存微调后的 Prefab，再在这里一次导出全部。重新进入战斗生效。", MessageType.Info);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            else EditorGUILayout.HelpBox(pending.Count == 0 ? "已保存预制体与配置一致。" : "待导出修改：" + pending.Count + " 项", pending.Count == 0 ? MessageType.Info : MessageType.Warning);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("导出全部已保存圆柱并生成 Luban", GUILayout.Height(32))) Run(CombatCylinderPipeline.Export);
                if (GUILayout.Button("批量生成缺失预制体（保留已有微调）")) Run(CombatCylinderPipeline.Generate);
            }
            if (GUILayout.Button("刷新修改状态")) RefreshStatus();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (string path in pending)
                if (GUILayout.Button(path, EditorStyles.linkLabel)) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            EditorGUILayout.EndScrollView();
        }

        /// <summary>执行明确点击的批量操作，将失败展示在同一窗口并重新校验状态。</summary>
        /// <param name="action">生成或导出操作。</param><remarks>操作可能创建资产或写表；异常不被当作成功。</remarks>
        private void Run(Action action)
        {
            try { action(); RefreshStatus(); }
            catch (Exception exception) { error = exception.Message; Debug.LogException(exception); }
        }

        /// <summary>按 ID 比较已保存的圆柱参数与生成 bytes，仅记录不一致资源路径。</summary>
        /// <remarks>只读资产，允许新生成但尚未导出的形状显示为待导出。</remarks>
        private void RefreshStatus()
        {
            pending.Clear(); heroCount = monsterCount = 0; error = null;
            try
            {
                var tables = CombatCylinderPipeline.ReadTables();
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] {CombatCylinderPipeline.Root + "/Hero", CombatCylinderPipeline.Root + "/Monster"}))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var shape = asset.GetComponentInChildren<CombatCylinderAuthoring>(true);
                    if (shape == null) { pending.Add(path); continue; }
                    if (shape.IsHero) heroCount++; else monsterCount++;
                    float[] actual = CombatCylinderPipeline.Values(shape, tables); float?[] expected;
                    if (shape.IsHero) { var c=tables.TbCharacter.Get(shape.ConfigId); expected=new[]{c.MoveRadiusPixels,c.MoveHeightPixels,c.MoveOffsetXPixels,c.MoveOffsetYPixels,c.MoveElevationPixels}; }
                    else { var c=tables.TbMonster.Get(shape.ConfigId); expected=new[]{c.MoveRadiusPixels,c.MoveHeightPixels,c.MoveOffsetXPixels,c.MoveOffsetYPixels,c.MoveElevationPixels}; }
                    for (int i=0;i<actual.Length;i++)
                        if (!expected[i].HasValue || Mathf.Abs(actual[i]-expected[i].Value)>Mathf.Max(1,Mathf.Abs(actual[i]))*0.000001f)
                        { pending.Add(path); break; }
                }
            }
            catch (Exception exception) { error = exception.Message; }
            Repaint();
        }
    }
}
