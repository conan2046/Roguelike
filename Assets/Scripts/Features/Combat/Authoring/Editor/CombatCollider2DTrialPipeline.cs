using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using cfg;
using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>按测试关卡闭包或全量范围将单位与技能迁移为 Unity Collider2D 编辑载体。</summary>
    public static class CombatCollider2DTrialPipeline
    {
        /// <summary>迁移当前唯一 TbStage 的引用闭包，然后按现有统一导出链生成 Luban。</summary>
        /// <remarks>只修改该关卡实际引用的 Prefab；不扫描或覆盖其他角色、怪物、技能微调。</remarks>
        /// <exception cref="InvalidOperationException">关卡不唯一、Prefab 缺失或碰撞结构不可无损迁移时抛出。</exception>
        [MenuItem("Roguelike/战斗/碰撞配置/迁移测试关卡 Collider2D")]
        public static void MigrateCurrentStage()
        {
            SkillCollisionPipeline.EnsureEditable();
            Tables tables = CombatCylinderPipeline.ReadTables();
            StageConfig stage = tables.TbStage.DataList.Single();
            var monsterIds = stage.StageRuleId_Ref.SpawnPhaseIds_Ref
                .SelectMany(phase => phase.MonsterWeights)
                .Select(weight => weight.MonsterId)
                .Append(stage.StageRuleId_Ref.BossEncounterId_Ref.MonsterId)
                .ToHashSet();
            var skillIds = stage.InitialSkillIds.ToHashSet();
            foreach (UpgradeOptionConfig option in stage.StageRuleId_Ref.UpgradePoolId_Ref.OptionIds_Ref)
                if (option.TargetSkillId.HasValue) skillIds.Add(option.TargetSkillId.Value);
            foreach (int monsterId in monsterIds)
            {
                int? defaultSkillId = tables.TbMonster.Get(monsterId).DefaultSkillId;
                if (defaultSkillId.HasValue)
                    skillIds.Add(defaultSkillId.Value);
            }

            MigrateUnit(stage.InitialCharacterId, true, tables);
            foreach (int monsterId in monsterIds.OrderBy(value => value)) MigrateUnit(monsterId, false, tables);
            int colliderSkillCount = 0;
            foreach (int skillId in skillIds.OrderBy(value => value))
            {
                SkillConfig skill = tables.TbSkill.Get(skillId);
                if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile ||
                    skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea)
                {
                    MigrateSkill(skill, tables);
                    colliderSkillCount++;
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CombatCollisionPipeline.ExportAll();
            Debug.Log($"测试关卡 Collider2D 迁移完成：角色 1，怪物 {monsterIds.Count}，技能 Collider2D {colliderSkillCount}。");
        }

        /// <summary>供指定 projectPath 的 Unity BatchMode 执行同一迁移流程。</summary>
        public static void MigrateCurrentStageFromCommandLine() => MigrateCurrentStage();

        /// <summary>将全部角色、怪物以及所有弹丸和目标位置技能迁移为已验证的 Collider2D 编辑载体并统一导出。</summary>
        /// <remarks>单位命中特效挂点缺少独立配置时从迁移前受击中心初始化；近战及未接入战斗配置的技能不创建无意义碰撞圆。</remarks>
        /// <exception cref="InvalidOperationException">Prefab 缺失、身份重复、旧几何不完整或 Luban 导出失败时抛出。</exception>
        [MenuItem("Roguelike/战斗/碰撞配置/迁移全部角色怪物技能 Collider2D")]
        public static void MigrateAll()
        {
            SkillCollisionPipeline.EnsureEditable();
            Tables tables = CombatCylinderPipeline.ReadTables();
            foreach (CharacterConfig character in tables.TbCharacter.DataList.OrderBy(item => item.Id))
                MigrateUnit(character.Id, true, tables);
            foreach (MonsterConfig monster in tables.TbMonster.DataList.OrderBy(item => item.Id))
                MigrateUnit(monster.Id, false, tables);
            SkillConfig[] colliderSkills = tables.TbSkill.DataList
                .Where(skill => skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile ||
                    skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea)
                .OrderBy(skill => skill.Id).ToArray();
            foreach (SkillConfig skill in colliderSkills) MigrateSkill(skill, tables);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CombatCollisionPipeline.ExportAll();
            Debug.Log($"全量 Collider2D 迁移完成：角色 {tables.TbCharacter.DataList.Count}，怪物 {tables.TbMonster.DataList.Count}，技能 Collider2D {colliderSkills.Length}。");
        }

        /// <summary>供指定 projectPath 的 Unity BatchMode 执行全量迁移与同一导出校验。</summary>
        public static void MigrateAllFromCommandLine() => MigrateAll();

        /// <summary>把一个单位的两个旧范围合并为纵向 CapsuleCollider2D。</summary>
        /// <param name="configId">角色或怪物表 ID。</param>
        /// <param name="hero">是否从角色目录查找。</param>
        /// <param name="tables">用于像素换算和引用校验的 Luban 快照。</param>
        /// <remarks>若 Prefab 已有 CapsuleCollider2D，保留用户调整的 Offset/Size，只烘焙节点位移并合并旧节点。</remarks>
        private static void MigrateUnit(int configId, bool hero, Tables tables)
        {
            string path = FindUnitPrefab(configId, hero);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                CombatCylinderAuthoring source = root.GetComponentsInChildren<CombatCylinderAuthoring>(true).Single();
                CombatBodyCollisionAuthoring body = root.GetComponentInChildren<CombatBodyCollisionAuthoring>(true);
                float[] bodyValues = CombatCylinderPipeline.BodyValues(source, tables);
                CapsuleCollider2D existing = root.GetComponentInChildren<CapsuleCollider2D>(true);
                GameObject target = existing != null ? existing.gameObject : source.gameObject;
                CombatCylinderAuthoring shape = target.GetComponent<CombatCylinderAuthoring>();
                if (shape == null)
                {
                    shape = target.AddComponent<CombatCylinderAuthoring>();
                    CopyIdentity(source, shape);
                }
                CapsuleCollider2D capsule = existing ?? target.AddComponent<CapsuleCollider2D>();
                float scale = tables.TbCombatRules.Get(source.CombatRulesId).WorldUnitsPerPixel;
                if (existing == null)
                {
                    float diameter = source.RadiusPixels * 2f;
                    capsule.size = new Vector2(diameter, Mathf.Max(source.HeightPixels, diameter)) * scale;
                    capsule.offset = new Vector2(source.OffsetXPixels,
                        source.ElevationPixels + source.HeightPixels * 0.5f) * scale;
                }
                else
                {
                    capsule.offset += (Vector2)target.transform.localPosition;
                }
                capsule.direction = CapsuleDirection2D.Vertical;
                capsule.isTrigger = true;
                target.transform.localPosition = Vector3.zero;
                target.transform.localRotation = Quaternion.identity;
                target.transform.localScale = Vector3.one;
                target.name = "碰撞范围";

                float radiusPixels = capsule.size.x * 0.5f / scale;
                Vector2 offsetPixels = capsule.offset / scale;
                shape.RadiusPixels = radiusPixels;
                shape.HeightPixels = capsule.size.y / scale;
                shape.OffsetXPixels = offsetPixels.x;
                shape.OffsetYPixels = offsetPixels.y;
                shape.ElevationPixels = 0f;
                shape.DataVersion = CombatCollisionPipeline.CurrentDataVersion;

                if (body != null) UnityEngine.Object.DestroyImmediate(body);
                if (source != shape)
                {
                    GameObject oldNode = source.gameObject;
                    UnityEngine.Object.DestroyImmediate(source);
                    if (oldNode.GetComponents<Component>().Length == 1) UnityEngine.Object.DestroyImmediate(oldNode);
                }
                foreach (CombatBodyCollisionAuthoring duplicate in root.GetComponentsInChildren<CombatBodyCollisionAuthoring>(true))
                {
                    GameObject oldNode = duplicate.gameObject;
                    UnityEngine.Object.DestroyImmediate(duplicate);
                    if (oldNode != target && oldNode.GetComponents<Component>().Length == 1)
                        UnityEngine.Object.DestroyImmediate(oldNode);
                }
                foreach (Transform child in root.transform.Cast<Transform>().ToArray())
                    if (child.name == "受击判定范围" && child.GetComponents<Component>().Length == 1)
                        UnityEngine.Object.DestroyImmediate(child.gameObject);
                float? configuredHitX = hero ? tables.TbCharacter.Get(configId).HitEffectOffsetXPixels : tables.TbMonster.Get(configId).HitEffectOffsetXPixels;
                float? configuredHitY = hero ? tables.TbCharacter.Get(configId).HitEffectOffsetYPixels : tables.TbMonster.Get(configId).HitEffectOffsetYPixels;
                EnsureHitEffectAnchor(root, configuredHitX ?? bodyValues[1], configuredHitY ?? bodyValues[2], scale);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>为实际接入弹丸或目标位置投递的技能添加 CircleCollider2D 编辑载体。</summary>
        /// <param name="skill">当前 Luban 技能配置。</param>
        /// <param name="tables">提供像素换算规则的 Luban 快照。</param>
        /// <remarks>仅添加编辑器载体；运行时弹丸和范围判定仍由 DOTS 圆形算法执行。</remarks>
        private static void MigrateSkill(SkillConfig skill, Tables tables)
        {
            string path = FindSkillPrefab(skill.Id);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                SkillCollisionAuthoring shape = root.GetComponentsInChildren<SkillCollisionAuthoring>(true).Single();
                CircleCollider2D circle = shape.GetComponent<CircleCollider2D>();
                if (circle == null)
                    circle = shape.gameObject.AddComponent<CircleCollider2D>();
                float scale = tables.TbPerformanceScenario.DataList
                    .Where(item => item.Kind == EPerformanceKind.Combat)
                    .Select(item => item.CombatRulesId_Ref.WorldUnitsPerPixel).Distinct().Single();
                float rootScale = SkillCollisionPipeline.UniformRootScale(root.transform);
                if (!(circle.radius > 0f))
                {
                    circle.radius = shape.RadiusPixels * scale / rootScale;
                    circle.offset = new Vector2(shape.OffsetXPixels, shape.OffsetYPixels) * scale / rootScale;
                }
                circle.offset += (Vector2)shape.transform.localPosition;
                circle.isTrigger = true;
                shape.transform.localPosition = Vector3.zero;
                shape.transform.localRotation = Quaternion.identity;
                shape.transform.localScale = Vector3.one;
                if (skill.ProjectileClipId_Ref != null)
                    EnsureVisualPlacement(root, SkillVisualPlacementAuthoring.VisualKind.Projectile, "弹丸表现",
                        skill.ProjectileVisualOffsetXPixels, skill.ProjectileVisualOffsetYPixels,
                        skill.ProjectileVisualScale, scale);
                if (skill.ImpactClipId_Ref != null)
                    EnsureVisualPlacement(root, SkillVisualPlacementAuthoring.VisualKind.Impact, "命中特效表现",
                        skill.ImpactVisualOffsetXPixels, skill.ImpactVisualOffsetYPixels,
                        skill.ImpactVisualScale, scale);
                if (skill.AreaClipIds_Ref != null && skill.AreaClipIds_Ref.Count > 0)
                    EnsureVisualPlacement(root, SkillVisualPlacementAuthoring.VisualKind.Area, "范围特效表现",
                        skill.AreaVisualOffsetXPixels, skill.AreaVisualOffsetYPixels,
                        skill.AreaVisualScale, scale);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>在指定角色或怪物目录中按组件 ID 查找唯一 Prefab。</summary>
        /// <param name="configId">配置 ID。</param>
        /// <param name="hero">是否查找角色目录。</param>
        /// <returns>唯一 Prefab 资产路径。</returns>
        /// <exception cref="InvalidOperationException">资产缺失或 ID 重复时抛出。</exception>
        private static string FindUnitPrefab(int configId, bool hero)
        {
            string folder = CombatCylinderPipeline.Root + (hero ? "/Hero" : "/Monster");
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                {
                    GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    CombatCylinderAuthoring shape = asset == null ? null : asset.GetComponentInChildren<CombatCylinderAuthoring>(true);
                    return shape != null && shape.ConfigId == configId && shape.IsHero == hero;
                }).ToArray();
            if (paths.Length != 1) throw new InvalidOperationException($"碰撞 Prefab 不唯一：{configId}/{hero}。");
            return paths[0];
        }

        /// <summary>按 SkillCollisionAuthoring.SkillId 查找唯一技能 Prefab。</summary>
        /// <param name="skillId">技能 ID。</param>
        /// <returns>唯一 Prefab 资产路径。</returns>
        /// <exception cref="InvalidOperationException">资产缺失或 ID 重复时抛出。</exception>
        private static string FindSkillPrefab(int skillId)
        {
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { SkillCollisionPipeline.Root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                {
                    GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    SkillCollisionAuthoring shape = asset == null ? null : asset.GetComponentInChildren<SkillCollisionAuthoring>(true);
                    return shape != null && shape.SkillId == skillId;
                }).ToArray();
            if (paths.Length != 1) throw new InvalidOperationException("技能碰撞 Prefab 不唯一：" + skillId + "。");
            return paths[0];
        }

        /// <summary>将旧移动组件的身份字段复制到合并节点。</summary>
        /// <param name="source">旧移动组件。</param>
        /// <param name="destination">合并节点上的新组件。</param>
        private static void CopyIdentity(CombatCylinderAuthoring source, CombatCylinderAuthoring destination)
        {
            destination.ConfigId = source.ConfigId;
            destination.IsHero = source.IsHero;
            destination.CombatRulesId = source.CombatRulesId;
        }

        /// <summary>为单位补齐可拖拽命中特效挂点；已有挂点保留用户位置。</summary>
        /// <param name="root">单位 Prefab 根节点。</param>
        /// <param name="xPixels">TbCharacter/TbMonster 挂点横向像素；缺少独立字段时使用迁移前受击中心。</param>
        /// <param name="yPixels">TbCharacter/TbMonster 挂点纵向像素；缺少独立字段时使用迁移前受击中心。</param>
        /// <param name="pixelScale">逻辑像素到 Unity 场景单位比例。</param>
        /// <exception cref="InvalidOperationException">挂点重复或根缩放非法。</exception>
        private static void EnsureHitEffectAnchor(GameObject root, float xPixels, float yPixels, float pixelScale)
        {
            CombatHitEffectAnchorAuthoring[] existing = root.GetComponentsInChildren<CombatHitEffectAnchorAuthoring>(true);
            if (existing.Length > 1) throw new InvalidOperationException(root.name + " 的命中特效挂点不唯一。");
            bool created = existing.Length == 0;
            GameObject node = created ? new GameObject("命中特效挂点") : existing[0].gameObject;
            if (created)
            {
                node.transform.SetParent(root.transform, false);
                node.AddComponent<CombatHitEffectAnchorAuthoring>();
                float rootScale = UniformScale(root.transform);
                node.transform.localPosition = new Vector3(xPixels, yPixels, 0f) * pixelScale / rootScale;
                node.transform.localRotation = Quaternion.identity;
                node.transform.localScale = Vector3.one;
            }
            node.name = "命中特效挂点";
        }

        /// <summary>为技能补齐一类独立表现编辑节点；已有节点保留用户位置与缩放。</summary>
        /// <param name="root">技能 Prefab 根节点。</param>
        /// <param name="kind">弹丸、命中或范围表现。</param>
        /// <param name="name">中文节点名。</param>
        /// <param name="xPixels">TbSkill 对应表现横向像素。</param>
        /// <param name="yPixels">TbSkill 对应表现纵向像素。</param>
        /// <param name="scale">TbSkill 对应表现相对缩放。</param>
        /// <param name="pixelScale">逻辑像素到 Unity 场景单位比例。</param>
        /// <exception cref="InvalidOperationException">配置缺失、节点重复或根缩放非法。</exception>
        private static void EnsureVisualPlacement(GameObject root, SkillVisualPlacementAuthoring.VisualKind kind,
            string name, float? xPixels, float? yPixels, float? scale, float pixelScale)
        {
            if (!xPixels.HasValue || !yPixels.HasValue || !scale.HasValue || !(scale.Value > 0f))
                throw new InvalidOperationException(root.name + " 缺少" + name + "配置。");
            SkillVisualPlacementAuthoring[] matches = root.GetComponentsInChildren<SkillVisualPlacementAuthoring>(true)
                .Where(item => item.Kind == kind).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException(root.name + " 的" + name + "节点不唯一。");
            bool created = matches.Length == 0;
            GameObject node = created ? new GameObject(name) : matches[0].gameObject;
            SkillVisualPlacementAuthoring placement = created ? node.AddComponent<SkillVisualPlacementAuthoring>() : matches[0];
            placement.Kind = kind;
            if (created)
            {
                node.transform.SetParent(root.transform, false);
                float rootScale = UniformScale(root.transform);
                Vector2 runtimeOffset = new Vector2(xPixels.Value, yPixels.Value);
                Vector2 authoredOffset = kind == SkillVisualPlacementAuthoring.VisualKind.Projectile
                    ? new Vector2(-runtimeOffset.y, runtimeOffset.x)
                    : runtimeOffset;
                node.transform.localPosition = (Vector3)(authoredOffset * pixelScale / rootScale);
                node.transform.localRotation = Quaternion.identity;
                node.transform.localScale = Vector3.one * scale.Value;
            }
            node.name = name;
        }

        /// <summary>读取 Prefab 根节点正的等比缩放，用于编辑节点与最终配置互换。</summary>
        /// <param name="root">单位或技能 Prefab 根变换。</param>
        /// <returns>统一缩放值。</returns>
        /// <exception cref="InvalidOperationException">根缩放非正或不等比。</exception>
        private static float UniformScale(Transform root)
        {
            Vector3 scale = root.localScale;
            if (!(scale.x > 0f) || Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                throw new InvalidOperationException(root.name + " 的根节点缩放必须为正数且 X/Y/Z 相同。");
            return scale.x;
        }
    }
}
