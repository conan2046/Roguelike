using System.IO;
using System.Threading;
using NUnit.Framework;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Rendering;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace Roguelike.Tests
{
    /// <summary>用真实播放器输出的网格和配置材质执行 GPU 绘制，保存四方向前摇/F0/F4 对照图。</summary>
    public sealed class CombatVisualCaptureTests
    {
        /// <summary>编辑器图形测试绘制四列方向、三行阶段；不打开或保存任何 Scene/Prefab。</summary>
        /// <remarks>测试预览相机及纹理在 finally 释放；数值仅用于测试布局，不进入正式玩法。按实际 RT 尺寸读回，兼容编辑器 DPI 缩放。</remarks>
        [Test]
        public void CaptureConfiguredMonsterTimeline()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null), "Run this visual test without -nographics.");
            var tables = CombatVisualTests.LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4);
            var source = new CombatVisualTests.Files(tables);
            using var world = new World("Combat GPU timeline");
            using var resources = CombatVisualResources.LoadAsync(scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            using var session = CombatSessionFactory.CreateAsync(world, scenario, source, CancellationToken.None).GetAwaiter().GetResult();
            var visual = scenario.MonsterIds_Ref[0].VisualSetId_Ref;
            var unit = session.ReadUnit(1);
            var pixels = new Texture2D(1200, 900, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            var preview = new PreviewRenderUtility();
            try
            {
                var config = scenario.PresentationId_Ref;
                preview.camera.orthographic = true; preview.camera.orthographicSize = 4.5f;
                preview.camera.transform.position = new Vector3(6, -4.5f, -10);
                preview.camera.transform.rotation = Quaternion.identity;
                preview.camera.nearClipPlane = 0.1f; preview.camera.farClipPlane = 50;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(config.BackgroundR, config.BackgroundG, config.BackgroundB);
                preview.BeginPreview(new Rect(0, 0, pixels.width, pixels.height), GUIStyle.none);
                for (int column = 0; column < config.AttackDirectionIds_Ref.Count; column++)
                {
                    var mapping = config.AttackDirectionIds_Ref[column]; var facing = mapping.FacingDirectionId_Ref;
                    unit.AttackOption = new Roguelike.Features.Combat.CombatAttackOption { ActionIndex = mapping.AttackActionIndex,
                        AlignedActionIndex = facing.ActionIndex, FlipX = facing.FlipX };
                    unit.Facing = new Unity.Mathematics.float2(facing.X, facing.Y);
                    unit.AttackActive = true; unit.PendingAttack.Sequence++;
                    var player = new CombatVisualPlayer(visual, resources);
                    var attack = resources.Read(visual.Id, visual.AttackClipId.Value).Animation;
                    for (int row = 0; row < 3; row++)
                    {
                        unit.AttackAge = row == 0 ? 0 : unit.WindupSeconds + (row == 2 ? attack.FrameStartSeconds(mapping.AttackActionIndex, mapping.HitFrameIndex) : 0);
                        player.Sample(unit, (ulong)(row + 1), session.StepSeconds);
                        Assert.That(player.FlipX, Is.EqualTo(facing.FlipX));
                        Assert.That(player.ActionIndex, Is.EqualTo(row == 0 ? facing.ActionIndex : mapping.AttackActionIndex));
                        var matrix = Matrix4x4.TRS(new Vector3(column * 3 + 1.5f, -row * 3 - 2f, 0), Quaternion.identity, Vector3.one);
                        preview.DrawMesh(resources.Meshes[player.MeshIndex], matrix, resources.Materials[player.MaterialIndex], 0);
                    }
                }
                preview.Render(true);
                var rendered = (RenderTexture)preview.EndPreview();
                RenderTexture.active = rendered;
                pixels.Reinitialize(rendered.width, rendered.height);
                pixels.ReadPixels(new Rect(0, 0, pixels.width, pixels.height), 0, 0); pixels.Apply();
                var colors = pixels.GetPixels32(); int bright = 0;
                var background = colors[0];
                foreach (var color in colors) if (System.Math.Abs(color.r-background.r) + System.Math.Abs(color.g-background.g) + System.Math.Abs(color.b-background.b) > 40) bright++;
                Assert.That(bright, Is.GreaterThan(5000), "GPU output must contain the configured art, not a blank frame.");
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 4; col++)
                    {
                        int changed = 0;
                        for (int y = row * pixels.height / 3; y < (row + 1) * pixels.height / 3; y++)
                            for (int x = col * pixels.width / 4; x < (col + 1) * pixels.width / 4; x++)
                            {
                                var color = colors[y * pixels.width + x];
                                if (System.Math.Abs(color.r-background.r) + System.Math.Abs(color.g-background.g) + System.Math.Abs(color.b-background.b) > 40) changed++;
                            }
                        Assert.That(changed, Is.GreaterThan(300), $"Empty direction/stage cell {col},{row}");
                    }
                string directory = Path.GetFullPath("outputs/combat-config/visual-review"); Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "monster-windup-f0-f4.png"), pixels.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous; preview.Cleanup(); Object.DestroyImmediate(pixels);
            }
        }
    }
}
