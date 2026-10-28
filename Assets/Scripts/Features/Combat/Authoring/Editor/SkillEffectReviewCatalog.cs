using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using cfg;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>审核分类字段；整组共享，旧版片段记录仅用于迁移留档，不修改正式技能表。</summary>
    [Serializable]
    public class SkillEffectReviewClassification
    {
        public int Decision;
        // SkillType 保留旧版原始填写内容，新界面通过 SkillTypes 与 Element 保存选择。
        public string SkillType = "";
        public List<string> SkillTypes = new List<string>();
        public string Element = "";
        public bool ClassificationInitialized;
        public string Notes = "";
    }

    /// <summary>源资源片段身份与旧版人工记录；整组审核启用后不再编辑片段上的旧决定。</summary>
    [Serializable]
    public sealed class SkillEffectReviewEntry : SkillEffectReviewClassification
    {
        public int SkillId;
        public int ClipId;
        public string SkillName = "";
        public string AniPath = "";
        public string TexturePath = "";
        public bool Available;
    }

    /// <summary>按 TbSkill.id 保存整组使用决定、类型、元素、备注和审核朝向修正，覆盖该组的全部后缀资源。</summary>
    [Serializable]
    public sealed class SkillEffectReviewGroup : SkillEffectReviewClassification
    {
        public int SkillId;
        public string SkillName = "";
        public string ResourceGroup = "";
        public List<int> ClipIds = new List<int>();
        public bool Available;
        public string MigrationWarning = "";
        public bool FlipProjectileForReview;
        public int ProjectileFacingMode;
        public bool ProjectileFacingInitialized;
    }

    /// <summary>持久化审核文档，版本与状态均须校验，避免损坏文件被静默覆盖。</summary>
    [Serializable]
    public sealed class SkillEffectReviewDocument
    {
        public int Version = 2;
        public List<SkillEffectReviewEntry> Entries = new List<SkillEffectReviewEntry>();
        public List<SkillEffectReviewGroup> Groups = new List<SkillEffectReviewGroup>();
    }

    /// <summary>从 TbSkill、TbVisualSet、TbAnimationClip 建立审核目录，保存用户决定与可检索备注。</summary>
    public sealed class SkillEffectReviewCatalog
    {
        public const string DirectoryPath = "docs/skill-effect-review";
        public const string JsonName = "annotations.json";
        public const string MarkdownName = "技能效果备注.md";
        public static readonly string[] Decisions = { "待确认", "使用", "不用" };
        public static readonly string[] FacingModes = { "正式配置", "水平镜像", "原始方向" };
        /// <summary>纯编辑器审核标签；不定义或控制正式技能机制。</summary>
        public static readonly string[] TypeChoices = { "近战斩击", "飞行弹丸", "直线射束", "扇形喷射", "落点爆发", "持续区域",
            "环绕旋转", "连锁跳跃", "治疗恢复", "护盾防御", "增益强化", "控制减益", "召唤生成", "移动范围" };
        /// <summary>纯编辑器人工元素标签；首项为空，禁止按画面颜色推断或自动选择。</summary>
        public static readonly string[] ElementChoices = { "未选择", "物理", "火", "冰", "雷", "风", "土", "毒", "光", "暗", "无属性" };
        public SkillEffectReviewDocument Document { get; private set; }
        public SkillConfig[] Skills { get; private set; }

        /// <summary>打开工具时按正式 Luban 目录刷新组与资源关系；旧版逐片段决定一次性迁移到整组并完整留档。</summary>
        /// <param name="tables">当前生成 bytes 读取的完整表集合。</param>
        /// <param name="directory">审核文档目录；测试可传独立临时目录。</param>
        /// <returns>已校验并与正式资源同步的审核目录。</returns>
        /// <exception cref="InvalidDataException">版本、重复键、状态或 JSON 内容非法。</exception>
        /// <exception cref="IOException">无法读取已有文档。</exception>
        public static SkillEffectReviewCatalog Load(Tables tables, string directory = DirectoryPath)
        {
            string path = Path.Combine(directory, JsonName);
            var document = File.Exists(path)
                ? JsonUtility.FromJson<SkillEffectReviewDocument>(File.ReadAllText(path, Encoding.UTF8))
                : new SkillEffectReviewDocument();
            if (document == null || (document.Version != 1 && document.Version != 2) || document.Entries == null)
                throw new InvalidDataException("技能效果审核文档版本或内容无效；请检查源文件，不会自动覆盖。");
            bool migrateGroups = document.Version == 1;
            if (document.Groups == null)
            {
                if (!migrateGroups) throw new InvalidDataException("整组审核文档缺少 Groups，不能用旧片段记录覆盖决定。");
                document.Groups = new List<SkillEffectReviewGroup>();
            }
            var keys = new HashSet<string>();
            foreach (var entry in document.Entries)
            {
                if (entry == null || entry.Decision < 0 || entry.Decision >= Decisions.Length ||
                    !keys.Add(entry.SkillId + ":" + entry.ClipId))
                    throw new InvalidDataException("审核文档存在空条目、非法状态或重复技能片段。");
                entry.Available = false;
            }
            var catalog = new SkillEffectReviewCatalog { Document = document,
                Skills = tables.TbSkill.DataList.OrderBy(skill => skill.Id).ToArray() };
            foreach (var skill in catalog.Skills)
                foreach (var clip in skill.VisualSetId_Ref.ClipIds_Ref)
                {
                    var entry = catalog.Find(skill.Id, clip.Id);
                    if (entry == null)
                    {
                        entry = new SkillEffectReviewEntry { SkillId = skill.Id, ClipId = clip.Id };
                        document.Entries.Add(entry);
                    }
                    entry.SkillName = skill.Name;
                    entry.AniPath = clip.AniResourceId_Ref.Path;
                    entry.TexturePath = clip.TextureResourceId_Ref.Path;
                    entry.Available = true;
                }
            foreach (var entry in document.Entries) InitializeClassification(entry);
            if (migrateGroups)
            {
                document.Groups.Clear();
                foreach (var entries in document.Entries.GroupBy(entry => entry.SkillId))
                    document.Groups.Add(MigrateGroup(entries.ToArray()));
                document.Version = 2;
            }
            var groupKeys = new HashSet<int>();
            foreach (var group in document.Groups)
            {
                if (group == null || group.Decision < 0 || group.Decision >= Decisions.Length || !groupKeys.Add(group.SkillId))
                    throw new InvalidDataException("整组审核存在空记录、非法状态或重复 Skill ID。");
                group.Available = false;
                InitializeClassification(group);
            }
            foreach (var skill in catalog.Skills)
            {
                var group = catalog.FindGroup(skill.Id);
                if (group == null)
                {
                    group = new SkillEffectReviewGroup { SkillId = skill.Id, ClassificationInitialized = true };
                    document.Groups.Add(group);
                }
                group.SkillName = skill.Name;
                group.Available = true;
                group.ClipIds = new List<int>(skill.VisualSetId_Ref.ClipIds);
                string file = Path.GetFileNameWithoutExtension(skill.VisualSetId_Ref.ClipIds_Ref[0].AniResourceId_Ref.Path);
                var match = Regex.Match(file, @"^skill_\d+");
                group.ResourceGroup = match.Success ? match.Value : skill.Name;
                if (!group.ProjectileFacingInitialized)
                {
                    var flight = skill.VisualSetId_Ref.ClipIds_Ref.FirstOrDefault(clip =>
                        Regex.IsMatch(Path.GetFileNameWithoutExtension(clip.AniResourceId_Ref.Path), @"^skill_\d+_f(?:_|$)"));
                    // 迁移时冻结用户已看到的最终方向，正式 flipX 更新后不可再次叠加镜像。
                    group.ProjectileFacingMode = group.FlipProjectileForReview ? (flight?.FlipX == true ? 2 : 1) : 0;
                    group.ProjectileFacingInitialized = true;
                }
                if (group.ProjectileFacingMode < 0 || group.ProjectileFacingMode >= FacingModes.Length)
                    throw new InvalidDataException("审核飞行朝向选项无效。");
            }
            return catalog;
        }

        /// <summary>旧文档首次迁移时合并同组人工分类；待确认不抵消明确决定，矛盾决定保留原记录并提示重新确认。</summary>
        /// <param name="entries">同一个 Skill ID 的全部历史片段记录。</param>
        /// <returns>包含整组分类与迁移提示的新记录。</returns>
        private static SkillEffectReviewGroup MigrateGroup(SkillEffectReviewEntry[] entries)
        {
            var decisions = entries.Select(entry => entry.Decision).Where(value => value != 0).Distinct().ToArray();
            var elements = entries.Select(entry => entry.Element).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            var notes = entries.Select(entry => entry.Notes).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            var group = new SkillEffectReviewGroup { SkillId = entries[0].SkillId, SkillName = entries[0].SkillName,
                Decision = decisions.Length == 1 ? decisions[0] : 0, SkillTypes = entries.SelectMany(entry => entry.SkillTypes).Distinct().ToList(),
                Element = elements.Length == 1 ? elements[0] : "", ClassificationInitialized = true,
                SkillType = string.Join(" / ", entries.Select(entry => entry.SkillType).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct()),
                Notes = notes.Length <= 1 ? notes.FirstOrDefault() ?? "" : string.Join("\n", entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Notes))
                    .Select(entry => "原片段 " + entry.ClipId + "：" + entry.Notes)) };
            if (decisions.Length > 1) group.MigrationWarning = "旧片段使用决定冲突，整组迁移初始为待确认；原记录已留档。";
            if (elements.Length > 1) group.MigrationWarning += " 旧片段元素不同（" + string.Join(" / ", elements) + "），整组迁移初始留空；原记录已留档。";
            return group;
        }

        /// <summary>旧审核记录首次加载时迁移明确的类型名称或同义词，始终保留原始填写文本。</summary>
        /// <param name="entry">正在恢复的人工审核条目。</param>
        /// <remarks>只执行一次；用户清空选择后重载不会再次从旧文本填回，不改使用状态或备注。</remarks>
        private static void InitializeClassification(SkillEffectReviewClassification entry)
        {
            if (entry.SkillTypes == null) entry.SkillTypes = new List<string>();
            if (entry.Element == null) entry.Element = "";
            if (entry.ClassificationInitialized) return;
            foreach (string part in (entry.SkillType ?? "").Split(new[] { '/', '、', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = part.Trim();
                if (value == "子弹") value = "飞行弹丸";
                else if (string.Equals(value, "buff", StringComparison.OrdinalIgnoreCase)) value = "增益强化";
                if (TypeChoices.Contains(value) && !entry.SkillTypes.Contains(value)) entry.SkillTypes.Add(value);
            }
            entry.ClassificationInitialized = true;
        }

        /// <summary>绘制多选控件时将保存的名称转换为临时位掩码，磁盘格式不依赖选项顺序。</summary>
        /// <param name="entry">当前整组审核或待迁移分类。</param><returns>当前已选类型对应的控件位掩码。</returns>
        public static int GetTypeMask(SkillEffectReviewClassification entry)
        {
            int mask = 0;
            for (int index = 0; index < TypeChoices.Length; index++)
                if (entry.SkillTypes.Contains(TypeChoices[index])) mask |= 1 << index;
            return mask;
        }

        /// <summary>用户完成类型勾选时按名称保存结果，保留旧文本与尚未被本版识别的历史标签。</summary>
        /// <param name="entry">当前整组审核或待迁移分类。</param><param name="mask">多选控件返回的临时位掩码。</param>
        public static void SetTypeMask(SkillEffectReviewClassification entry, int mask)
        {
            entry.SkillTypes = entry.SkillTypes.Where(value => !TypeChoices.Contains(value)).ToList();
            for (int index = 0; index < TypeChoices.Length; index++)
                if ((mask & (1 << index)) != 0) entry.SkillTypes.Add(TypeChoices[index]);
            entry.ClassificationInitialized = true;
        }

        /// <summary>查看资源详情或迁移留档时按技能与片段联合键查找来源，不读取此处旧状态作为当前审核决定。</summary>
        /// <param name="skillId">TbSkill.id。</param><param name="clipId">TbAnimationClip.id。</param>
        /// <returns>审核条目；正式表已移除或尚未收录时为空。</returns>
        public SkillEffectReviewEntry Find(int skillId, int clipId) =>
            Document.Entries.FirstOrDefault(entry => entry.SkillId == skillId && entry.ClipId == clipId);

        /// <summary>绘制卡片、筛选和技能效果匹配时读取整组统一决定，与当前正在查看的后缀无关。</summary>
        /// <param name="skillId">TbSkill.id。</param><returns>整组审核记录；已移除的资源仍可留档，新 ID 不存在时为空。</returns>
        public SkillEffectReviewGroup FindGroup(int skillId) => Document.Groups.FirstOrDefault(group => group.SkillId == skillId);

        /// <summary>搜索或筛选时按整组审核状态匹配，资源路径与 Clip ID 仅作查找入口，保持四个技能一页。</summary>
        /// <param name="search">名称、ID、资源路径、已选类型、元素、旧类型或备注关键字。</param>
        /// <param name="decision">空值为全部，否则为审核状态。</param>
        /// <returns>符合条件且稳定排序的技能数组。</returns>
        public SkillConfig[] Filter(string search, int? decision)
        {
            return Skills.Where(skill =>
            {
                var group = FindGroup(skill.Id);
                string searchable = group.ResourceGroup + " " + group.SkillId + " " + group.SkillName + " " +
                    string.Join(" / ", group.SkillTypes) + " " + group.Element + " " + group.SkillType + " " + group.Notes + " " +
                    string.Join(" ", Document.Entries.Where(entry => entry.Available && entry.SkillId == skill.Id)
                        .Select(entry => entry.ClipId + " " + entry.AniPath));
                return group.Available && (!decision.HasValue || group.Decision == decision.Value) &&
                    (string.IsNullOrWhiteSpace(search) || searchable.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }).ToArray();
        }

        /// <summary>修改后将 JSON 和 Markdown 分别原子替换；JSON 是审核决定的唯一来源。</summary>
        /// <param name="directory">项目文档目录或隔离测试目录。</param>
        /// <remarks>只写审核文档，不写 Assets、表格或运行时配置；Markdown 失败时 JSON 仍可恢复。</remarks>
        /// <exception cref="IOException">磁盘写入失败；调用方应显示未保存状态并允许重试。</exception>
        public void Save(string directory = DirectoryPath)
        {
            Directory.CreateDirectory(directory);
            AtomicWrite(Path.Combine(directory, JsonName), JsonUtility.ToJson(Document, true) + "\n");
            AtomicWrite(Path.Combine(directory, MarkdownName), BuildMarkdown());
        }

        /// <summary>保存审核后生成整组匹配清单和后缀资源映射；选择一组即包含全部片段，不再逐片段审核。</summary>
        /// <returns>含可用技能组、整组记录、资源详情与使用约束的 UTF-8 Markdown。</returns>
        public string BuildMarkdown()
        {
            var text = new StringBuilder("# 技能效果备注\n\n");
            text.Append("由 Unity 菜单 `Roguelike > 技能效果审核 > 打开审核场景` 编辑并自动生成。审核源为同目录 `annotations.json`，请通过工具修改。\n\n");
            text.Append("## 后续技能设计匹配规则\n\n");
            text.Append("- 按 skill_1 这样的技能组统一审核。标记“使用”即包含该组全部后缀（如 _f、_h、_s）；类型、元素与备注同样作用于整组。\n");
            text.Append("- 只从下方“已确认使用”选择，按技能类型、元素与备注匹配，引用资源组名和 Skill ID；具体实现从资源详情取 Clip ID 与 ANI 路径。\n");
            text.Append("- 待确认、标记不用或源表已移除的技能组不可自动选用；没有匹配时先说明缺口。旧版片段记录仅用于迁移留档，不代表当前决定。\n");
            text.Append("- 此文件是人工资源审核依据，不是战斗配置；最终绑定必须进入正式 Luban 表并走功能确认流程。\n\n");
            text.Append("- 飞行朝向支持正式配置、水平镜像、原始方向；后两者指定 f 段最终方向，正式 flipX 同步后不重复翻转。\n\n");
            text.Append("- 角色可边移动边施法：随身施法用途的 s 段跟随施法者，与发射同时开始，不阻挡移动或等待播完；地面爆炸等落点用途的 s 段留在落点，不按后缀一律跟随。\n\n");
            AppendTable(text, "已确认使用", Document.Groups.Where(group => group.Available && group.Decision == 1));
            AppendTable(text, "全部审核记录", Document.Groups);
            text.Append("## 资源详情（技术 ID 对照，后缀不单独审核）\n\n");
            text.Append("| 技能组 | Skill ID | Clip ID | ANI 路径 | 纹理路径 |\n| --- | --- | --- | --- | --- |\n");
            foreach (var entry in Document.Entries.Where(entry => entry.Available).OrderBy(entry => entry.SkillId).ThenBy(entry => entry.ClipId))
                text.Append("| " + Cell(FindGroup(entry.SkillId).ResourceGroup) + " | " + entry.SkillId + " | " + entry.ClipId + " | " +
                    Cell(entry.AniPath) + " | " + Cell(entry.TexturePath) + " |\n");
            text.Append("\n## 飞行朝向修正\n\n| 技能组 | Skill ID | 审核修正 |\n| --- | --- | --- |\n");
            foreach (var group in Document.Groups.Where(group => group.Available && group.ProjectileFacingMode != 0).OrderBy(group => group.SkillId))
                text.Append("| " + Cell(group.ResourceGroup) + " | " + group.SkillId + " | " + FacingModes[group.ProjectileFacingMode] + "（最终朝向） |\n");
            return text.ToString();
        }

        /// <summary>生成整组审核表，每个 Skill ID 只占一行，列出全部片段技术 ID 以供后续实现查找。</summary>
        /// <param name="text">输出缓冲。</param><param name="title">章节标题。</param><param name="entries">待输出记录。</param>
        private static void AppendTable(StringBuilder text, string title, IEnumerable<SkillEffectReviewGroup> entries)
        {
            text.Append("## " + title + "\n\n");
            text.Append("| 技能组 | Skill ID | 技能名称 | 整组状态 | 技能类型 | 元素 | 原填写类型 | 整组备注 | 包含片段 ID | 迁移提示 |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var entry in entries.OrderBy(item => item.SkillId))
                text.Append("| " + Cell(entry.ResourceGroup) + " | " + entry.SkillId + " | " + Cell(entry.SkillName) + " | " +
                    (entry.Available ? Decisions[entry.Decision] : "源表已移除") + " | " + Cell(string.Join(" / ", entry.SkillTypes)) + " | " +
                    Cell(entry.Element) + " | " + Cell(entry.SkillType) + " | " + Cell(entry.Notes) + " | " +
                    string.Join(", ", entry.ClipIds ?? new List<int>()) + " | " + Cell(entry.MigrationWarning) + " |\n");
            text.Append('\n');
        }

        /// <summary>写表格前转义用户输入的分隔符、HTML 与换行，防止破坏文档列。</summary>
        /// <param name="value">任意用户备注。</param><returns>可安全放入 Markdown 单元格的文本。</returns>
        private static string Cell(string value) => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;")
            .Replace(">", "&gt;").Replace("|", "&#124;").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");

        /// <summary>每次文档保存先完成同目录临时文件，再替换目标，避免截断已有决定。</summary>
        /// <param name="path">目标文档路径。</param><param name="contents">完整文档内容。</param>
        /// <remarks>临时文件只包含审核文本；不生成预览资产。</remarks>
        /// <exception cref="IOException">创建或替换文件失败。</exception>
        private static void AtomicWrite(string path, string contents)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
