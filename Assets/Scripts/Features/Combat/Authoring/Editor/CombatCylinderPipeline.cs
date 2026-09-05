using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using cfg;
using Luban;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>圆柱预制体生成、源表导出及一致性检查；只操作独立资产，不保存 Scene。</summary>
    public static class CombatCylinderPipeline
    {
        public const string Root = "Assets/Prefabs/Combat";
        private static readonly string[] Fields = { "moveRadiusPixelsMilli", "moveHeightPixelsMilli", "moveOffsetXPixelsMilli", "moveOffsetYPixelsMilli", "moveElevationPixelsMilli" };

        /// <summary>编辑器工具从当前生成 bytes 读取表；不持有缓存，避免导出后沿用旧值。</summary>
        /// <returns>解析引用后的 Luban 表集合。</returns>
        public static Tables ReadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes("Assets/StreamingAssets/Config/Luban/" + name + ".bytes")));

        /// <summary>菜单调用时批量为全部角色和怪物创建缺失预制体；已有资产始终保留微调。</summary>
        /// <remarks>创建 Hero/Monster 子目录和轻量预览节点；外观由编辑器内存缓存绘制，不触碰 Scene 保存。</remarks>
        public static void Generate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play Mode。");
            var tables = ReadTables();
            var scenarios = tables.TbPerformanceScenario.DataList.Where(s => s.Kind == EPerformanceKind.Combat).ToArray();
            if (scenarios.Select(s => s.CombatRulesId).Distinct().Count() != 1 || scenarios.Select(s => s.PresentationId_Ref.ShaderName).Distinct().Count() != 1)
                throw new InvalidOperationException("多个战斗坐标比例或 Shader，需要先指定生成规则。");
            var scenario = scenarios.First();
            MigrateWeapons(tables);
            foreach (var h in tables.TbCharacter.DataList)
                GenerateOne(h.Id, h.Name, true, h.VisualSetId_Ref, scenario.CombatRulesId_Ref,
                    h.MoveRadiusPixels, h.MoveHeightPixels, h.MoveOffsetXPixels, h.MoveOffsetYPixels, h.MoveElevationPixels, scenario.PresentationId_Ref.ShaderName);
            foreach (var m in tables.TbMonster.DataList)
                    GenerateOne(m.Id, m.Name, false, m.VisualSetId_Ref, scenario.CombatRulesId_Ref,
                        m.MoveRadiusPixels, m.MoveHeightPixels, m.MoveOffsetXPixels, m.MoveOffsetYPixels, m.MoveElevationPixels, scenario.PresentationId_Ref.ShaderName);
            foreach (var weapon in tables.TbHeroWeapon.DataList.Where(w => !w.IntegratedInBody))
                GenerateOne(weapon.Id, WeaponName(weapon), false, weapon.VisualSetId_Ref, scenario.CombatRulesId_Ref,
                    null, null, null, null, null, scenario.PresentationId_Ref.ShaderName, weapon);
            AssetDatabase.Refresh();
        }

        /// <summary>按表配置创建单个 ANI 首帧模板；未配置资源仅在编辑器按图像包围盒初始化，导出后才成为正式数据。</summary>
        /// <param name="id">角色或怪物表 ID。</param><param name="name">资源显示名。</param><param name="hero">目录及表类型。</param>
        /// <param name="visual">外观集合。</param><param name="rules">世界比例来源。</param>
        /// <param name="radius">圆柱半径像素。</param><param name="height">高度像素。</param>
        /// <param name="x">地面横向偏移。</param><param name="y">地面纵向偏移。</param><param name="elevation">底面高度。</param>
        /// <param name="shaderName">表现表 Shader 名称。</param>
        /// <param name="weapon">独立武器层映射；为空时生成本体圆柱。</param>
        /// <remarks>外观按源资源在编辑器内存生成，不保存纹理副本；只创建缺失 Prefab。</remarks>
        private static void GenerateOne(int id, string name, bool hero, VisualSetConfig visual, CombatRulesConfig rules,
            float? radius, float? height, float? x, float? y, float? elevation, string shaderName, HeroWeaponConfig weapon = null)
        {
            string folder = weapon == null ? Root + (hero ? "/Hero" : "/Monster") : Root + "/Weapon/" + weapon.CharacterId_Ref.Name;
            string path = folder + "/" + id + "_" + name + ".prefab";
            if (File.Exists(path)) return;
            // 以保存组件的表 ID 识别已有资产，允许用户重命名文件，避免覆盖微调。
            if (Directory.Exists(folder))
                foreach (string existing in Directory.GetFiles(folder, "*.prefab"))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(existing.Replace('\\', '/'));
                    if (asset == null) continue;
                    var cylinder = asset.GetComponentInChildren<CombatCylinderAuthoring>(true);
                    var layer = asset.GetComponent<HeroWeaponAuthoring>();
                    if (weapon == null && cylinder != null && cylinder.ConfigId == id && cylinder.IsHero == hero) return;
                    if (weapon != null && layer != null && layer.WeaponConfigId == id) return;
                }
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            var appearance = CombatAuthoringPreview.Get(visual, rules, shaderName);
            float scale = rules.WorldUnitsPerPixel;
            radius = radius ?? appearance.Mesh.bounds.size.x / (2 * scale);
            height = height ?? appearance.Mesh.bounds.size.y / scale;
            x = x ?? 0; y = y ?? 0; elevation = elevation ?? 0;
            var root = new GameObject(name);
            try
            {
                var preview = new GameObject(visual.StandClipId_Ref == null ? "VisualPreview_NoIdle_FirstClip" : "VisualPreview"); preview.transform.SetParent(root.transform, false);
                if (weapon != null)
                {
                    root.AddComponent<HeroWeaponAuthoring>().WeaponConfigId = weapon.Id;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    return;
                }
                var node = new GameObject("MovementCollision"); node.transform.SetParent(root.transform, false);
                node.transform.localPosition = new Vector3(x.Value, elevation.Value + height.Value / 2, y.Value) * scale;
                var shape = node.AddComponent<CombatCylinderAuthoring>();
                shape.ConfigId = id; shape.IsHero = hero; shape.CombatRulesId = rules.Id;
                shape.Radius = radius.Value * scale; shape.Height = height.Value * scale;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>编辑工具从片段资源及显式后缀还原显示名，仅用于稳定资产文件名。</summary>
        /// <param name="weapon">TbHeroWeapon 对应资源。</param><returns>原始资源前缀。</returns>
        private static string WeaponName(HeroWeaponConfig weapon)
        {
            var clip = weapon.VisualSetId_Ref.StandClipId_Ref ?? weapon.VisualSetId_Ref.ClipIds_Ref.First();
            string stem = Path.GetFileNameWithoutExtension(clip.AniResourceId_Ref.Path);
            string suffix = "_" + clip.SourceSuffix;
            if (!stem.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidOperationException("动画后缀与资源名不一致。");
            return stem.Substring(0, stem.Length - suffix.Length);
        }

        /// <summary>依据 TbHeroWeapon 将历史误分类模板移入所属神将武器目录，保留资产 GUID 和预览引用。</summary>
        /// <param name="tables">已生成的本体及武器映射。</param>
        /// <remarks>只移除独立武器上的错误移动圆柱；本体和怪物模板不写入，微调保持原样。</remarks>
        private static void MigrateWeapons(Tables tables)
        {
            foreach (var weapon in tables.TbHeroWeapon.DataList.Where(w => !w.IntegratedInBody))
            {
                string name = weapon.Id + "_" + WeaponName(weapon);
                string source = Root + "/Hero/" + name + ".prefab";
                string folder = Root + "/Weapon/" + weapon.CharacterId_Ref.Name;
                string destination = folder + "/" + name + ".prefab";
                if (!File.Exists(source)) continue;
                Directory.CreateDirectory(folder + "/Preview"); AssetDatabase.Refresh();
                string error = AssetDatabase.MoveAsset(source, destination);
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                string preview = Root + "/Hero/Preview/" + name + ".asset";
                if (File.Exists(preview))
                {
                    error = AssetDatabase.MoveAsset(preview, folder + "/Preview/" + name + ".asset");
                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                }
                var root = PrefabUtility.LoadPrefabContents(destination);
                try
                {
                    foreach (var shape in root.GetComponentsInChildren<CombatCylinderAuthoring>(true))
                        UnityEngine.Object.DestroyImmediate(shape.gameObject);
                    var marker = root.GetComponent<HeroWeaponAuthoring>() ?? root.AddComponent<HeroWeaponAuthoring>();
                    marker.WeaponConfigId = weapon.Id;
                    PrefabUtility.SaveAsPrefabAsset(root, destination);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        /// <summary>从已保存 Prefab 获取有效圆柱并换算为表内像素字段，同时拒绝不支持的旋转/缩放。</summary>
        /// <param name="shape">预制体中的唯一圆柱节点。</param><param name="tables">当前转换规则。</param>
        /// <returns>与 Fields 顺序一致的五个数值。</returns>
        /// <exception cref="InvalidOperationException">配置标识、形状或节点变换非法。</exception>
        public static float[] Values(CombatCylinderAuthoring shape, Tables tables)
        {
            if (!(shape.Radius > 0) || !(shape.Height > 0)) throw new InvalidOperationException("圆柱半径和高度必须大于零。");
            if (shape.transform.parent == null || shape.transform.parent.parent != null ||
                shape.transform.localScale != Vector3.one || shape.transform.parent.localScale != Vector3.one ||
                Quaternion.Angle(shape.transform.localRotation, Quaternion.identity) != 0 ||
                Quaternion.Angle(shape.transform.parent.localRotation, Quaternion.identity) != 0 || shape.transform.parent.localPosition != Vector3.zero)
                throw new InvalidOperationException("圆柱必须是根节点直接子节点；根位置归零，旋转归零，缩放为一。请使用 Radius/Height 调整。");
            float scale = tables.TbCombatRules.Get(shape.CombatRulesId).WorldUnitsPerPixel;
            if (!(scale > 0)) throw new InvalidOperationException("世界像素比例非法。");
            Vector3 center = shape.transform.localPosition;
            var result = new[] {shape.Radius / scale, shape.Height / scale, center.x / scale, center.z / scale, (center.y - shape.Height / 2) / scale};
            if (result.Any(v => float.IsNaN(v) || float.IsInfinity(v))) throw new InvalidOperationException("圆柱必须为有限数值。");
            return result;
        }

        /// <summary>扫描两个编辑目录内的已保存模板，拒绝重复 ID 和多个碰撞节点。</summary>
        /// <returns>通过结构校验的模板组件。</returns>
        private static List<CombatCylinderAuthoring> Shapes()
        {
            var result = new List<CombatCylinderAuthoring>(); var ids = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] {Root + "/Hero", Root + "/Monster"}))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var shapes = root.GetComponentsInChildren<CombatCylinderAuthoring>(true);
                if (shapes.Length != 1) throw new InvalidOperationException(root.name + " 必须包含一个圆柱节点。");
                var shape = shapes[0];
                if (!ids.Add(shape.IsHero + ":" + shape.ConfigId)) throw new InvalidOperationException("重复圆柱 ID: " + shape.ConfigId);
                result.Add(shape);
            }
            if (result.Count == 0) throw new InvalidOperationException("尚未生成圆柱预制体。");
            return result;
        }

        /// <summary>按钮或菜单调用时将保存模板量化到三位小数，按千分整数写回源 Excel 并生成 Luban，异常时恢复源表。</summary>
        /// <remarks>不保存 Scene/Prefab；未保存的 Prefab Stage 会拒绝导出，防止导出旧值。生成日志写入 Library。</remarks>
        public static void Export()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play Mode。");
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty) throw new InvalidOperationException("请先保存正在编辑的 Prefab，再导出。");
            var tables = ReadTables(); var shapes = Shapes();
            var backups = new Dictionary<string, byte[]>();
            try
            {
                foreach (var group in shapes.GroupBy(s => s.IsHero))
                {
                    string kind = group.Key ? "Character" : "Monster";
                    string path = Directory.GetFiles("Config/Datas/Tables", "*_" + kind + "Config.xlsx").Single();
                    backups.Add(path, File.ReadAllBytes(path));
                    var updates = group.ToDictionary(s => s.ConfigId, s => Values(s, tables).Select(v => ConfigNumber.Encode(v)).ToArray());
                    ConfigWorkbookWriter.Patch(path, Fields, updates);
                }
                var info = new ProcessStartInfo("pwsh", "-NoProfile -File Tools/Config/generate.ps1")
                { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(info))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    File.WriteAllText("Library/CombatCylinderExport.log", stdout.Result + stderr.Result);
                    if (process.ExitCode != 0) throw new InvalidOperationException("Luban 生成失败，参见 Library/CombatCylinderExport.log。");
                }
            }
            catch { foreach (var pair in backups) File.WriteAllBytes(pair.Key, pair.Value); throw; }
            AssetDatabase.Refresh(); ValidateSaved();
            Debug.Log("圆柱配置已导出并生成 Luban。重新进入战斗生效。");
        }

        /// <summary>校验已保存 Prefab 与当前生成 bytes 的圆柱字段一致，供导出及人工验收调用。</summary>
        /// <remarks>只读资产和数据；浮点容差只覆盖像素/世界换算舍入。</remarks>
        public static void ValidateSaved()
        {
            var tables = ReadTables();
            foreach (var shape in Shapes())
            {
                float?[] expected;
                if (shape.IsHero) { var c = tables.TbCharacter.Get(shape.ConfigId); expected = new[] {c.MoveRadiusPixels,c.MoveHeightPixels,c.MoveOffsetXPixels,c.MoveOffsetYPixels,c.MoveElevationPixels}; }
                else { var c = tables.TbMonster.Get(shape.ConfigId); expected = new[] {c.MoveRadiusPixels,c.MoveHeightPixels,c.MoveOffsetXPixels,c.MoveOffsetYPixels,c.MoveElevationPixels}; }
                float[] actual = Values(shape, tables);
                for (int i = 0; i < actual.Length; i++)
                    if (!expected[i].HasValue || Mathf.Abs(actual[i] - expected[i].Value) > 0.00051f)
                        throw new InvalidOperationException(shape.name + " 尚未导出: " + Fields[i]);
            }
        }
    }
}
