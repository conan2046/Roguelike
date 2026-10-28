using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>正式关卡验证护环跟随脚点，屏幕 HUD 在相机 LateUpdate 位移时保持贴边且不改 Prefab 布局。</summary>
        /// <returns>等待真实 YooAsset、UI、运行帧和相机截图的协程。</returns>
        /// <remarks>只操作本测试独占 World；相机临时采样后恢复，结束释放入口、原生容器及共享资源。</remarks>
        [UnityTest]
        public IEnumerator FormalGuardUsesFeetAndHudDoesNotLagCamera()
        {
            using var initializer = new YooAssetPackageInitializer();
            yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(initializer);
            yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources = new YooAssetResourceService(initializer, config);
            var definition = CombatRunDefinition.Create(config.Tables, 1);
            int worldCount = World.All.Count;
            var root = new GameObject("Guard feet and camera HUD acceptance");
            var runner = root.AddComponent<CombatRuntimeRunner>();
            try
            {
                yield return Wait(runner.InitializeAsync(definition, resources, new TestInput(), CancellationToken.None));
                World world = FindCombatWorld();
                CombatSession session = runner.Session;
                const int guardSkillId = 20043;
                var skill = config.Tables.TbSkill.Get(guardSkillId);
                session.SynchronizePlayerSkills(new[] { guardSkillId });
                Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 314, false, out int monsterSlot), Is.True);
                CombatUnit monster = session.ReadUnit(monsterSlot);
                monster.Position = session.ReadUnit(0).Position + new float2(2, 0);
                monster.PreviousPosition = monster.Position; monster.MoveSpeed = 0; monster.Cooldown = 1000;
                monster.Target.Health = monster.Target.MaxHealth = 1000000000;
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);
                var visualResources = (CombatVisualResources)typeof(CombatEntityVisuals)
                    .GetField("resources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runner.Visuals);
                int material = visualResources.Read(guardSkillId, skill.AreaClipIds.Single()).MaterialIndex;
                float2 start = session.ReadUnit(0).Position;
                for (int frame = 0; frame < 4; frame++)
                {
                    runner.AdvanceFrame(session.StepSeconds, new CombatInputFrame { Movement = new float2(0, 1) });
                    CombatUnit owner = session.ReadUnit(0);
                    Assert.That(math.length(owner.BodyOffset), Is.GreaterThan(0.1f), "A body offset must exist to distinguish the regression.");
                    using var all = world.EntityManager.GetAllEntities(Allocator.Temp);
                    Entity[] rings = all.ToArray().Where(e => world.EntityManager.HasComponent<MaterialMeshInfo>(e) &&
                        !world.EntityManager.HasComponent<DisableRendering>(e) &&
                        MaterialMeshInfo.StaticIndexToArrayIndex(world.EntityManager.GetComponentData<MaterialMeshInfo>(e).Material) == material).ToArray();
                    Assert.That(rings.Length, Is.GreaterThan(0));
                    foreach (Entity ring in rings)
                    {
                        LocalTransform transform = world.EntityManager.GetComponentData<LocalTransform>(ring);
                        float2 offset = new float2(skill.AreaVisualOffsetXPixels.Value, skill.AreaVisualOffsetYPixels.Value) *
                            definition.CombatRules.WorldUnitsPerPixel * transform.Scale / skill.AreaVisualScale.Value;
                        Assert.That(math.distance(transform.Position.xy, owner.Position + offset), Is.LessThan(1e-5f));
                    }
                }
                Assert.That(math.distance(start, session.ReadUnit(0).Position), Is.GreaterThan(0));
                Assert.That(runner.FormalUiRoot.transform.parent, Is.SameAs(runner.transform));
                Canvas canvas = runner.FormalUiRoot.GetComponent<Canvas>();
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                var image = runner.FormalUiRoot.transform.Find("TopBar").GetComponent<Image>();
                var bar = (RectTransform)image.transform;
                Vector2 anchorMin = bar.anchorMin, anchorMax = bar.anchorMax, anchoredPosition = bar.anchoredPosition, sizeDelta = bar.sizeDelta;
                Camera camera = runner.ViewCamera;
                Vector3 savedCamera = camera.transform.position;
                Canvas.ForceUpdateCanvases();
                Rect pixels = image.GetPixelAdjustedRect();
                Vector2 initial = RectTransformUtility.WorldToScreenPoint(null, bar.TransformPoint(new Vector3(pixels.xMin, pixels.yMax, 0)));
                float maximumDrift = 0;
                try
                {
                    // 模拟相机在 Canvas 本帧排版后移动；父子继承必须当场更新，不等待下一帧排版。
                    for (int sample = 0; sample < 24; sample++)
                    {
                        camera.transform.position = savedCamera + new Vector3(sample * 0.01731f, sample * 0.01097f, 0);
                        Canvas.ForceUpdateCanvases();
                        Rect adjusted = image.GetPixelAdjustedRect();
                        Vector2 current = RectTransformUtility.WorldToScreenPoint(null, bar.TransformPoint(new Vector3(adjusted.xMin, adjusted.yMax, 0)));
                        maximumDrift = Mathf.Max(maximumDrift, Vector2.Distance(initial, current));
                    }
                }
                finally { camera.transform.position = savedCamera; Canvas.ForceUpdateCanvases(); }
                Assert.That(maximumDrift, Is.LessThan(0.05f), "TopBar must not reveal a moving camera gap.");
                Assert.That(bar.anchorMin, Is.EqualTo(anchorMin)); Assert.That(bar.anchorMax, Is.EqualTo(anchorMax));
                Assert.That(bar.anchoredPosition, Is.EqualTo(anchoredPosition)); Assert.That(bar.sizeDelta, Is.EqualTo(sizeDelta));
                runner.AdvanceFrame(0, new CombatInputFrame { PausePressed = true });
                yield return CaptureCamera(camera, "guard-feet-and-stable-hud.png");
                System.IO.Directory.CreateDirectory(".local/foot-ring-hud-fix-20261027");
                System.IO.File.WriteAllText(".local/foot-ring-hud-fix-20261027/edge-drift.txt", "maxDriftPixels=" + maximumDrift);
                runner.Close(); yield return null;
                Assert.That(world.IsCreated, Is.False);
                Assert.That(World.All.Count, Is.EqualTo(worldCount));
            }
            finally { if (root != null) { runner.Close(); UnityEngine.Object.Destroy(root); } }
        }
    }
}
