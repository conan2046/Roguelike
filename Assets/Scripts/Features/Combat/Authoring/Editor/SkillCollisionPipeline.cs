using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using cfg;
using ProjectX.Migration;
using Roguelike.Animation;
using Roguelike.Animation.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>按 TbSkill 生成编辑预制体，将圆形节点导出为 Luban 千分整数；保持现有角色怪物资产。</summary>
    public static class SkillCollisionPipeline
    {
        public const string Root = "Assets/Prefabs/Combat/Skill";
        private static readonly string[] Fields = { "projectileRadiusMilli", "projectileOffsetXMilli", "projectileOffsetYMilli" };

        /// <summary>菜单批量创建 TbSkill 目录内缺失的预制体，按保存组件的 ID 识别用户重命名资产。</summary>
        /// <remarks>只创建技能预制体；首帧预览在打开 Prefab Stage 时临时生成；已有资产不覆盖，临时对象在 finally 清理，不保存 Scene。</remarks>
        /// <exception cref="InvalidOperationException">编辑器运行中、规则不唯一或资源无效。</exception>
        [MenuItem("Roguelike/Combat/技能碰撞配置/生成缺失技能预制体")]
        public static void Generate()
        {
            EnsureEditable();
            var tables = CombatCylinderPipeline.ReadTables();
            var scenarios = tables.TbPerformanceScenario.DataList.Where(s => s.Kind == EPerformanceKind.Combat).ToArray();
            if (scenarios.Length == 0 || scenarios.Select(s => s.CombatRulesId).Distinct().Count() != 1 ||
                scenarios.Select(s => s.PresentationId_Ref.ShaderName).Distinct().Count() != 1)
                throw new InvalidOperationException("生成技能预览需要唯一坐标比例与 Shader。");
            Directory.CreateDirectory(Root); AssetDatabase.Refresh();
            var existing = Shapes().Select(s => s.SkillId).ToHashSet();
            foreach (var skill in tables.TbSkill.DataList)
                if (!existing.Contains(skill.Id)) GenerateOne(skill, scenarios[0].CombatRulesId_Ref, scenarios[0].PresentationId_Ref);
            AssetDatabase.Refresh();
            Debug.Log("技能预制体生成完成；已有编辑资产保留。");
        }

        /// <summary>从技能视觉集合首片段生成首帧预览及碰撞节点；未投入战斗的技能以图像宽度初始化编辑半径。</summary>
        /// <param name="skill">TbSkill 行，正式半径/偏移只来自该行。</param>
        /// <param name="rules">TbCombatRules 世界像素比例。</param>
        /// <param name="presentation">TbCombatPresentation Shader 与采样方式。</param>
        /// <remarks>创建技能 Prefab；临时读取源图集估算未配置条目的初始编辑半径；预览初始化值不自动写入正式表。临时 GameObject 必定销毁。</remarks>
        private static void GenerateOne(SkillConfig skill, CombatRulesConfig rules, CombatPresentationConfig presentation)
        {
            var clip = skill.VisualSetId_Ref.ClipIds_Ref.First();
            var data = CocosAniData.Parse(File.ReadAllBytes("Assets/StreamingAssets/" + clip.AniResourceId_Ref.Path));
            var texture = new Texture2D(2, 2) { name = skill.Name + " preview atlas", filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), presentation.TextureFilterMode) };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes("Assets/StreamingAssets/" + clip.TextureResourceId_Ref.Path)))
                throw new InvalidOperationException("技能纹理解码失败: " + skill.Id);
            var mesh = AniMeshFactory.CreateFrame(AniFrameLayout.Build(data, 0, 0, texture.width, texture.height), rules.WorldUnitsPerPixel * skill.VisualSetId_Ref.ScalePermille / 1000f);
            mesh.name = skill.Name + " first frame";
            float previewRadius = ConfigNumber.Decode(ConfigNumber.Encode(mesh.bounds.size.x / 2));
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(mesh);
            var root = new GameObject(skill.Name);
            try
            {
                var node = new GameObject("ProjectileCollision"); node.transform.SetParent(root.transform, false);
                node.transform.localPosition = new Vector3(skill.ProjectileOffsetX ?? 0, 0, skill.ProjectileOffsetY ?? 0);
                var shape = node.AddComponent<SkillCollisionAuthoring>(); shape.SkillId = skill.Id;
                shape.Radius = skill.ProjectileRadius ?? previewRadius;
                PrefabUtility.SaveAsPrefabAsset(root, Root + "/" + skill.Id + "_" + skill.Name + ".prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>读取技能目录内的唯一碰撞组件，拒绝重复 ID，防止多份资产写同一行。</summary>
        /// <returns>已保存的技能碰撞组件列表。</returns>
        /// <exception cref="InvalidOperationException">预制体缺少或有多个组件，或者技能 ID 重复。</exception>
        public static List<SkillCollisionAuthoring> Shapes()
        {
            var result = new List<SkillCollisionAuthoring>(); var ids = new HashSet<int>();
            if (!Directory.Exists(Root)) return result;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { Root }))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var shapes = root.GetComponentsInChildren<SkillCollisionAuthoring>(true);
                if (shapes.Length != 1 || !ids.Add(shapes[0].SkillId)) throw new InvalidOperationException("技能碰撞节点或 ID 不唯一: " + root.name);
                result.Add(shapes[0]);
            }
            return result;
        }

        /// <summary>导出或校验时将世界空间编辑尺寸量化为 TbSkill 的半径与局部偏移千分整数。</summary>
        /// <param name="shape">根节点直接子级的碰撞组件，局部 X/Z 分别为右/前。</param>
        /// <returns>半径、X 偏移、Y 偏移，与 Fields 列顺序一致。</returns>
        /// <exception cref="InvalidOperationException">旋转、缩放、高度、根位置或半径不符合圆形协议。</exception>
        /// <exception cref="OverflowException">数值非有限或超过千分整数范围。</exception>
        public static int[] Values(SkillCollisionAuthoring shape)
        {
            var node = shape.transform; var root = node.parent;
            if (root == null || root.parent != null || root.localPosition != Vector3.zero ||
                root.localScale != Vector3.one || node.localScale != Vector3.one || node.localPosition.y != 0 ||
                Quaternion.Angle(root.localRotation, Quaternion.identity) != 0 || Quaternion.Angle(node.localRotation, Quaternion.identity) != 0)
                throw new InvalidOperationException("技能碰撞节点必须为根直接子节点；根位置、旋转归零，缩放为一，碰撞节点 Y 为零。");
            int radius = ConfigNumber.Encode(shape.Radius);
            if (radius <= 0) throw new InvalidOperationException("技能半径量化后必须大于零。");
            return new[] { radius, ConfigNumber.Encode(node.localPosition.x), ConfigNumber.Encode(node.localPosition.z) };
        }

        /// <summary>菜单将已保存的弹丸技能节点写入源表并生成 Luban，非弹丸与目录条目保持空碰撞配置。</summary>
        /// <remarks>只写 TbSkill 和生成物；生成失败恢复源表；不修改角色、怪物或当前场景。退出重入战斗后使用新快照。</remarks>
        [MenuItem("Roguelike/Combat/技能碰撞配置/导出已保存技能并生成 Luban")]
        public static void Export()
        {
            EnsureEditable();
            var tables = CombatCylinderPipeline.ReadTables();
            var updates = new Dictionary<int, int[]>();
            foreach (var shape in Shapes())
            {
                var skill = tables.TbSkill.Get(shape.SkillId);
                if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile) updates.Add(skill.Id, Values(shape));
            }
            foreach (var skill in tables.TbSkill.DataList.Where(s => s.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile))
                if (!updates.ContainsKey(skill.Id)) throw new InvalidOperationException("缺少弹丸技能预制体: " + skill.Id);
            string path = Directory.GetFiles("Config/Datas/Tables", "*_SkillConfig.xlsx").Single();
            byte[] backup = File.ReadAllBytes(path);
            try
            {
                ConfigWorkbookWriter.Patch(path, Fields, updates);
                var info = new ProcessStartInfo("pwsh", "-NoProfile -File Tools/Config/generate.ps1")
                { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(info);
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit(); File.WriteAllText("Library/SkillCollisionExport.log", stdout.Result + stderr.Result);
                if (process.ExitCode != 0) throw new InvalidOperationException("Luban 生成失败，查看 Library/SkillCollisionExport.log。");
            }
            catch { File.WriteAllBytes(path, backup); throw; }
            AssetDatabase.Refresh(); ValidateSaved();
            Debug.Log("技能碰撞已按三位小数导出为千分整数。重新进入战斗生效。");
        }

        /// <summary>菜单或测试对比保存节点与当前 bytes 的量化值；不修改表或资产。</summary>
        /// <exception cref="InvalidOperationException">技能不存在、节点无效或配置未导出。</exception>
        [MenuItem("Roguelike/Combat/技能碰撞配置/校验保存节点与配置")]
        public static void ValidateSaved()
        {
            var tables = CombatCylinderPipeline.ReadTables(); var found = new HashSet<int>();
            foreach (var shape in Shapes())
            {
                var skill = tables.TbSkill.Get(shape.SkillId); found.Add(skill.Id);
                if (skill.CombatProfileId_Ref?.DeliveryType != ESkillDeliveryType.Projectile) continue;
                var expected = new[] { skill.ProjectileRadiusMilli, skill.ProjectileOffsetXMilli, skill.ProjectileOffsetYMilli };
                var actual = Values(shape);
                for (int i = 0; i < actual.Length; i++)
                    if (!expected[i].HasValue || actual[i] != expected[i].Value) throw new InvalidOperationException("技能尚未导出: " + skill.Id + "/" + Fields[i]);
            }
            if (tables.TbSkill.DataList.Any(s => !found.Contains(s.Id))) throw new InvalidOperationException("部分技能尚未生成预制体。");
        }

        /// <summary>生成或导出前检查编辑状态；拒绝运行模式及未保存的 Prefab Stage，避免读取旧值。</summary>
        /// <exception cref="InvalidOperationException">编辑器正在运行或预制体尚未保存。</exception>
        private static void EnsureEditable()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play Mode。");
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty) throw new InvalidOperationException("请先保存当前技能预制体。");
        }
    }
}
