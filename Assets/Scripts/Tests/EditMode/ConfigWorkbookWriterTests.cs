using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Roguelike.Features.Combat.Authoring;
using Roguelike.Features.Combat.Authoring.Editor;
using Roguelike.Features.Combat.Runtime;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>配置工作簿导出器回归测试；只修改系统临时目录中的源表副本。</summary>
    public sealed class ConfigWorkbookWriterTests
    {
        /// <summary>怪物表字段带换行和缩进时，导出器仍应按规范字段名完成写入。</summary>
        [Test]
        public void PatchAcceptsIndentedWorkbookHeaders()
        {
            string source = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "Config",
                "Datas",
                "Tables",
                "g_怪物_MonsterConfig.xlsx"));
            string temporary = Path.Combine(Path.GetTempPath(), $"rougelike-monster-config-{Guid.NewGuid():N}.xlsx");
            File.Copy(source, temporary, true);
            try
            {
                Assert.DoesNotThrow(() => ConfigWorkbookWriter.Patch(
                    temporary,
                    new[]
                    {
                        "moveRadiusPixelsMilli",
                        "moveHeightPixelsMilli",
                        "moveOffsetXPixelsMilli",
                        "moveOffsetYPixelsMilli",
                        "moveElevationPixelsMilli"
                    },
                    new Dictionary<int, int[]>
                    {
                        [10001] = new[] { 50000, 149000, 0, 0, 0 }
                    }));
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        /// <summary>血条根节点保持一倍缩放，像素到场景显示的换算只应用到内部画布。</summary>
        /// <remarks>测试对象只存在于内存并在 finally 销毁，不保存 Scene、Prefab 或预览资产。</remarks>
        [Test]
        public void CombatHealthBarPreviewKeepsRootScaleAndAppliesPixelSize()
        {
            var root = new GameObject("CombatHealthBar", typeof(RectTransform), typeof(CombatHealthBarView));
            var canvasObject = new GameObject("显示画布（自动像素换算勿改）", typeof(RectTransform));
            var fillObject = new GameObject("当前血量", typeof(RectTransform));
            try
            {
                var rootRect = (RectTransform)root.transform;
                var canvasRect = (RectTransform)canvasObject.transform;
                var fillRect = (RectTransform)fillObject.transform;
                canvasRect.SetParent(rootRect, false);
                fillRect.SetParent(canvasRect, false);
                fillRect.anchorMin = Vector2.zero;
                fillRect.anchorMax = Vector2.one;
                fillRect.pivot = new Vector2(0f, 0.5f);

                var view = root.GetComponent<CombatHealthBarView>();
                view.Configure(1, 60f, 7f, canvasRect, fillRect);
                view.ApplyPreview(0.01f);

                Assert.That(rootRect.localScale, Is.EqualTo(Vector3.one));
                Assert.That(rootRect.sizeDelta, Is.EqualTo(new Vector2(60f, 7f)));
                Assert.That(canvasRect.sizeDelta, Is.EqualTo(new Vector2(60f, 7f)));
                Assert.That(canvasRect.localScale, Is.EqualTo(Vector3.one * 0.01f));
                Assert.That(fillRect.anchorMax.x, Is.EqualTo(1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>原生纵向胶囊的 Offset、Size 及节点位移必须统一换算为 Luban 像素字段。</summary>
        /// <remarks>测试对象只存在于内存；不启用 Physics2D 模拟，也不保存 Prefab。</remarks>
        [Test]
        public void CapsuleColliderValuesConvertToUnifiedPixelFields()
        {
            var root = new GameObject("Unit");
            var node = new GameObject("碰撞范围");
            try
            {
                node.transform.SetParent(root.transform, false);
                node.transform.localPosition = new Vector3(0f, 0.305f, 0f);
                var shape = node.AddComponent<CombatCylinderAuthoring>();
                var tables = CombatCylinderPipeline.ReadTables();
                shape.CombatRulesId = tables.TbCombatRules.DataList[0].Id;
                var capsule = node.AddComponent<CapsuleCollider2D>();
                capsule.direction = CapsuleDirection2D.Vertical;
                capsule.isTrigger = true;
                capsule.offset = new Vector2(0.0048460364f, 0.42160296f);
                capsule.size = new Vector2(0.6163043f, 1.4652171f);

                float scale = tables.TbCombatRules.Get(shape.CombatRulesId).WorldUnitsPerPixel;
                float[] movement = CombatCylinderPipeline.Values(shape, tables);
                float[] body = CombatCylinderPipeline.BodyValues(shape, tables);

                Assert.That(movement[0], Is.EqualTo(capsule.size.x * 0.5f / scale).Within(0.0001f));
                Assert.That(movement[1], Is.EqualTo(capsule.size.y / scale).Within(0.0001f));
                Assert.That(movement[2], Is.EqualTo(capsule.offset.x / scale).Within(0.0001f));
                Assert.That(movement[3], Is.EqualTo((capsule.offset.y + 0.305f) / scale).Within(0.0001f));
                Assert.That(movement[4], Is.Zero);
                Assert.That(body, Is.EqualTo(new[] { movement[0], movement[2], movement[3] }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>技能 CircleCollider2D 导出时必须把 Prefab 根缩放同时应用到半径和偏移。</summary>
        /// <remarks>复现根缩放为 0.5 时运行时碰撞偏移放大两倍的问题；对象仅存在于内存，不启用 Physics2D。</remarks>
        [Test]
        public void SkillCircleColliderValuesIncludePrefabRootScale()
        {
            var root = new GameObject("Skill");
            var node = new GameObject("弹丸命中范围");
            try
            {
                root.transform.localScale = Vector3.one * 0.5f;
                node.transform.SetParent(root.transform, false);
                var shape = node.AddComponent<SkillCollisionAuthoring>();
                shape.Kind = SkillCollisionAuthoring.CollisionKind.Projectile;
                var circle = node.AddComponent<CircleCollider2D>();
                circle.isTrigger = true;
                circle.radius = 0.36f;
                circle.offset = new Vector2(0f, 0.5f);

                float pixelScale = CombatCylinderPipeline.ReadTables().TbCombatRules.DataList[0].WorldUnitsPerPixel;
                int[] values = SkillCollisionPipeline.Values(shape);

                Assert.That(cfg.ConfigNumber.Decode(values[0]), Is.EqualTo(0.36f * 0.5f / pixelScale).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[1]), Is.Zero.Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[2]), Is.EqualTo(0.5f * 0.5f / pixelScale).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>弹丸表现节点在 Prefab 中向上、向右拖拽后，导出值必须换算为运行时 ANI 的前向和左向坐标。</summary>
        /// <remarks>验证换轴只发生在导出边界；命中与范围表现仍保持 Prefab XY，保证编辑器所见位置等于运行时位置。</remarks>
        [Test]
        public void SkillVisualPlacementExportsPrefabCoordinatesAsRuntimeCoordinates()
        {
            var root = new GameObject("Skill");
            var shapeNode = new GameObject("弹丸命中范围");
            var projectileNode = new GameObject("弹丸表现");
            var impactNode = new GameObject("命中特效表现");
            var areaNode = new GameObject("范围特效表现");
            try
            {
                root.transform.localScale = Vector3.one * 0.5f;
                shapeNode.transform.SetParent(root.transform, false);
                var shape = shapeNode.AddComponent<SkillCollisionAuthoring>();
                shape.Kind = SkillCollisionAuthoring.CollisionKind.Projectile;

                projectileNode.transform.SetParent(root.transform, false);
                projectileNode.transform.localPosition = new Vector3(0.2f, -0.4f, 0f);
                projectileNode.transform.localScale = Vector3.one * 1.25f;
                var projectile = projectileNode.AddComponent<SkillVisualPlacementAuthoring>();
                projectile.Kind = SkillVisualPlacementAuthoring.VisualKind.Projectile;

                impactNode.transform.SetParent(root.transform, false);
                impactNode.transform.localPosition = new Vector3(0.6f, 0.8f, 0f);
                impactNode.transform.localScale = Vector3.one * 0.75f;
                var impact = impactNode.AddComponent<SkillVisualPlacementAuthoring>();
                impact.Kind = SkillVisualPlacementAuthoring.VisualKind.Impact;

                areaNode.transform.SetParent(root.transform, false);
                areaNode.transform.localPosition = new Vector3(-0.2f, 0.1f, 0f);
                var area = areaNode.AddComponent<SkillVisualPlacementAuthoring>();
                area.Kind = SkillVisualPlacementAuthoring.VisualKind.Area;

                float pixelScale = CombatCylinderPipeline.ReadTables().TbCombatRules.DataList[0].WorldUnitsPerPixel;
                int?[] values = SkillCollisionPipeline.VisualPlacementValues(shape);

                Assert.That(cfg.ConfigNumber.Decode(values[0].Value), Is.EqualTo(-0.4f * 0.5f / pixelScale).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[1].Value), Is.EqualTo(-0.2f * 0.5f / pixelScale).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[2].Value), Is.EqualTo(1.25f).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[3].Value), Is.EqualTo(0.6f * 0.5f / pixelScale).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[4].Value), Is.EqualTo(0.8f * 0.5f / pixelScale).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[5].Value), Is.EqualTo(0.75f).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[6].Value), Is.EqualTo(-0.2f * 0.5f / pixelScale).Within(0.001f));
                Assert.That(cfg.ConfigNumber.Decode(values[7].Value), Is.EqualTo(0.1f * 0.5f / pixelScale).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

    }
}
