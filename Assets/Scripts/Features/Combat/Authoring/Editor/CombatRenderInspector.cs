using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using cfg;
using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>在现有 Prefab Inspector 编辑 TbCombatRender；待保存值只驻留编辑器内存，不新增 Prefab 配置副本。</summary>
    internal static class CombatRenderInspector
    {
        private static readonly Dictionary<int, int> Pending = new Dictionary<int, int>();
        private static Tables tables;
        private static long layerVersion, presentationVersion;
        private static string error;

        /// <summary>Inspector 重绘时按生成物时间戳复用配置，避免每次控件绘制重新读取全部 bytes。</summary>
        /// <returns>当前已生成且强引用完整的 Luban 快照。</returns>
        /// <exception cref="IOException">生成数据不可读。</exception>
        private static Tables ReadTables()
        {
            long currentLayer = File.GetLastWriteTimeUtc("Assets/StreamingAssets/Config/Luban/tbcombatrender.bytes").Ticks;
            long currentPresentation = File.GetLastWriteTimeUtc("Assets/StreamingAssets/Config/Luban/tbcombatpresentation.bytes").Ticks;
            if (tables != null && layerVersion == currentLayer && presentationVersion == currentPresentation) return tables;
            tables = CombatCylinderPipeline.ReadTables();
            layerVersion = currentLayer;
            presentationVersion = currentPresentation;
            Pending.Clear();
            return tables;
        }

        /// <summary>从所有表现方案收集已引用的层级行；同一行复用时只显示一次。</summary>
        /// <param name="source">当前配置快照。</param>
        /// <returns>按表 ID 排序的已引用层级绑定。</returns>
        /// <exception cref="InvalidOperationException">源表目录包含空引用。</exception>
        private static IEnumerable<CombatRenderConfig> Rows(Tables source)
        {
            var result = source.TbCombatPresentation.DataList.SelectMany(item => item.RenderConfigIds_Ref).ToArray();
            if (result.Any(item => item == null)) throw new InvalidOperationException("层级目录存在空引用，请先检查 Luban 源表。");
            return result.GroupBy(item => item.Id).Select(group => group.First()).OrderBy(item => item.Id);
        }

        /// <summary>在单位碰撞 Inspector 中按角色/怪物强引用找到模型绑定，保留原有碰撞与 Transform。</summary>
        /// <param name="shape">当前选中的角色或怪物编辑标识。</param>
        /// <remarks>仅绘制源表控件；单击保存按钮前不写表、不修改 Prefab。</remarks>
        public static void DrawModel(CombatCylinderAuthoring shape)
        {
            if (shape == null) return;
            try
            {
                Tables source = ReadTables();
                int visual = shape.IsHero ? source.TbCharacter.Get(shape.ConfigId).VisualSetId : source.TbMonster.Get(shape.ConfigId).VisualSetId;
                DrawRows(source, Rows(source).Where(row => row.VisualSetId == visual).ToArray());
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
        }

        /// <summary>在技能 Inspector 中逐片段显示层级；表现节点可仅查看飞行、命中或全部范围片段。</summary>
        /// <param name="skillId">当前 Prefab 对应 TbSkill.id。</param>
        /// <param name="kind">可选表现用途；空时显示本技能全部片段。</param>
        /// <remarks>只保存内存中的待导出队列，场景、Prefab 和技能几何保持不变。</remarks>
        public static void DrawSkill(int skillId, SkillVisualPlacementAuthoring.VisualKind? kind = null)
        {
            try
            {
                Tables source = ReadTables();
                var rows = Rows(source).Where(row => row.SkillId == skillId);
                if (kind.HasValue)
                {
                    EAnimationAction action = kind.Value == SkillVisualPlacementAuthoring.VisualKind.Projectile ? EAnimationAction.Projectile :
                        kind.Value == SkillVisualPlacementAuthoring.VisualKind.Impact ? EAnimationAction.Impact : EAnimationAction.Area;
                    rows = rows.Where(row => row.ClipId_Ref.Action == action);
                }
                DrawRows(source, rows.ToArray());
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
        }

        /// <summary>在独立武器编辑标识上显示其真实 TbVisualSet 模型绑定。</summary>
        /// <param name="weaponId">TbHeroWeapon.id。</param>
        /// <remarks>不改变武器关联或根节点缩放。</remarks>
        public static void DrawWeapon(int weaponId)
        {
            try
            {
                Tables source = ReadTables();
                int visual = source.TbHeroWeapon.Get(weaponId).VisualSetId;
                DrawRows(source, Rows(source).Where(row => row.VisualSetId == visual).ToArray());
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
        }

        /// <summary>以中文标签显示逐项队列和显式保存入口；运行中只读，不把层级写入组件。</summary>
        /// <param name="source">当前生成配置。</param>
        /// <param name="rows">当前选中对象的全部或指定用途绑定。</param>
        /// <remarks>保存操作调用受保护的源表导出；不创建渲染资源。</remarks>
        private static void DrawRows(Tables source, CombatRenderConfig[] rows)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("模型 / 特效前后层级", EditorStyles.boldLabel);
            if (rows.Length == 0)
            {
                EditorGUILayout.HelpBox("当前对象没有可绘制片段或缺少层级绑定；运行时不会使用统一层级兜底。", MessageType.Info);
                return;
            }
            EditorGUILayout.HelpBox("数字越大越靠前。每个模型、每个技能片段独立配置；保存到 Excel 并生成 Luban 后，重新进入战斗生效。", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                foreach (CombatRenderConfig row in rows)
                {
                    int value = Pending.TryGetValue(row.Id, out int edited) ? edited : row.RenderQueue;
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(new GUIContent(row.Name + (row.ClipId.HasValue ? " [" + row.ClipId + "]" : ""),
                        "TbCombatRender " + row.Id + "；渲染队列越大越靠前。"), EditorStyles.wordWrappedLabel);
                    int next = EditorGUILayout.IntField(value, GUILayout.Width(EditorGUIUtility.fieldWidth));
                    EditorGUILayout.EndHorizontal();
                    if (EditorGUI.EndChangeCheck()) { Pending[row.Id] = next; error = null; }
                }
                bool changed = rows.Any(row => Pending.TryGetValue(row.Id, out int value) && value != row.RenderQueue);
                using (new EditorGUI.DisabledScope(!changed))
                    if (GUILayout.Button("保存层级到 Excel 并生成 Luban"))
                    {
                        try { Export(source, rows); error = null; }
                        catch (Exception exception) { error = exception.Message; }
                    }
            }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        /// <summary>用户点击保存后仅导出当前对象改动的队列，成功后重新导入生成数据。</summary>
        /// <param name="source">构建待编辑控件时的生成快照。</param>
        /// <param name="rows">当前 Inspector 所有可编辑绑定。</param>
        /// <remarks>写源 Excel、调用 Luban 并导入对应 bytes；失败恢复本次源表和层级 bytes，不保存 Scene/Prefab。</remarks>
        /// <exception cref="InvalidOperationException">运行中保存、源表发生冲突或配置非法。</exception>
        /// <exception cref="IOException">源表被占用或生成物不可写。</exception>
        private static void Export(Tables source, CombatRenderConfig[] rows)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play Mode 再保存层级。");
            var changed = rows.Where(row => Pending.TryGetValue(row.Id, out int value) && value != row.RenderQueue).ToArray();
            if (changed.Length == 0) return;
            string path = Directory.GetFiles("Config/Datas/Tables", "*_CombatRenderConfig.xlsx").Single();
            string dataPath = "Assets/StreamingAssets/Config/Luban/tbcombatrender.bytes";
            byte[] sourceBackup = File.ReadAllBytes(path), dataBackup = File.ReadAllBytes(dataPath);
            int feedback = source.TbCombatPresentation.DataList
                .Where(presentation => presentation.RenderConfigIds.Any(id => changed.Any(row => row.Id == id)))
                .Min(presentation => presentation.FeedbackRenderQueue);
            try
            {
                SaveQueues(path, changed.ToDictionary(row => row.Id, row => row.RenderQueue),
                    changed.ToDictionary(row => row.Id, row => Pending[row.Id]), feedback);
                CombatCollisionPipeline.RunLuban();
                AssetDatabase.ImportAsset(dataPath, ImportAssetOptions.ForceUpdate);
            }
            catch
            {
                File.WriteAllBytes(path, sourceBackup);
                File.WriteAllBytes(dataPath, dataBackup);
                AssetDatabase.ImportAsset(dataPath, ImportAssetOptions.ForceUpdate);
                throw;
            }
            foreach (CombatRenderConfig row in changed) Pending.Remove(row.Id);
            tables = null;
            CombatAuthoringPreview.Clear();
            SkillPrefabPreview.RefreshCurrent();
            SceneView.RepaintAll();
            Debug.Log("层级已保存到 TbCombatRender 并通过 Luban 生成；重新进入战斗后生效。");
        }

        /// <summary>导出前验证改动值和源表基线，拒绝覆盖外部修改，只写指定行的 renderQueue。</summary>
        /// <param name="path">正式或测试副本的 CombatRenderConfig 源表路径。</param>
        /// <param name="expected">界面打开时各行的已生成队列基线。</param>
        /// <param name="updates">用户明确修改的行及新队列。</param>
        /// <param name="feedbackQueue">使用这些绑定的方案中最低的反馈队列。</param>
        /// <remarks>只修改源表数字单元格；调用方负责生成、重入与异常回滚，不调用资源或场景 API。</remarks>
        /// <exception cref="ArgumentNullException">缺少基线或更新集合。</exception>
        /// <exception cref="InvalidOperationException">行缺失、数值非法或 Excel 中有外部改动。</exception>
        /// <exception cref="IOException">工作簿被占用或不可写。</exception>
        internal static void SaveQueues(string path, IReadOnlyDictionary<int, int> expected,
            IReadOnlyDictionary<int, int> updates, int feedbackQueue)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (updates == null) throw new ArgumentNullException(nameof(updates));
            var current = ConfigWorkbookWriter.ReadValues(path, new[] { "renderQueue" }, updates.Keys);
            foreach (var update in updates)
            {
                CombatRenderCatalog.ValidateQueue(update.Value, feedbackQueue);
                if (!expected.TryGetValue(update.Key, out int baseline) ||
                    !int.TryParse(current[update.Key][0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int actual) ||
                    actual != baseline && actual != update.Value)
                    throw new InvalidOperationException("层级源表已被外部修改，请重新生成并刷新 Inspector 后再保存：" + update.Key);
            }
            ConfigWorkbookWriter.Patch(path, new[] { "renderQueue" }, updates.ToDictionary(pair => pair.Key, pair => new[] { pair.Value }));
        }
    }
}
