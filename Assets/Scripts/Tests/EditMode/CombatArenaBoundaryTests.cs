using System.IO;
using System.Linq;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>通用边界显示直接消费正式移动边界和 Luban 样式，不借用 Tilemap 范围。</summary>
    public sealed class CombatArenaBoundaryTests
    {
        /// <summary>普通与试炼的显示矩形等于移动系统设置，开关和视口变化不会改变几何或规则。</summary>
        /// <param name="stageId">正式表中的待验证关卡。</param>
        /// <remarks>所有显示对象仅在测试内存中创建并在 finally 释放，不写场景或存档。</remarks>
        [TestCase(1),TestCase(101),TestCase(102)]
        public void BoundaryUsesMovementBoundsAndPreservesGeometryAcrossVisibility(int stageId)
        {
            var tables=Load();var definition=CombatRunDefinition.Create(tables,stageId);
            var settings=CombatSessionSettings.FromRun(definition);var root=new GameObject("Boundary test owner");
            var cameraObject=new GameObject("Boundary test camera");var camera=cameraObject.AddComponent<Camera>();
            camera.orthographic=true;camera.orthographicSize=definition.Map.CameraViewportHeightPixels*definition.CombatRules.WorldUnitsPerPixel*.5f;
            try
            {
                using var visual=new CombatArenaBoundaryVisuals(settings.Arena,definition.Presentation,root.transform);
                Assert.That(visual.Visible,Is.EqualTo(definition.Presentation.ArenaBoundaryDefaultVisible));
                var line=visual.Root.GetComponent<LineRenderer>();Assert.That(line.useWorldSpace,Is.True);Assert.That(line.positionCount,Is.EqualTo(4));
                Assert.That(line.GetPosition(0),Is.EqualTo(new Vector3(-settings.Arena.x,-settings.Arena.y,0)));
                Assert.That(line.GetPosition(2),Is.EqualTo(new Vector3(settings.Arena.x,settings.Arena.y,0)));
                var before=Enumerable.Range(0,4).Select(line.GetPosition).ToArray();
                Assert.That(settings.Arena.x*2,Is.Not.EqualTo(definition.MapLayout.FitViewport(camera.aspect).WidthPixels*definition.CombatRules.WorldUnitsPerPixel));
                visual.SetVisible(false);Assert.That(visual.Root.activeSelf,Is.False);
                camera.aspect=1;visual.Synchronize(camera);visual.SetVisible(true);visual.Synchronize(camera);
                Assert.That(Enumerable.Range(0,4).Select(line.GetPosition),Is.EqualTo(before));
                Assert.That(line.startWidth,Is.EqualTo(camera.orthographicSize*2*definition.Presentation.ArenaBoundaryWidthPixels/camera.pixelHeight).Within(.00001f));
            }
            finally { Object.DestroyImmediate(root);Object.DestroyImmediate(cameraObject); }
        }

        /// <summary>战斗测试场景允许覆盖地图边界，显示器须以 FromScenario 与实际模拟同源的数据绘制。</summary>
        [Test]
        public void CombatScenarioBoundsComeFromItsMovementSettings()
        {
            var tables=Load();var scenario=tables.TbPerformanceScenario.Get(4);var settings=CombatSessionSettings.FromScenario(scenario);
            using var visual=new CombatArenaBoundaryVisuals(settings.Arena,settings.Presentation,null);
            Assert.That(visual.Root.GetComponent<LineRenderer>().GetPosition(2),Is.EqualTo(new Vector3(settings.Arena.x,settings.Arena.y,0)));
        }

        /// <summary>每项用例读取项目正式 Luban bytes，不复制业务默认参数。</summary><returns>当前配置表。</returns>
        private static Tables Load()=>new Tables(n=>new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,"Config/Luban",n+".bytes"))));
    }
}
