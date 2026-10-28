using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
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

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>正式关卡入口使用真实 YooAsset、UI、相机和 ECS 验收边走边施法；死亡、重开与退出不残留随身层。</summary>
        /// <returns>等待真实加载、相机 GPU 读回与退出清理的协程。</returns>
        /// <remarks>只在隔离 World 控制目标与时钟；截图写入 .local，临时 RenderTexture/Texture2D 不保存为资产。</remarks>
        [UnityTest]
        public IEnumerator FormalStageMovesAndRendersCasterEffectWithoutWindup()
        {
            using var initializer = new YooAssetPackageInitializer();
            yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(initializer);
            yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources = new YooAssetResourceService(initializer, config);
            CombatRunDefinition definition = CombatRunDefinition.Create(config.Tables, 1);
            int worldCount = World.All.Count;
            var root = new GameObject("Formal caster effect acceptance");
            var runner = root.AddComponent<CombatRuntimeRunner>();
            try
            {
                yield return Wait(runner.InitializeAsync(definition, resources, new TestInput(), CancellationToken.None));
                Assert.That(runner.Ready, Is.True);
                World world = FindCombatWorld();
                CombatSession session = runner.Session;
                session.Paused = true;
                session.SynchronizePlayerSkills(new[] { 20044 });
                CombatWeaponState weapon = Enumerable.Range(0, session.PlayerWeaponCount).Select(session.ReadPlayerWeapon).Single(w => w.SkillId == 20044);
                Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 4321, false, out int monsterSlot), Is.True);
                CombatUnit monster = session.ReadUnit(monsterSlot);
                monster.Position = session.ReadUnit(0).Position + new float2(weapon.Range * 0.6f, 0);
                monster.PreviousPosition = monster.Position; monster.MoveSpeed = 0; monster.Cooldown = 1000;
                monster.Target.Health = monster.Target.MaxHealth = 1000000000;
                world.EntityManager.SetComponentData(session.UnitEntity(monsterSlot), monster);
                float2 before = session.ReadUnit(0).Position;
                session.Paused = false;
                runner.AdvanceFrame(session.StepSeconds, new CombatInputFrame { Movement = new float2(0, 1) });
                Assert.That(session.CastCount, Is.EqualTo(1));
                Assert.That(runner.Visuals.VisibleCastCount, Is.EqualTo(1));
                Assert.That(session.Statistics.ProjectileSpawns, Is.EqualTo(weapon.ProjectileCount));
                Assert.That(math.distance(before, session.ReadUnit(0).Position), Is.GreaterThan(0), "Casting must allow movement on the release tick.");
                session.SynchronizePlayerSkills(Array.Empty<int>());
                for (int i = 0; i < 5; i++) runner.AdvanceFrame(session.StepSeconds, new CombatInputFrame { Movement = new float2(0, 1) });
                Assert.That(runner.Visuals.VisibleCastCount, Is.EqualTo(1));
                runner.AdvanceFrame(0, new CombatInputFrame { PausePressed = true });
                Assert.That(runner.Coordinator.Paused, Is.True);
                yield return CaptureCasterDifference(runner, world);
                CombatUnit caster = session.ReadUnit(0); caster.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(0), caster);
                runner.Visuals.Synchronize();
                Assert.That(runner.Visuals.VisibleCastCount, Is.Zero, "Death must hide the caster layer immediately.");
                session.Restart(); runner.Visuals.Synchronize();
                Assert.That(session.CastCount, Is.Zero);
                Assert.That(runner.Visuals.VisibleCastCount, Is.Zero);
                Assert.That(runner.Visuals.VisibleProjectileCount, Is.Zero);
                runner.Close(); yield return null;
                Assert.That(world.IsCreated, Is.False);
                Assert.That(World.All.Count, Is.EqualTo(worldCount));
            }
            finally
            {
                if (root != null) { runner.Close(); UnityEngine.Object.Destroy(root); }
            }
        }

        /// <summary>冻结同一模拟帧，对真实相机分别开启和隐藏随身层，验证像素差来自该层并保存对比证据。</summary>
        /// <param name="runner">正式关卡运行器。</param><param name="world">运行器拥有的战斗 World。</param>
        /// <returns>等待渲染资源就绪与 GPU 读回的协程。</returns>
        /// <remarks>仅临时改变新增的施法池实体可见标记，finally 恢复；不改变模拟、配置或 Scene/Prefab。</remarks>
        private static IEnumerator CaptureCasterDifference(CombatRuntimeRunner runner, World world)
        {
            var target = new RenderTexture(960, 540, 24);
            var pixels = new Texture2D(960, 540, TextureFormat.RGB24, false);
            RenderTexture previous = runner.ViewCamera.targetTexture, active = RenderTexture.active;
            Entity[] casterEntities;
            // 本测试只激活万象星羽；施法池实体没有 CombatUnit/CombatProjectile 组件。
            using (var all = world.EntityManager.GetAllEntities(Allocator.Temp))
                casterEntities = all.ToArray().Where(e => world.EntityManager.HasComponent<MaterialMeshInfo>(e) &&
                    world.EntityManager.HasComponent<LocalTransform>(e) && !world.EntityManager.HasComponent<CombatUnit>(e) &&
                    !world.EntityManager.HasComponent<CombatProjectile>(e) && !world.EntityManager.HasComponent<DisableRendering>(e)).ToArray();
            Assert.That(casterEntities.Length, Is.EqualTo(1));
            try
            {
                target.Create(); runner.ViewCamera.targetTexture = target;
                string directory = ".local/cast-effect-runtime-20261027"; Directory.CreateDirectory(directory);
                int changed = 0; double deadline = Time.realtimeSinceStartupAsDouble + 15;
                do
                {
                    yield return null;
                    world.Update();
                    runner.ViewCamera.Render(); RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    Color32[] withCast = pixels.GetPixels32();
                    byte[] withBytes = pixels.EncodeToPNG();
                    foreach (Entity e in casterEntities) world.EntityManager.AddComponent<DisableRendering>(e);
                    world.Update(); runner.ViewCamera.Render();
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    Color32[] withoutCast = pixels.GetPixels32(); changed = 0;
                    for (int i = 0; i < withCast.Length; i++)
                        if (Math.Abs(withCast[i].r - withoutCast[i].r) + Math.Abs(withCast[i].g - withoutCast[i].g) + Math.Abs(withCast[i].b - withoutCast[i].b) > 15) changed++;
                    foreach (Entity e in casterEntities) world.EntityManager.RemoveComponent<DisableRendering>(e);
                    world.Update();
                    if (changed > 30)
                    {
                        File.WriteAllBytes(Path.Combine(directory, "formal-cast-visible.png"), withBytes);
                        File.WriteAllBytes(Path.Combine(directory, "formal-cast-hidden.png"), pixels.EncodeToPNG());
                        File.WriteAllText(Path.Combine(directory, "camera-difference.txt"), "changedPixels=" + changed);
                    }
                } while (changed <= 30 && Time.realtimeSinceStartupAsDouble < deadline);
                Assert.That(changed, Is.GreaterThan(30), "Caster layer must contribute visible pixels to the actual formal-stage camera.");
            }
            finally
            {
                foreach (Entity e in casterEntities)
                    if (world.IsCreated && world.EntityManager.Exists(e) && world.EntityManager.HasComponent<DisableRendering>(e)) world.EntityManager.RemoveComponent<DisableRendering>(e);
                runner.ViewCamera.targetTexture = previous; RenderTexture.active = active;
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(pixels);
            }
        }
    }
}
