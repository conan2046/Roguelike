using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Authoring.Editor;
using Roguelike.Features.Combat.Rendering;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>真实 Luban、ANI 与隔离 Excel 副本验证逐片段层级、材质隔离、缺失拒绝及受保护导出。</summary>
    public sealed class CombatRenderLayerTests
    {
        /// <summary>当前护体环在模型下、落雷命中在模型上；复用同一已审核护环的两种技能仍保留独立材质与队列。</summary>
        /// <remarks>只创建测试拥有的内存材质和网格，using 统一释放，不保存资产。</remarks>
        [Test]
        public void AuraPartsStraddleModelsAndSharedTexturesKeepIndependentMaterials()
        {
            Tables tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, tables.TbTribulationSlice.Get(101).StageId);
            var source = new CombatVisualTests.Files(tables);
            using var assets = CombatVisualResources.LoadAsync(definition, source, CancellationToken.None).GetAwaiter().GetResult();
            var model = assets.Read(definition.Character.VisualSetId, definition.Character.VisualSetId_Ref.StandClipId.Value);
            var move = assets.Read(definition.Character.VisualSetId, definition.Character.VisualSetId_Ref.MoveClipId.Value);
            int modelQueue = assets.Materials[model.MaterialIndex].renderQueue;
            Assert.That(assets.Materials[move.MaterialIndex].renderQueue, Is.EqualTo(modelQueue));
            Assert.That(assets.Materials[model.HitFlashMaterialIndex].renderQueue, Is.EqualTo(modelQueue));

            var strike = assets.Read(20050, tables.TbSkill.Get(20050).AreaClipIds.Single());
            var ground = assets.Read(20043, tables.TbSkill.Get(20043).AreaClipIds.Single());
            var otherGround = assets.Read(20037, tables.TbSkill.Get(20037).AreaClipIds.Single());
            Assert.That(assets.Materials[strike.MaterialIndex].renderQueue, Is.GreaterThan(modelQueue));
            Assert.That(assets.Materials[ground.MaterialIndex].renderQueue, Is.LessThan(modelQueue));
            Assert.That(ground.MaterialIndex, Is.Not.EqualTo(otherGround.MaterialIndex));
            Assert.That(assets.Materials[ground.MaterialIndex].mainTexture,
                Is.SameAs(assets.Materials[otherGround.MaterialIndex].mainTexture));
            Assert.That(assets.Materials[otherGround.MaterialIndex].renderQueue, Is.Not.EqualTo(assets.Materials[ground.MaterialIndex].renderQueue));
            Assert.That(assets.Materials[otherGround.HitFlashMaterialIndex].renderQueue,
                Is.EqualTo(assets.Materials[otherGround.MaterialIndex].renderQueue));
            var flight = assets.Read(20001, tables.TbSkill.Get(20001).ProjectileClipId.Value);
            Assert.That(assets.Materials[flight.MaterialIndex].renderQueue, Is.GreaterThan(modelQueue));
            Assert.That(source.ActiveHandles, Is.Zero);
        }

        /// <summary>删除模型或已使用技能片段绑定后，实际加载必须失败并释放所有临时资源句柄。</summary>
        /// <param name="modelMissing">删除模型绑定时为真，否则删除护体罡环地面片段。</param>
        /// <remarks>只修改本测试独立反序列化的配置列表，不写正式 bytes 或 Excel。</remarks>
        [TestCase(true)]
        [TestCase(false)]
        public void MissingBindingsFailLoadingWithoutFallbackOrLeakedHandles(bool modelMissing)
        {
            Tables tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, tables.TbTribulationSlice.Get(101).StageId);
            definition.Presentation.RenderConfigIds_Ref.RemoveAll(row => modelMissing
                ? row.VisualSetId == definition.Character.VisualSetId
                : row.SkillId == 20043 && row.ClipId == tables.TbSkill.Get(20043).AreaClipIds.Single());
            var source = new CombatVisualTests.Files(tables);
            Assert.Throws<InvalidOperationException>(() => CombatVisualResources.LoadAsync(definition, source, CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(source.ActiveHandles, Is.Zero);
        }

        /// <summary>非法绑定必须在目录构建时拒绝，避免某一技能修改影响其他模型或特效。</summary>
        /// <param name="fault">重复行、重复归属、双方归属、错误片段或反馈队列冲突。</param>
        /// <remarks>故障只注入测试配置快照，不改变项目数据。</remarks>
        [TestCase("duplicate-row")]
        [TestCase("duplicate-owner")]
        [TestCase("both-owners")]
        [TestCase("wrong-clip")]
        [TestCase("feedback-queue")]
        public void InvalidBindingsAreRejected(string fault)
        {
            Tables tables = LoadTables();
            CombatPresentationConfig presentation = tables.TbCombatPresentation.DataList.First();
            CombatRenderConfig model = presentation.RenderConfigIds_Ref.First(row => row.VisualSetId.HasValue);
            SkillConfig skill = tables.TbSkill.Get(20043);
            if (fault == "duplicate-row") presentation.RenderConfigIds_Ref.Add(model);
            else if (fault == "duplicate-owner")
                presentation.RenderConfigIds_Ref.Add(Binding(tables, 900001, model.VisualSetId, null, null, model.RenderQueue));
            else if (fault == "both-owners")
                presentation.RenderConfigIds_Ref.Add(Binding(tables, 900002, model.VisualSetId, skill.Id, null, model.RenderQueue));
            else if (fault == "wrong-clip")
                presentation.RenderConfigIds_Ref.Add(Binding(tables, 900003, null, skill.Id, tables.TbSkill.Get(20001).ProjectileClipId, model.RenderQueue));
            else if (fault == "feedback-queue")
                presentation.RenderConfigIds_Ref.Add(Binding(tables, 900004, null, skill.Id, skill.AreaClipIds.First(), presentation.FeedbackRenderQueue));
            else throw new ArgumentOutOfRangeException(nameof(fault));
            Assert.Throws<InvalidOperationException>(() => new CombatRenderCatalog(presentation));
        }

        /// <summary>层级导出只改变目标队列单元格，源表外部改动和非法队列均被拒绝且不覆盖文件。</summary>
        /// <remarks>只读正式源表，写入并最终删除系统临时 XLSX 副本，不生成预览资产。</remarks>
        [Test]
        public void InspectorExportChangesOneCellAndPreservesExternalEdits()
        {
            Tables tables = LoadTables();
            var presentation = tables.TbCombatPresentation.DataList.First();
            var row = presentation.RenderConfigIds_Ref.First(item => item.VisualSetId.HasValue);
            string source = Directory.GetFiles("Config/Datas/Tables", "*_CombatRenderConfig.xlsx").Single();
            string temporary = Path.Combine(Path.GetTempPath(), "rouge-render-" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.Copy(source, temporary);
            try
            {
                var before = ReadCells(temporary);
                var expected = new Dictionary<int, int> { [row.Id] = row.RenderQueue };
                var update = new Dictionary<int, int> { [row.Id] = row.RenderQueue + 1 };
                CombatRenderInspector.SaveQueues(temporary, expected, update, presentation.FeedbackRenderQueue);
                var after = ReadCells(temporary);
                CollectionAssert.AreEquivalent(before.Keys, after.Keys);
                string[] changed = before.Keys.Where(key => !XNode.DeepEquals(before[key], after[key])).ToArray();
                Assert.That(changed.Length, Is.EqualTo(1));
                Assert.That(changed[0], Does.StartWith("G"));

                ConfigWorkbookWriter.Patch(temporary, new[] { "renderQueue" },
                    new Dictionary<int, int[]> { [row.Id] = new[] { row.RenderQueue + 2 } });
                byte[] external = File.ReadAllBytes(temporary);
                Assert.Throws<InvalidOperationException>(() => CombatRenderInspector.SaveQueues(temporary, expected,
                    new Dictionary<int, int> { [row.Id] = row.RenderQueue + 3 }, presentation.FeedbackRenderQueue));
                CollectionAssert.AreEqual(external, File.ReadAllBytes(temporary));
                Assert.Throws<InvalidOperationException>(() => CombatRenderInspector.SaveQueues(temporary, expected,
                    new Dictionary<int, int> { [row.Id] = presentation.FeedbackRenderQueue }, presentation.FeedbackRenderQueue));
                CollectionAssert.AreEqual(external, File.ReadAllBytes(temporary));
            }
            finally { File.Delete(temporary); }
        }

        /// <summary>从真实生成 bytes 建立本测试独立表对象。</summary>
        /// <returns>完成 Luban 强引用解析的配置快照。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config/Luban", name + ".bytes"))));

        /// <summary>按新表真实二进制字段顺序构造故障行，随后通过当前表解析真实引用。</summary>
        /// <param name="tables">独立测试表。</param><param name="id">故障行编号。</param>
        /// <param name="model">可空模型表现编号。</param><param name="skill">可空技能编号。</param>
        /// <param name="clip">可空片段编号。</param><param name="queue">故障或重复队列。</param>
        /// <returns>只存在于当前测试内存的已解析行。</returns>
        private static CombatRenderConfig Binding(Tables tables, int id, int? model, int? skill, int? clip, int queue)
        {
            var bytes = new ByteBuf(); bytes.WriteInt(id); bytes.WriteString("fault fixture");
            bytes.WriteBool(model.HasValue); if (model.HasValue) bytes.WriteInt(model.Value);
            bytes.WriteBool(skill.HasValue); if (skill.HasValue) bytes.WriteInt(skill.Value);
            bytes.WriteBool(clip.HasValue); if (clip.HasValue) bytes.WriteInt(clip.Value);
            bytes.WriteInt(queue);
            var result = new CombatRenderConfig(new ByteBuf(bytes.CopyData())); result.ResolveRef(tables); return result;
        }

        /// <summary>读取原始单元格 XML，以比较导出是否只修改目标数字并保留其他列内容与样式属性。</summary>
        /// <param name="path">系统临时工作簿。</param><returns>按单元格地址索引的独立 XML 节点。</returns>
        private static Dictionary<string, XElement> ReadCells(string path)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml").Open();
            return XDocument.Load(stream).Descendants(ns + "c").ToDictionary(cell => (string)cell.Attribute("r"), cell => new XElement(cell));
        }
    }
}
