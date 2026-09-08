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
        internal static readonly string[] ProjectileFields = { "projectileRadiusPixelsMilli", "projectileOffsetXPixelsMilli", "projectileOffsetYPixelsMilli" };
        internal static readonly string[] AreaFields = { "areaRadiusPixelsMilli" };
        internal static readonly string[] VisualPlacementFields =
        {
            "projectileVisualOffsetXPixelsMilli", "projectileVisualOffsetYPixelsMilli", "projectileVisualScalePermille",
            "impactVisualOffsetXPixelsMilli", "impactVisualOffsetYPixelsMilli", "impactVisualScalePermille",
            "areaVisualOffsetXPixelsMilli", "areaVisualOffsetYPixelsMilli", "areaVisualScalePermille"
        };
        /// <summary>缩放写入 TbVisualSet 而非 TbSkill；与半径字段分表导出，避免相互覆盖或重复烘焙。</summary>
        public static readonly string[] VisualFields = { "scalePermille" };

        /// <summary>菜单批量创建 TbSkill 目录内缺失的预制体，按保存组件的 ID 识别用户重命名资产。</summary>
        /// <remarks>只创建技能预制体；首帧预览在打开 Prefab Stage 时临时生成；已有资产不覆盖，临时对象在 finally 清理，不保存 Scene。</remarks>
        /// <exception cref="InvalidOperationException">编辑器运行中、规则不唯一或资源无效。</exception>
        [MenuItem("Roguelike/战斗/碰撞配置/生成缺失技能预制体")]
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
            float previewRadiusPixels = ConfigNumber.Decode(ConfigNumber.Encode(mesh.bounds.size.x / (2 * rules.WorldUnitsPerPixel)));
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(mesh);
            var root = new GameObject(skill.Name);
            try
            {
                bool area = skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea;
                var node = new GameObject(area ? "范围技能命中范围" : "弹丸命中范围"); node.transform.SetParent(root.transform, false);
                var shape = node.AddComponent<SkillCollisionAuthoring>(); shape.SkillId = skill.Id;
                shape.Kind = area ? SkillCollisionAuthoring.CollisionKind.TargetArea : SkillCollisionAuthoring.CollisionKind.Projectile;
                shape.RadiusPixels = area ? skill.AreaRadiusPixels ?? previewRadiusPixels : skill.ProjectileRadiusPixels ?? previewRadiusPixels;
                shape.OffsetXPixels = skill.ProjectileOffsetXPixels ?? 0f;
                shape.OffsetYPixels = skill.ProjectileOffsetYPixels ?? 0f;
                shape.DataVersion = CombatCollisionPipeline.CurrentDataVersion;
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

        /// <summary>导出或校验时将像素编辑尺寸量化为 TbSkill 的弹丸半径与偏移千分整数。</summary>
        /// <param name="shape">根节点直接子级的弹丸命中组件。</param>
        /// <returns>半径、横向偏移、前向偏移，与 ProjectileFields 顺序一致。</returns>
        /// <exception cref="InvalidOperationException">旋转、缩放、高度、根位置或半径不符合圆形协议。</exception>
        /// <exception cref="OverflowException">数值非有限或超过千分整数范围。</exception>
        public static int[] Values(SkillCollisionAuthoring shape)
        {
            var node = shape.transform; var root = node.parent;
            if (root == null || root.parent != null || root.localPosition != Vector3.zero ||
                node.localScale != Vector3.one ||
                Quaternion.Angle(root.localRotation, Quaternion.identity) != 0 || Quaternion.Angle(node.localRotation, Quaternion.identity) != 0)
                throw new InvalidOperationException("技能命中范围必须为根节点直接子节点；根位置、旋转归零，范围节点缩放为一。");
            if (shape.Kind != SkillCollisionAuthoring.CollisionKind.Projectile)
                throw new InvalidOperationException("该节点不是弹丸命中范围。");
            CircleCollider2D circle = shape.GetComponent<CircleCollider2D>();
            float scale = SkillCollisionEditor.PixelScale();
            float rootScale = UniformRootScale(root);
            float radiusPixels = circle == null ? shape.RadiusPixels : circle.radius * rootScale / scale;
            Vector2 offsetPixels = circle == null
                ? new Vector2(shape.OffsetXPixels, shape.OffsetYPixels)
                : ((Vector2)node.localPosition + circle.offset) * rootScale / scale;
            if (circle != null && !circle.isTrigger)
                throw new InvalidOperationException("技能 CircleCollider2D 必须启用 Is Trigger。");
            int radius = ConfigNumber.Encode(radiusPixels);
            if (radius <= 0) throw new InvalidOperationException("技能半径量化后必须大于零。");
            return new[] { radius, ConfigNumber.Encode(offsetPixels.x), ConfigNumber.Encode(offsetPixels.y) };
        }

        /// <summary>将范围技能像素半径量化为配置千分整数。</summary>
        /// <param name="shape">范围技能命中组件。</param>
        /// <returns>仅包含范围半径的整数数组。</returns>
        /// <exception cref="InvalidOperationException">节点用途错误或半径非法。</exception>
        public static int[] AreaValues(SkillCollisionAuthoring shape)
        {
            if (shape.Kind != SkillCollisionAuthoring.CollisionKind.TargetArea)
                throw new InvalidOperationException("该节点不是范围技能命中范围。");
            CircleCollider2D circle = shape.GetComponent<CircleCollider2D>();
            if (circle != null && !circle.isTrigger)
                throw new InvalidOperationException("技能 CircleCollider2D 必须启用 Is Trigger。");
            float radiusPixels = circle == null
                ? shape.RadiusPixels
                : circle.radius * UniformRootScale(shape.transform.parent) / SkillCollisionEditor.PixelScale();
            int radius = ConfigNumber.Encode(radiusPixels);
            if (radius <= 0) throw new InvalidOperationException("范围技能半径量化后必须大于零。");
            return new[] { radius };
        }

        /// <summary>读取预制体根节点的等比缩放，量化为 TbVisualSet.scalePermille 千分整数。</summary>
        /// <param name="shape">技能碰撞组件，以其父级根节点的缩放作为表现缩放。</param>
        /// <returns>千分整数缩放；1000 表示原始大小，500 表示缩小一半。</returns>
        /// <exception cref="InvalidOperationException">缺少根节点、缩放非等比或量化后不为正。</exception>
        /// <remarks>与 Values 分离导出，使缩放同时驱动视觉与 TbSkill 判定半径，避免二者脱节。</remarks>
        public static int ScalePermille(SkillCollisionAuthoring shape)
        {
            var root = shape.transform.parent;
            if (root == null) throw new InvalidOperationException("技能碰撞节点缺少根节点。");
            return ConfigNumber.Encode(UniformRootScale(root));
        }

        /// <summary>读取技能 Prefab 根节点的正数等比缩放，供 Collider2D 局部几何换算为最终世界像素。</summary>
        /// <param name="root">技能 Prefab 根变换。</param>
        /// <returns>根节点统一缩放值。</returns>
        /// <exception cref="InvalidOperationException">根节点为空、缩放非正或 XYZ 不等比。</exception>
        internal static float UniformRootScale(Transform root)
        {
            if (root == null) throw new InvalidOperationException("技能碰撞节点缺少根节点。");
            Vector3 scale = root.localScale;
            if (!(scale.x > 0f) || Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                throw new InvalidOperationException("技能预制体根节点缩放必须为正数且 X/Y/Z 相同。");
            return scale.x;
        }

        /// <summary>读取技能根节点下三类表现节点，换算为 TbSkill 的偏移像素与相对缩放。</summary>
        /// <param name="shape">同一技能 Prefab 的碰撞组件。</param>
        /// <returns>与 VisualPlacementFields 顺序一致的九个可空整数。</returns>
        /// <exception cref="InvalidOperationException">节点重复、层级、旋转、Z 偏移或非等比缩放不符合协议。</exception>
        public static int?[] VisualPlacementValues(SkillCollisionAuthoring shape)
        {
            Transform root = shape.transform.parent;
            if (root == null) throw new InvalidOperationException("技能碰撞节点缺少根节点。");
            var result = new int?[VisualPlacementFields.Length];
            var seen = new HashSet<SkillVisualPlacementAuthoring.VisualKind>();
            float pixelScale = SkillCollisionEditor.PixelScale();
            float rootScale = root.localScale.x;
            foreach (SkillVisualPlacementAuthoring placement in root.GetComponentsInChildren<SkillVisualPlacementAuthoring>(true))
            {
                if (!seen.Add(placement.Kind)) throw new InvalidOperationException(root.name + " 的技能表现节点用途重复。");
                Transform node = placement.transform;
                Vector3 scale = node.localScale;
                if (node.parent != root || Mathf.Abs(node.localPosition.z) > 0.0001f ||
                    Quaternion.Angle(node.localRotation, Quaternion.identity) > 0.0001f ||
                    !(scale.x > 0f) || Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                    throw new InvalidOperationException(root.name + " 的技能表现节点必须是根节点直接子级，Z/旋转归零且 Scale 等比为正数。");
                int index = (int)placement.Kind * 3;
                Vector2 authoredOffset = (Vector2)node.localPosition * rootScale / pixelScale;
                // Prefab 中弹丸朝上：运行时 ANI 以局部 X 为前向、局部 Y 为左向，因此只在导出边界换轴；编辑者看到的位置保持不变。
                Vector2 runtimeOffset = placement.Kind == SkillVisualPlacementAuthoring.VisualKind.Projectile
                    ? new Vector2(authoredOffset.y, -authoredOffset.x)
                    : authoredOffset;
                result[index] = ConfigNumber.Encode(runtimeOffset.x);
                result[index + 1] = ConfigNumber.Encode(runtimeOffset.y);
                result[index + 2] = ConfigNumber.Encode(scale.x);
            }
            return result;
        }

        /// <summary>菜单将已保存的弹丸技能节点写入源表并生成 Luban，非弹丸与目录条目保持空碰撞配置。</summary>
        /// <remarks>只写 TbSkill 和生成物；生成失败恢复源表；不修改角色、怪物或当前场景。退出重入战斗后使用新快照。</remarks>
        public static void Export() => CombatCollisionPipeline.ExportAll();

        /// <summary>菜单或测试对比保存节点与当前 bytes 的量化值；不修改表或资产。</summary>
        /// <exception cref="InvalidOperationException">技能不存在、节点无效或配置未导出。</exception>
        [MenuItem("Roguelike/战斗/碰撞配置/校验技能预制体与配置")]
        public static void ValidateSaved()
        {
            var tables = CombatCylinderPipeline.ReadTables(); var found = new HashSet<int>();
            foreach (var shape in Shapes())
            {
                var skill = tables.TbSkill.Get(shape.SkillId); found.Add(skill.Id);
                if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.Projectile)
                {
                    var expected = new[] { skill.ProjectileRadiusPixelsMilli, skill.ProjectileOffsetXPixelsMilli, skill.ProjectileOffsetYPixelsMilli };
                    var actual = Values(shape);
                    for (int i = 0; i < actual.Length; i++)
                        if (!expected[i].HasValue || actual[i] != expected[i].Value) throw new InvalidOperationException("技能尚未导出: " + skill.Id + "/" + ProjectileFields[i]);
                }
                else if (skill.CombatProfileId_Ref?.DeliveryType == ESkillDeliveryType.TargetArea)
                {
                    int[] actual = AreaValues(shape);
                    if (!skill.AreaRadiusPixelsMilli.HasValue || actual[0] != skill.AreaRadiusPixelsMilli.Value)
                        throw new InvalidOperationException("技能尚未导出: " + skill.Id + "/" + AreaFields[0]);
                }
                int?[] placementActual = VisualPlacementValues(shape);
                int?[] placementExpected =
                {
                    skill.ProjectileVisualOffsetXPixelsMilli, skill.ProjectileVisualOffsetYPixelsMilli, skill.ProjectileVisualScalePermille,
                    skill.ImpactVisualOffsetXPixelsMilli, skill.ImpactVisualOffsetYPixelsMilli, skill.ImpactVisualScalePermille,
                    skill.AreaVisualOffsetXPixelsMilli, skill.AreaVisualOffsetYPixelsMilli, skill.AreaVisualScalePermille
                };
                for (int i = 0; i < placementActual.Length; i++)
                    if (placementActual[i] != placementExpected[i])
                        throw new InvalidOperationException("技能尚未导出: " + skill.Id + "/" + VisualPlacementFields[i]);
            }
            if (tables.TbSkill.DataList.Any(s => !found.Contains(s.Id))) throw new InvalidOperationException("部分技能尚未生成预制体。");
        }

        /// <summary>生成或导出前检查编辑状态；拒绝运行模式及未保存的 Prefab Stage，避免读取旧值。</summary>
        /// <exception cref="InvalidOperationException">编辑器正在运行或预制体尚未保存。</exception>
        internal static void EnsureEditable()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play Mode。");
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty) throw new InvalidOperationException("请先保存当前技能预制体。");
        }
    }
}
