using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using cfg;
using Luban;
using NUnit.Framework;
using ProjectX.Migration;
using Roguelike.Animation;
using Roguelike.Animation.Rendering;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>对实际 Luban 动画引用做格式回归，阻止将模块头误读为帧数的旧错误。</summary>
    public sealed class AniResourceTests
    {
        /// <summary>审计全部配对边界并严格验证当前战斗角色引用；非本轮启用资源异常明确记入独立清单。</summary>
        /// <remarks>源资源与生成表只读；审计清单写入 outputs/combat-config，不用于证明角色已经在运行时显示。</remarks>
        [Test]
        public void AuditAtlasCorpusAndValidateActiveCombatClips()
        {
            var tables = LoadTables();
            int clips = 0, frames = 0, multiple = 0;
            var issues = new List<string>();
            var active = new HashSet<int>(tables.TbCharacter.Get(10001).VisualSetId_Ref.ClipIds
                .Concat(tables.TbMonster.Get(10001).VisualSetId_Ref.ClipIds));
            foreach (var clip in tables.TbAnimationClip.DataList)
            {
                var data = CocosAniData.Parse(File.ReadAllBytes(ResourcePath(clip.AniResourceId_Ref.Path)));
                var png = File.ReadAllBytes(ResourcePath(clip.TextureResourceId_Ref.Path));
                int width = ReadPngInteger(png, 16), height = ReadPngInteger(png, 20);
                try { AniFrameLayout.ValidateAtlas(data, width, height); }
                catch (InvalidOperationException exception)
                {
                    string issue = $"clip={clip.Id} resource={clip.AniResourceId_Ref.Path}: {exception.Message}";
                    issues.Add(issue);
                    Assert.That(active.Contains(clip.Id), Is.False, issue);
                    continue;
                }
                for (int action = 0; action < data.actions.Length; action++)
                    for (int frame = 0; frame < data.actions[action].frames.Length; frame++)
                    {
                        var layout = AniFrameLayout.Build(data, action, frame, width, height);
                        Assert.That(layout.Length, Is.GreaterThan(0), $"clip={clip.Id} action={action} frame={frame}");
                        if (layout.Length > 1) multiple++;
                        frames++;
                    }
                clips++;
                if (clip.Id == tables.TbCharacter.Get(10001).VisualSetId_Ref.StandClipId ||
                    clip.Id == tables.TbCharacter.Get(10001).VisualSetId_Ref.MoveClipId ||
                    clip.Id == tables.TbMonster.Get(10001).VisualSetId_Ref.StandClipId ||
                    clip.Id == tables.TbMonster.Get(10001).VisualSetId_Ref.MoveClipId)
                    Debug.Log($"ANI_COMBAT_CLIP id={clip.Id} action={clip.Action} modules={data.modules.Length} frames={data.frames.Length} " +
                        $"actions={data.actions.Length} actionLengths={string.Join(",", data.actions.Select(x => x.frames.Length))} " +
                        $"durationUnits={string.Join(",", data.actions.SelectMany(x => x.durations).Distinct())} " +
                        $"firstModule={data.modules[0].x},{data.modules[0].y},{data.modules[0].width},{data.modules[0].height}");
            }
            string output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "outputs", "combat-config", "ani-resource-audit.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllLines(output, issues, new System.Text.UTF8Encoding(false));
            Debug.Log($"ANI_RESOURCE_AUDIT validClips={clips} invalidClips={issues.Count} actionFrames={frames} multiModuleFrames={multiple} activeCombatClipsValid=True");
        }

        /// <summary>固定 B_0_fd 主角待机夹具验证模块头与小端宽高。</summary>
        [Test]
        public void CharacterHeaderIsModuleCountNotIntFrameCount()
        {
            var clip = LoadTables().TbAnimationClip.Get(10002);
            Assert.That(clip.Action, Is.EqualTo(EAnimationAction.Stand));
            var bytes = File.ReadAllBytes(ResourcePath(clip.AniResourceId_Ref.Path));
            var data = CocosAniData.Parse(bytes);
            Assert.That(data.modules.Length, Is.EqualTo(bytes[0]));
            Assert.That(data.modules[0].x, Is.Zero);
            Assert.That(data.modules[0].y, Is.Zero);
            Assert.That(data.modules[0].width, Is.EqualTo(75));
            Assert.That(data.modules[0].height, Is.EqualTo(154));
            for (int length = 0; length < bytes.Length; length++)
            {
                var truncated = new byte[length];
                Array.Copy(bytes, truncated, length);
                Assert.That(() => CocosAniData.Parse(truncated), Throws.Exception, $"Truncated length={length}");
            }
            var extra = new byte[bytes.Length + 1];
            Array.Copy(bytes, extra, bytes.Length);
            Assert.Throws<InvalidOperationException>(() => CocosAniData.Parse(extra));
        }

        /// <summary>模块偏移与翻转不修改原始纹理，同时支持同帧多模块。</summary>
        [Test]
        public void LayoutPreservesOffsetsAndBothFlipFlags()
        {
            var data = new CocosAniData
            {
                modules = new[] { new CocosAniData.Module { x = 10, y = 20, width = 30, height = 40 } },
                frames = new[] { new CocosAniData.Frame { modules = new[] {
                    new CocosAniData.FrameModule { x = -15, y = -40, moduleId = 0, flags = 0 },
                    new CocosAniData.FrameModule { x = -15, y = -40, moduleId = 0, flags = 3 } } } },
                actions = new[] { new CocosAniData.Action { frames = new byte[] { 0 }, durations = new byte[] { 2 } } }
            };
            AniFrameLayout.ValidateAtlas(data, 100, 100);
            var quads = AniFrameLayout.Build(data, 0, 0, 100, 100);
            Assert.That(quads.Length, Is.EqualTo(2));
            Assert.That(quads[0].CenterX, Is.Zero);
            Assert.That(quads[0].CenterY, Is.EqualTo(20));
            Assert.That(quads[0].U, Is.EqualTo(0.1f).Within(1e-6));
            Assert.That(quads[0].V, Is.EqualTo(0.4f).Within(1e-6));
            Assert.That(quads[1].UWidth, Is.EqualTo(-0.3f).Within(1e-6));
            Assert.That(quads[1].VHeight, Is.EqualTo(-0.4f).Within(1e-6));
            Assert.That(quads[1].U, Is.EqualTo(0.4f).Within(1e-6));
            Assert.That(quads[1].V, Is.EqualTo(0.8f).Within(1e-6));
            Assert.Throws<InvalidOperationException>(() => AniFrameLayout.ValidateAtlas(data, 10, 10));
        }

        /// <summary>按真实角色资源建立世界单位网格，验证不是旧压测的正方形裁切。</summary>
        /// <remarks>创建测试网格并在 finally 中销毁，不向源资产写入网格。</remarks>
        [Test]
        public void CharacterFrameMeshUsesConfiguredPixelScale()
        {
            var tables = LoadTables();
            var visual = tables.TbCharacter.Get(10001).VisualSetId_Ref;
            var clip = visual.StandClipId_Ref;
            var data = CocosAniData.Parse(File.ReadAllBytes(ResourcePath(clip.AniResourceId_Ref.Path)));
            var png = File.ReadAllBytes(ResourcePath(clip.TextureResourceId_Ref.Path));
            var quads = AniFrameLayout.Build(data, 0, 0, ReadPngInteger(png, 16), ReadPngInteger(png, 20));
            float scale = tables.TbCombatRules.Get(1).WorldUnitsPerPixel * visual.ScalePermille / 1000f;
            var mesh = AniMeshFactory.CreateFrame(quads, scale);
            try
            {
                Assert.That(mesh.vertexCount, Is.EqualTo(quads.Length * 4));
                Assert.That(mesh.bounds.size.x, Is.EqualTo(quads.Max(q => q.CenterX + q.Width / 2) * scale - quads.Min(q => q.CenterX - q.Width / 2) * scale).Within(1e-5));
                Assert.That(mesh.bounds.size.y, Is.EqualTo(quads.Max(q => q.CenterY + q.Height / 2) * scale - quads.Min(q => q.CenterY - q.Height / 2) * scale).Within(1e-5));
                Assert.That(mesh.isReadable, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        /// <summary>读取测试配置生成物并解析引用。</summary>
        /// <returns>真实配置集合。</returns>
        /// <remarks>只读本工作树数据。</remarks>
        /// <exception cref="IOException">生成数据不可读取。</exception>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(
            Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));

        /// <summary>测试只按 TbResource.path 定位本地实物，不推测动画文件名。</summary>
        /// <param name="path">表内资源路径。</param>
        /// <returns>按资源服务的 StreamingAssets 相对路径约定定位的实物路径。</returns>
        private static string ResourcePath(string path) => Path.Combine(Application.streamingAssetsPath, path);

        /// <summary>读取 PNG IHDR 的大端尺寸；此字节序只用于 PNG，不用于 ANI。</summary>
        /// <param name="bytes">PNG 字节。</param>
        /// <param name="offset">IHDR 字段偏移。</param>
        /// <returns>PNG 整数尺寸。</returns>
        private static int ReadPngInteger(byte[] bytes, int offset) =>
            bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
    }
}
