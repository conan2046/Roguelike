using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Roguelike.Features.Combat.Authoring.Editor;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>审核工具的独立文档、片段身份、真实资源解析和资源释放验证，不进入正式战斗。</summary>
    public sealed class SkillEffectReviewTests
    {
        private string directory;

        /// <summary>NUnit 在每个用例前建立独立目录，避免测试覆盖用户正式审核记录。</summary>
        /// <remarks>只创建系统临时目录；TearDown 对称移除。</remarks>
        [SetUp]
        public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "rouge-effect-review-" + Guid.NewGuid().ToString("N"));

        /// <summary>NUnit 在每个用例结束后清理本用例独占目录，不触及项目审核文档。</summary>
        /// <remarks>目录使用随机唯一名，仅删除本测试创建的数据。</remarks>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        /// <summary>验证每组正式技能有一个统一待确认记录，并包含该组的全部片段身份。</summary>
        [Test]
        public void FreshCatalogIncludesEveryClipWithoutApprovingAny()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            Assert.That(catalog.Skills.Length, Is.EqualTo(tables.TbSkill.DataList.Count));
            Assert.That(catalog.Document.Entries.Count, Is.EqualTo(tables.TbSkill.DataList.Sum(skill => skill.VisualSetId_Ref.ClipIds.Count)));
            Assert.That(catalog.Document.Groups.Count, Is.EqualTo(catalog.Skills.Length));
            Assert.That(catalog.Document.Groups.All(entry => entry.Decision == 0 && entry.Available && string.IsNullOrEmpty(entry.SkillType)), Is.True);
            foreach (var skill in catalog.Skills)
                Assert.That(catalog.FindGroup(skill.Id).ClipIds, Is.EqualTo(skill.VisualSetId_Ref.ClipIds));
        }

        /// <summary>验证中文、多行、分隔符、片段选择与审核状态经两次原子保存和重载仍保持。</summary>
        [Test]
        public void DecisionsRoundTripAndMarkdownEscapesNotes()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var entry = catalog.Document.Groups.First();
            entry.Decision = 1; entry.SkillType = "冰系范围"; entry.Notes = "起手|命中\n仅<测试>&备注";
            catalog.Save(directory); catalog.Save(directory);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory).FindGroup(entry.SkillId);
            Assert.That(loaded.Decision, Is.EqualTo(1)); Assert.That(loaded.SkillType, Is.EqualTo(entry.SkillType));
            Assert.That(loaded.Notes, Is.EqualTo(entry.Notes));
            string markdown = File.ReadAllText(Path.Combine(directory, SkillEffectReviewCatalog.MarkdownName));
            Assert.That(markdown, Does.Contain("起手&#124;命中<br>仅&lt;测试&gt;&amp;备注"));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }

        /// <summary>验证认可一组时可用清单包含全部后缀 ID，筛选按整组决定，搜索能定位备注。</summary>
        [Test]
        public void ApprovedSectionAndFilterUseWholeGroupDecision()
        {
            var catalog = SkillEffectReviewCatalog.Load(CombatCylinderPipeline.ReadTables(), directory);
            var entry = catalog.Document.Groups.First(); entry.Decision = 1; entry.SkillType = "测试独有类型";
            string approved = catalog.BuildMarkdown().Split(new[] { "## 全部审核记录" }, StringSplitOptions.None)[0];
            Assert.That(approved, Does.Contain("| " + entry.ResourceGroup + " | " + entry.SkillId + " |"));
            foreach (int clipId in entry.ClipIds) Assert.That(approved, Does.Contain(clipId.ToString()));
            Assert.That(catalog.Filter("测试独有类型", 1).Select(skill => skill.Id), Is.EqualTo(new[] { entry.SkillId }));
            Assert.That(catalog.Filter("测试独有类型", 2), Is.Empty);
        }

        /// <summary>验证重复片段或未来版本的审核文件明确失败，阻止自动覆盖来源不明的数据。</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void InvalidDocumentIsRejected(bool duplicate)
        {
            Directory.CreateDirectory(directory);
            var document = new SkillEffectReviewDocument();
            if (duplicate)
            {
                document.Entries.Add(new SkillEffectReviewEntry { SkillId = 1, ClipId = 2 });
                document.Entries.Add(new SkillEffectReviewEntry { SkillId = 1, ClipId = 2 });
            }
            else document.Version = 999;
            File.WriteAllText(Path.Combine(directory, SkillEffectReviewCatalog.JsonName), JsonUtility.ToJson(document));
            Assert.Throws<InvalidDataException>(() => SkillEffectReviewCatalog.Load(CombatCylinderPipeline.ReadTables(), directory));
        }

        /// <summary>验证已经失去正式表来源的人工记录仍保留，但不可进入可用效果清单。</summary>
        [Test]
        public void RemovedSourceKeepsNotesButCannotBeMatched()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            catalog.Document.Groups.Add(new SkillEffectReviewGroup { SkillId = -1, Decision = 1, Notes = "保留旧备注" });
            catalog.Save(directory);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory);
            Assert.That(loaded.FindGroup(-1).Available, Is.False);
            Assert.That(loaded.FindGroup(-1).Notes, Is.EqualTo("保留旧备注"));
            string approved = loaded.BuildMarkdown().Split(new[] { "## 全部审核记录" }, StringSplitOptions.None)[0];
            Assert.That(approved, Does.Not.Contain("保留旧备注"));
        }

        /// <summary>验证旧版子弹与 buff 明确迁移到常规选项，原始填写、备注和使用状态保持。</summary>
        [TestCase("子弹", "飞行弹丸")]
        [TestCase("buff", "增益强化")]
        public void LegacyClassificationMigratesWithoutLosingUserInput(string original, string expected)
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var entry = catalog.Document.Entries.First();
            entry.SkillType = original; entry.ClassificationInitialized = false;
            entry.Decision = 1; entry.Notes = "原备注保留";
            WriteLegacyDocument(catalog);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory).FindGroup(entry.SkillId);
            Assert.That(loaded.SkillTypes, Is.EqualTo(new[] { expected }));
            Assert.That(loaded.SkillType, Is.EqualTo(original));
            Assert.That(loaded.Notes, Is.EqualTo(entry.Notes)); Assert.That(loaded.Decision, Is.EqualTo(1));
        }

        /// <summary>验证整组多选类型与元素保存重载、检索和备注导出保持一致。</summary>
        [Test]
        public void SelectedTypesAndElementRoundTripAndRemainSearchable()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var entry = catalog.Document.Groups.First();
            int mask = (1 << Array.IndexOf(SkillEffectReviewCatalog.TypeChoices, "落点爆发")) |
                (1 << Array.IndexOf(SkillEffectReviewCatalog.TypeChoices, "控制减益"));
            SkillEffectReviewCatalog.SetTypeMask(entry, mask); entry.Element = "雷"; entry.Decision = 1;
            catalog.Save(directory);
            var loadedCatalog = SkillEffectReviewCatalog.Load(tables, directory);
            var loaded = loadedCatalog.FindGroup(entry.SkillId);
            Assert.That(SkillEffectReviewCatalog.GetTypeMask(loaded), Is.EqualTo(mask));
            Assert.That(loaded.SkillTypes, Is.EquivalentTo(new[] { "落点爆发", "控制减益" }));
            Assert.That(loaded.Element, Is.EqualTo("雷"));
            Assert.That(loadedCatalog.Filter("控制减益", 1).Select(skill => skill.Id), Does.Contain(entry.SkillId));
            Assert.That(loadedCatalog.Filter("雷", 1).Select(skill => skill.Id), Does.Contain(entry.SkillId));
            Assert.That(loadedCatalog.BuildMarkdown(), Does.Contain("落点爆发 / 控制减益 | 雷 |"));
        }

        /// <summary>验证用户取消全部类型后不会因保留的旧类型在下次打开时重新勾选。</summary>
        [Test]
        public void ClearingMigratedTypesPersistsAcrossReload()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var entry = catalog.Document.Groups.First(); entry.SkillType = "子弹";
            SkillEffectReviewCatalog.SetTypeMask(entry, 0); catalog.Save(directory);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory).FindGroup(entry.SkillId);
            Assert.That(loaded.SkillTypes, Is.Empty); Assert.That(loaded.SkillType, Is.EqualTo("子弹"));
        }

        /// <summary>验证旧版仅标记飞行片段时迁移为整组使用，备注保留、全部后缀纳入，并保存原始片段记录。</summary>
        [Test]
        public void LegacyFragmentApprovalBecomesWholeGroupAndPreservesArchive()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var skill = catalog.Skills.First(item => item.VisualSetId_Ref.ClipIds.Count > 1);
            var entry = catalog.Find(skill.Id, skill.VisualSetId_Ref.ClipIds[0]);
            entry.Decision = 1; entry.Notes = "整组半月斩"; entry.Element = "冰";
            WriteLegacyDocument(catalog);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory);
            Assert.That(loaded.Document.Version, Is.EqualTo(2));
            Assert.That(loaded.FindGroup(skill.Id).Decision, Is.EqualTo(1));
            Assert.That(loaded.FindGroup(skill.Id).Notes, Is.EqualTo(entry.Notes));
            Assert.That(loaded.FindGroup(skill.Id).Element, Is.EqualTo("冰"));
            Assert.That(loaded.FindGroup(skill.Id).ClipIds, Is.EqualTo(skill.VisualSetId_Ref.ClipIds));
            Assert.That(loaded.Find(skill.Id, skill.VisualSetId_Ref.ClipIds[1]).Decision, Is.Zero);
            loaded.Save(directory);
            loaded = SkillEffectReviewCatalog.Load(tables, directory);
            Assert.That(loaded.FindGroup(skill.Id).Decision, Is.EqualTo(1));
        }

        /// <summary>验证旧片段互相矛盾时不擅自选用整组，所有原决定均保留供追溯。</summary>
        [Test]
        public void ConflictingLegacyDecisionsRequireGroupConfirmation()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var skill = catalog.Skills.First(item => item.VisualSetId_Ref.ClipIds.Count > 1);
            catalog.Find(skill.Id, skill.VisualSetId_Ref.ClipIds[0]).Decision = 1;
            catalog.Find(skill.Id, skill.VisualSetId_Ref.ClipIds[1]).Decision = 2;
            WriteLegacyDocument(catalog);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory);
            Assert.That(loaded.FindGroup(skill.Id).Decision, Is.Zero);
            Assert.That(loaded.FindGroup(skill.Id).MigrationWarning, Does.Contain("冲突"));
            Assert.That(loaded.Find(skill.Id, skill.VisualSetId_Ref.ClipIds[0]).Decision, Is.EqualTo(1));
            Assert.That(loaded.Find(skill.Id, skill.VisualSetId_Ref.ClipIds[1]).Decision, Is.EqualTo(2));
            Assert.That(loaded.Filter(skill.Id.ToString(), 1), Is.Empty);
        }

        /// <summary>验证移动范围标签、元素和飞行方向修正随整组记录保存，且能够导出供后续正式接入查阅。</summary>
        [Test]
        public void MovingAreaAndFlightCorrectionRoundTripWithGroup()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var catalog = SkillEffectReviewCatalog.Load(tables, directory);
            var group = catalog.FindGroup(20032);
            Assert.That(SkillEffectReviewCatalog.TypeChoices, Does.Contain("移动范围"));
            SkillEffectReviewCatalog.SetTypeMask(group, 1 << Array.IndexOf(SkillEffectReviewCatalog.TypeChoices, "移动范围"));
            group.Element = "风"; group.FlipProjectileForReview = true; group.ProjectileFacingMode = 1; group.Decision = 1;
            catalog.Save(directory);
            var loaded = SkillEffectReviewCatalog.Load(tables, directory);
            Assert.That(loaded.FindGroup(20032).SkillTypes, Does.Contain("移动范围"));
            Assert.That(loaded.FindGroup(20032).FlipProjectileForReview, Is.True);
            Assert.That(loaded.FindGroup(20032).ProjectileFacingMode, Is.EqualTo(1));
            Assert.That(loaded.Filter("移动范围", 1).Select(skill => skill.Id), Does.Contain(20032));
            Assert.That(loaded.BuildMarkdown(), Does.Contain("| skill_32 | 20032 | 水平镜像（最终朝向） |"));
        }

        /// <summary>测试迁移时写入只有旧片段记录的版本一 JSON，模拟用户升级前的真实文件。</summary>
        /// <param name="catalog">临时目录中的测试审核目录。</param>
        /// <remarks>只写本测试独占临时文件，不生成尚不存在的整组 Markdown。</remarks>
        private void WriteLegacyDocument(SkillEffectReviewCatalog catalog)
        {
            catalog.Document.Version = 1; catalog.Document.Groups.Clear();
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, SkillEffectReviewCatalog.JsonName), JsonUtility.ToJson(catalog.Document, true));
        }

        /// <summary>验证全部正式技能片段都能按共享解析器播放、循环归零且 Dispose 后不残留内存纹理。</summary>
        [Test]
        public void EveryFormalSkillClipLoadsAndReleasesMemory()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var presentation = tables.TbPerformanceScenario.DataList.First(item => item.Kind == cfg.EPerformanceKind.Combat).PresentationId_Ref;
            int before = Resources.FindObjectsOfTypeAll<Texture2D>().Count(item => item.name == "SkillReview_MemoryTexture");
            foreach (var clip in tables.TbSkill.DataList.SelectMany(skill => skill.VisualSetId_Ref.ClipIds_Ref).GroupBy(item => item.Id).Select(group => group.First()))
                using (var player = new SkillEffectReviewPlayback())
                {
                    player.Load(clip, presentation, 0, default(UnityEngine.SceneManagement.Scene), 0);
                    Assert.That(player.Error, Is.Null, "Clip " + clip.Id);
                    Assert.That(player.FrameCount, Is.GreaterThan(0));
                    player.Sample(player.Duration); Assert.That(player.FrameIndex, Is.Zero);
                }
            Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Count(item => item.name == "SkillReview_MemoryTexture"), Is.EqualTo(before));
        }
    }
}
