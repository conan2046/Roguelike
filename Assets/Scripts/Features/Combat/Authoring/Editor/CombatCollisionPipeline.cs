using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using cfg;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>统一迁移、导出和校验单位及技能的预制体碰撞配置。</summary>
    public static class CombatCollisionPipeline
    {
        public const int CurrentDataVersion = 1;

        /// <summary>保存当前 Prefab 后，将全部碰撞像素参数写入源表并只运行一次 Luban。</summary>
        /// <remarks>首次执行会把旧英文节点和世界坐标数值迁移为中文节点及最终像素值；不创建预览资产。</remarks>
        /// <exception cref="InvalidOperationException">Prefab 结构、配置引用、像素值或 Luban 生成失败。</exception>
        [MenuItem("Roguelike/战斗/碰撞配置/导出全部预制体并生成配置")]
        public static void ExportAll()
        {
            SkillCollisionPipeline.EnsureEditable();
            Tables tables = CombatCylinderPipeline.ReadTables();
            MigrateLegacyPrefabs(tables);
            AssetDatabase.Refresh();
            tables = CombatCylinderPipeline.ReadTables();

            string characterPath = Directory.GetFiles("Config/Datas/Tables", "*_CharacterConfig.xlsx").Single();
            string monsterPath = Directory.GetFiles("Config/Datas/Tables", "*_MonsterConfig.xlsx").Single();
            string skillPath = Directory.GetFiles("Config/Datas/Tables", "*_SkillConfig.xlsx").Single();
            string visualPath = Directory.GetFiles("Config/Datas/Tables", "*_VisualSetConfig.xlsx").Single();
            string[] paths = { characterPath, monsterPath, skillPath, visualPath };
            var backups = paths.ToDictionary(path => path, File.ReadAllBytes);
            try
            {
                ExportUnits(tables, characterPath, monsterPath, out Dictionary<int, int[]> visualUpdates);
                ExportSkills(tables, skillPath, visualUpdates);
                ConfigWorkbookWriter.Patch(visualPath, CombatCylinderPipeline.VisualFields, visualUpdates);
                RunLuban();
            }
            catch
            {
                foreach (var pair in backups) File.WriteAllBytes(pair.Key, pair.Value);
                throw;
            }
            AssetDatabase.Refresh();
            CombatCylinderPipeline.ValidateSaved();
            SkillCollisionPipeline.ValidateSaved();
            Debug.Log("碰撞配置导出完成：Prefab 是编辑源，Luban 表已更新；运行时只读取生成配置。");
        }

        /// <summary>命令行自动化入口；行为与 Unity 菜单的一键导出完全一致。</summary>
        /// <remarks>供指定 projectPath 的 Unity BatchMode 验证调用。</remarks>
        public static void ExportAllFromCommandLine() => ExportAll();

        /// <summary>导出角色和怪物的移动阻挡、受击范围，并收集视觉根缩放。</summary>
        /// <param name="tables">迁移前当前 Luban 配置。</param>
        /// <param name="characterPath">角色源表路径。</param>
        /// <param name="monsterPath">怪物源表路径。</param>
        /// <param name="visualUpdates">输出按表现编号索引的根缩放。</param>
        private static void ExportUnits(Tables tables, string characterPath, string monsterPath,
            out Dictionary<int, int[]> visualUpdates)
        {
            visualUpdates = new Dictionary<int, int[]>();
            foreach (IGrouping<bool, CombatCylinderAuthoring> group in CombatCylinderPipeline.Shapes().GroupBy(shape => shape.IsHero))
            {
                var movement = group.ToDictionary(shape => shape.ConfigId,
                    shape => CombatCylinderPipeline.Values(shape, tables).Select(Quantize).ToArray());
                var body = group.ToDictionary(shape => shape.ConfigId, shape =>
                {
                    float[] values = CombatCylinderPipeline.BodyValues(shape, tables);
                    return values[0] > 0f
                        ? values.Select(value => (float?)Quantize(value)).ToArray()
                        : new float?[] { null, null, null };
                });
                string path = group.Key ? characterPath : monsterPath;
                ConfigWorkbookWriter.Patch(path, CombatCylinderPipeline.MovementFields, movement);
                ConfigWorkbookWriter.PatchOptional(path, CombatCylinderPipeline.BodyFields, body);
                var collisionShape = group.ToDictionary(shape => shape.ConfigId, shape => new int?[]
                {
                    shape.GetComponent<CapsuleCollider2D>() != null
                        ? (int)EUnitCollisionShape.VerticalCapsule
                        : (int?)null
                });
                ConfigWorkbookWriter.PatchOptional(path, CombatCylinderPipeline.CollisionShapeFields, collisionShape);
                var hitEffect = group.Select(shape => new { shape.ConfigId, Values = CombatCylinderPipeline.HitEffectValues(shape, tables) })
                    .Where(item => item.Values.Length > 0)
                    .ToDictionary(item => item.ConfigId,
                        item => item.Values.Select(value => (float?)Quantize(value)).ToArray());
                if (hitEffect.Count > 0)
                    ConfigWorkbookWriter.PatchOptional(path, CombatCylinderPipeline.HitEffectFields, hitEffect);
                foreach (CombatCylinderAuthoring shape in group)
                {
                    int visualId = group.Key ? tables.TbCharacter.Get(shape.ConfigId).VisualSetId : tables.TbMonster.Get(shape.ConfigId).VisualSetId;
                    AddVisualScale(visualUpdates, visualId, CombatCylinderPipeline.ScalePermille(shape));
                }
            }
        }

        /// <summary>按技能投递类型导出弹丸或范围半径，并合并表现根缩放。</summary>
        /// <param name="tables">当前 Luban 配置。</param>
        /// <param name="skillPath">技能源表路径。</param>
        /// <param name="visualUpdates">与单位共用的表现缩放更新集合。</param>
        private static void ExportSkills(Tables tables, string skillPath, Dictionary<int, int[]> visualUpdates)
        {
            var projectile = new Dictionary<int, float[]>();
            var area = new Dictionary<int, float[]>();
            var visualPlacement = new Dictionary<int, float?[]>();
            foreach (SkillCollisionAuthoring shape in SkillCollisionPipeline.Shapes())
            {
                SkillConfig skill = tables.TbSkill.Get(shape.SkillId);
                if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile)
                    projectile.Add(skill.Id, SkillCollisionPipeline.Values(shape));
                else if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea)
                    area.Add(skill.Id, SkillCollisionPipeline.AreaValues(shape));
                float?[] placement = SkillCollisionPipeline.VisualPlacementValues(shape);
                if (placement.Any(value => value.HasValue)) visualPlacement.Add(skill.Id, placement);
                AddVisualScale(visualUpdates, skill.VisualSetId, SkillCollisionPipeline.ScalePermille(shape));
            }
            foreach (SkillConfig skill in tables.TbSkill.DataList)
            {
                if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile && !projectile.ContainsKey(skill.Id))
                    throw new InvalidOperationException("缺少弹丸技能预制体: " + skill.Id);
                if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea && !area.ContainsKey(skill.Id))
                    throw new InvalidOperationException("缺少范围技能预制体: " + skill.Id);
            }
            if (projectile.Count > 0) ConfigWorkbookWriter.Patch(skillPath, SkillCollisionPipeline.ProjectileFields, projectile);
            if (area.Count > 0) ConfigWorkbookWriter.Patch(skillPath, SkillCollisionPipeline.AreaFields, area);
            if (visualPlacement.Count > 0)
                ConfigWorkbookWriter.PatchOptional(skillPath, SkillCollisionPipeline.VisualPlacementFields, visualPlacement);
        }

        /// <summary>合并同一表现编号的根缩放，拒绝多个 Prefab 给出互相矛盾的数值。</summary>
        /// <param name="updates">表现表更新集合。</param>
        /// <param name="visualId">TbVisualSet 编号。</param>
        /// <param name="scale">量化后的等比根缩放。</param>
        private static void AddVisualScale(Dictionary<int, int[]> updates, int visualId, int scale)
        {
            if (updates.TryGetValue(visualId, out int[] existing) && existing[0] != scale)
                throw new InvalidOperationException($"表现 {visualId} 被多个预制体配置为不同缩放。");
            updates[visualId] = new[] { scale };
        }

        /// <summary>运行项目固定 Luban 生成脚本，并把完整日志保存在 Library。</summary>
        /// <exception cref="InvalidOperationException">脚本启动或生成返回非零状态。</exception>
        internal static void RunLuban()
        {
            var info = new ProcessStartInfo("pwsh", "-NoProfile -File Tools/Config/generate.ps1")
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Luban 生成脚本。");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            File.WriteAllText("Library/CombatCollisionExport.log", stdout.Result + stderr.Result);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Luban 生成失败，查看 Library/CombatCollisionExport.log。");
        }

        /// <summary>遍历正式碰撞 Prefab，将旧世界坐标字段一次性烘焙成最终像素字段。</summary>
        /// <param name="tables">旧字段数值仍可从中读取的当前 Luban 快照。</param>
        /// <remarks>只改碰撞组件、碰撞节点名称和节点变换；保留所有外观、锚点及用户其他修改。</remarks>
        private static void MigrateLegacyPrefabs(Tables tables)
        {
            MigrateLegacyUnits(tables);
            MigrateLegacySkills(tables);
        }

        /// <summary>迁移角色和怪物旧移动圆柱，并补齐受击判定节点。</summary>
        /// <param name="tables">提供像素比例和旧身体半径的当前配置。</param>
        private static void MigrateLegacyUnits(Tables tables)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { CombatCylinderPipeline.Root + "/Hero", CombatCylinderPipeline.Root + "/Monster" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    CombatCylinderAuthoring shape = root.GetComponentInChildren<CombatCylinderAuthoring>(true);
                    if (shape == null) continue;
                    if (shape.TryGetComponent(out CapsuleCollider2D _))
                    {
                        // Collider2D 是合并后的正式编辑源；不得重新创建旧受击节点或覆盖原生 Offset/Size。
                        continue;
                    }
                    float rootScale = UniformScale(root.transform);
                    float pixelScale = tables.TbCombatRules.Get(shape.CombatRulesId).WorldUnitsPerPixel;
                    if (shape.DataVersion < CurrentDataVersion)
                    {
                        float oldHeight = shape.HeightPixels;
                        Vector3 oldCenter = shape.transform.localPosition;
                        shape.RadiusPixels = Quantize(shape.RadiusPixels * rootScale / pixelScale);
                        shape.HeightPixels = Quantize(oldHeight * rootScale / pixelScale);
                        shape.OffsetXPixels = Quantize(oldCenter.x * rootScale / pixelScale);
                        shape.OffsetYPixels = Quantize(oldCenter.z * rootScale / pixelScale);
                        shape.ElevationPixels = Quantize((oldCenter.y - oldHeight * 0.5f) * rootScale / pixelScale);
                        shape.DataVersion = CurrentDataVersion;
                    }
                    shape.gameObject.name = "移动阻挡范围";
                    shape.transform.localPosition = Vector3.zero;
                    shape.transform.localRotation = Quaternion.identity;
                    shape.transform.localScale = Vector3.one;

                    CombatBodyCollisionAuthoring body = root.GetComponentInChildren<CombatBodyCollisionAuthoring>(true);
                    if (body == null)
                    {
                        var node = new GameObject("受击判定范围");
                        node.transform.SetParent(root.transform, false);
                        body = node.AddComponent<CombatBodyCollisionAuthoring>();
                        float oldRadius = shape.IsHero
                            ? tables.TbCharacter.Get(shape.ConfigId).BodyRadiusPixels ?? 0f
                            : tables.TbMonster.Get(shape.ConfigId).BodyRadiusPixels ?? 0f;
                        body.RadiusPixels = Quantize(oldRadius * rootScale / pixelScale);
                    }
                    body.gameObject.name = "受击判定范围";
                    body.transform.localPosition = Vector3.zero;
                    body.transform.localRotation = Quaternion.identity;
                    body.transform.localScale = Vector3.one;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        /// <summary>迁移技能旧世界坐标圆，按投递类型改为中文节点和最终像素字段。</summary>
        /// <param name="tables">提供像素比例、投递类型和旧范围半径的当前配置。</param>
        private static void MigrateLegacySkills(Tables tables)
        {
            int[] rulesIds = tables.TbPerformanceScenario.DataList.Where(item => item.Kind == EPerformanceKind.Combat)
                .Select(item => item.CombatRulesId).Distinct().ToArray();
            if (rulesIds.Length != 1) throw new InvalidOperationException("迁移技能碰撞需要唯一战斗规则。");
            float pixelScale = tables.TbCombatRules.Get(rulesIds[0]).WorldUnitsPerPixel;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { SkillCollisionPipeline.Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    SkillCollisionAuthoring shape = root.GetComponentInChildren<SkillCollisionAuthoring>(true);
                    if (shape == null) continue;
                    SkillConfig skill = tables.TbSkill.Get(shape.SkillId);
                    bool area = skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea;
                    float rootScale = UniformScale(root.transform);
                    if (shape.DataVersion < CurrentDataVersion)
                    {
                        Vector3 oldCenter = shape.transform.localPosition;
                        shape.Kind = area ? SkillCollisionAuthoring.CollisionKind.TargetArea : SkillCollisionAuthoring.CollisionKind.Projectile;
                        float oldRadius = area ? skill.AreaRadiusPixels ?? shape.RadiusPixels : shape.RadiusPixels;
                        shape.RadiusPixels = Quantize(oldRadius * rootScale / pixelScale);
                        shape.OffsetXPixels = area ? 0f : Quantize(oldCenter.x * rootScale / pixelScale);
                        shape.OffsetYPixels = area ? 0f : Quantize(oldCenter.z * rootScale / pixelScale);
                        shape.DataVersion = CurrentDataVersion;
                    }
                    shape.gameObject.name = area ? "范围技能命中范围" : "弹丸命中范围";
                    shape.transform.localPosition = Vector3.zero;
                    shape.transform.localRotation = Quaternion.identity;
                    shape.transform.localScale = Vector3.one;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        /// <summary>读取 Prefab 根节点等比缩放，供旧视觉几何一次性烘焙。</summary>
        /// <param name="root">Prefab 根变换。</param>
        /// <returns>正的统一缩放。</returns>
        /// <exception cref="InvalidOperationException">缩放不等比或非正数。</exception>
        private static float UniformScale(Transform root)
        {
            Vector3 scale = root.localScale;
            if (!(scale.x > 0f) || Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                throw new InvalidOperationException(root.name + " 的根节点缩放必须为正数且 X/Y/Z 相同。");
            return scale.x;
        }

        /// <summary>把迁移结果量化到三位小数像素，保证重复导出稳定。</summary>
        /// <param name="value">待量化像素值。</param>
        /// <returns>三位小数像素值。</returns>
        private static float Quantize(float value) => (float)Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }
}
