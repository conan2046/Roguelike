using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>对 Prefab 拥有的 Excel 字段执行 Dry Run、冲突检测和导出基线提交。</summary>
    internal sealed class AuthoringExportTransaction
    {
        internal const string DefaultManifestPath = "Config/Authoring/combat-prefab-ownership.json";
        internal const string DefaultReportPath = "Library/CombatAuthoringDryRun.log";
        private const int ManifestVersion = 1;

        private readonly string scope;
        private readonly bool dryRun;
        private readonly string manifestPath;
        private readonly string reportPath;
        private readonly Dictionary<string, ManifestEntry> baseline;
        private readonly Dictionary<string, ManifestEntry> proposed = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        private readonly List<string> changes = new List<string>();
        private readonly List<string> conflicts = new List<string>();
        private int observedCells;
        private bool completed;

        /// <summary>读取现有字段基线并开始一次只读预览或正式写入事务。</summary>
        /// <param name="scope">写入报告的导出范围名称。</param>
        /// <param name="dryRun">为真时只检查和报告，不允许写工作簿。</param>
        /// <param name="manifestPath">字段所有权基线文件；测试可传入临时路径。</param>
        /// <param name="reportPath">Dry Run 报告路径；测试可传入临时路径。</param>
        /// <exception cref="InvalidOperationException">清单版本、内容或键重复时抛出。</exception>
        internal AuthoringExportTransaction(string scope, bool dryRun,
            string manifestPath = DefaultManifestPath, string reportPath = DefaultReportPath)
        {
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("导出范围不能为空。", nameof(scope));
            this.scope = scope;
            this.dryRun = dryRun;
            this.manifestPath = manifestPath ?? throw new ArgumentNullException(nameof(manifestPath));
            this.reportPath = reportPath ?? throw new ArgumentNullException(nameof(reportPath));
            baseline = LoadManifest(manifestPath);
        }

        /// <summary>指示当前事务是否禁止工作簿写入。</summary>
        internal bool IsDryRun => dryRun;

        /// <summary>登记一组 Prefab 拥有字段，对比当前 Excel、上次导出基线和本次 Prefab 值。</summary>
        /// <param name="path">业务 Excel 路径。</param>
        /// <param name="fields">Prefab 拥有的字段名。</param>
        /// <param name="updates">按 ID 索引的规范化可空数值文本。</param>
        /// <remarks>正式事务发现冲突时立即拒绝后续写入；Dry Run 会收集全部冲突后由 Complete 汇总。</remarks>
        /// <exception cref="InvalidOperationException">同一字段出现矛盾提案，或正式事务检测到 Excel 冲突。</exception>
        internal void Observe(string path, string[] fields, Dictionary<int, string[]> updates)
        {
            Dictionary<int, string[]> current = ConfigWorkbookWriter.ReadValues(path, fields, updates.Keys);
            string workbook = RelativeWorkbook(path);
            int conflictCountBefore = conflicts.Count;
            foreach (KeyValuePair<int, string[]> pair in updates.OrderBy(item => item.Key))
            {
                string[] oldValues = current[pair.Key];
                string[] newValues = pair.Value;
                for (int index = 0; index < fields.Length; index++)
                {
                    observedCells++;
                    string key = Key(workbook, pair.Key, fields[index]);
                    var entry = new ManifestEntry
                    {
                        workbook = workbook,
                        id = pair.Key,
                        field = fields[index],
                        hasValue = newValues[index] != null,
                        value = newValues[index] ?? string.Empty
                    };
                    if (proposed.TryGetValue(key, out ManifestEntry existing) &&
                        !SameValue(existing, entry))
                        throw new InvalidOperationException($"同一导出字段收到矛盾值：{workbook}/{pair.Key}/{fields[index]}。");
                    proposed[key] = entry;

                    string oldValue = oldValues[index];
                    string newValue = newValues[index];
                    bool currentMatchesPrefab = SameValue(oldValue, newValue);
                    if (!baseline.TryGetValue(key, out ManifestEntry previous))
                    {
                        if (!currentMatchesPrefab)
                            conflicts.Add(Format("CONFLICT", workbook, pair.Key, fields[index], oldValue, newValue,
                                "未建立基线"));
                    }
                    else if (!SameValue(oldValue, Value(previous)) && !currentMatchesPrefab)
                    {
                        conflicts.Add(Format("CONFLICT", workbook, pair.Key, fields[index], oldValue, newValue,
                            "baseline=" + Display(Value(previous))));
                    }

                    if (!currentMatchesPrefab)
                        changes.Add(Format("CHANGE", workbook, pair.Key, fields[index], oldValue, newValue, null));
                }
            }

            if (!dryRun && conflicts.Count > conflictCountBefore)
            {
                WriteReport();
                throw new InvalidOperationException($"检测到 Excel 人工修改冲突，已拒绝写入。查看 {reportPath}。");
            }
        }

        /// <summary>写出 Dry Run 报告，并在存在冲突时拒绝继续正式导出。</summary>
        /// <exception cref="InvalidOperationException">当前 Excel 与上次导出基线冲突。</exception>
        internal void Complete()
        {
            WriteReport();
            completed = true;
            if (conflicts.Count > 0)
                throw new InvalidOperationException($"检测到 {conflicts.Count} 个 Excel 人工修改冲突，未写入任何源表。查看 {reportPath}。");
        }

        /// <summary>正式导出和 Luban 校验成功后，原子更新已观察字段的导出基线。</summary>
        /// <remarks>只写 Config/Authoring 清单，不保存 Prefab、Scene 或运行时配置。</remarks>
        /// <exception cref="InvalidOperationException">事务未完成、仍是 Dry Run 或存在冲突。</exception>
        internal void Commit()
        {
            if (!completed || dryRun || conflicts.Count > 0)
                throw new InvalidOperationException("只有已完成且无冲突的正式导出事务可以提交字段基线。");
            foreach (KeyValuePair<string, ManifestEntry> pair in proposed) baseline[pair.Key] = pair.Value;
            SaveManifest(manifestPath, baseline.Values);
        }

        /// <summary>把当前 Excel/Prefab 差异和冲突写入 Library 报告。</summary>
        /// <remarks>报告含表、ID、字段、旧值、新值；不包含绝对本机路径。</remarks>
        private void WriteReport()
        {
            string directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var lines = new List<string>
            {
                $"scope={scope}",
                $"mode={(dryRun ? "dry-run" : "apply")}",
                $"observed={observedCells}",
                $"changes={changes.Count}",
                $"conflicts={conflicts.Count}"
            };
            lines.AddRange(conflicts);
            lines.AddRange(changes);
            if (changes.Count == 0 && conflicts.Count == 0) lines.Add("NO_CHANGE");
            File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
        }

        /// <summary>读取版本化字段基线；文件不存在时返回空清单。</summary>
        /// <param name="path">清单路径。</param>
        /// <returns>按工作簿、ID、字段组合键索引的基线。</returns>
        /// <exception cref="InvalidOperationException">版本不兼容、条目非法或重复。</exception>
        private static Dictionary<string, ManifestEntry> LoadManifest(string path)
        {
            var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            if (!File.Exists(path)) return result;
            ManifestFile file = JsonUtility.FromJson<ManifestFile>(File.ReadAllText(path, Encoding.UTF8));
            if (file == null || file.version != ManifestVersion || file.entries == null)
                throw new InvalidOperationException("Prefab 字段所有权清单版本或结构无效。");
            foreach (ManifestEntry entry in file.entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.workbook) ||
                    string.IsNullOrWhiteSpace(entry.field))
                    throw new InvalidOperationException("Prefab 字段所有权清单包含无效条目。");
                string key = Key(entry.workbook, entry.id, entry.field);
                if (!result.TryAdd(key, entry))
                    throw new InvalidOperationException("Prefab 字段所有权清单包含重复条目：" + key);
            }

            return result;
        }

        /// <summary>使用同目录临时文件原子保存字段基线。</summary>
        /// <param name="path">正式清单路径。</param>
        /// <param name="entries">合并后的全部字段基线。</param>
        private static void SaveManifest(string path, IEnumerable<ManifestEntry> entries)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var file = new ManifestFile
            {
                version = ManifestVersion,
                entries = entries.OrderBy(item => item.workbook, StringComparer.Ordinal)
                    .ThenBy(item => item.id)
                    .ThenBy(item => item.field, StringComparer.Ordinal)
                    .ToList()
            };
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(file, true) + Environment.NewLine,
                new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        /// <summary>把工作簿路径规范为仓库内正斜杠相对路径，避免清单写入本机绝对路径。</summary>
        /// <param name="path">调用方提供的相对或绝对路径。</param>
        /// <returns>相对当前项目根目录的稳定路径。</returns>
        private static string RelativeWorkbook(string path)
        {
            return Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path)).Replace('\\', '/');
        }

        /// <summary>组合稳定字段键。</summary>
        /// <param name="workbook">相对工作簿路径。</param>
        /// <param name="id">配置行 ID。</param>
        /// <param name="field">字段名。</param>
        /// <returns>清单字典键。</returns>
        private static string Key(string workbook, int id, string field) => $"{workbook}|{id}|{field}";

        /// <summary>格式化一条可机器检索、可人工阅读的变更记录。</summary>
        /// <param name="kind">CHANGE 或 CONFLICT。</param>
        /// <param name="workbook">相对工作簿路径。</param>
        /// <param name="id">配置行 ID。</param>
        /// <param name="field">字段名。</param>
        /// <param name="oldValue">Excel 当前值。</param>
        /// <param name="newValue">Prefab 建议值。</param>
        /// <param name="suffix">可选基线说明。</param>
        /// <returns>单行报告文本。</returns>
        private static string Format(string kind, string workbook, int id, string field,
            string oldValue, string newValue, string suffix)
        {
            string result = $"{kind} table={workbook} id={id} field={field} old={Display(oldValue)} new={Display(newValue)}";
            return string.IsNullOrEmpty(suffix) ? result : result + " " + suffix;
        }

        /// <summary>把空单元格显示为统一标记。</summary>
        /// <param name="value">规范化可空数值。</param>
        /// <returns>报告中的显示文本。</returns>
        private static string Display(string value) => value ?? "<empty>";

        /// <summary>读取清单条目的可空值。</summary>
        /// <param name="entry">字段基线条目。</param>
        /// <returns>有值时返回文本，否则返回空。</returns>
        private static string Value(ManifestEntry entry) => entry.hasValue ? entry.value : null;

        /// <summary>比较两条清单值。</summary>
        /// <param name="left">左侧条目。</param>
        /// <param name="right">右侧条目。</param>
        /// <returns>空值状态和文本都相同时返回真。</returns>
        private static bool SameValue(ManifestEntry left, ManifestEntry right) =>
            left.hasValue == right.hasValue && (!left.hasValue || string.Equals(left.value, right.value, StringComparison.Ordinal));

        /// <summary>比较两个规范化可空单元格值。</summary>
        /// <param name="left">Excel 或基线值。</param>
        /// <param name="right">Prefab 建议值。</param>
        /// <returns>空值或规范文本相同时返回真。</returns>
        private static bool SameValue(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

        [Serializable]
        private sealed class ManifestFile
        {
            public int version;
            public List<ManifestEntry> entries;
        }

        [Serializable]
        private sealed class ManifestEntry
        {
            public string workbook;
            public int id;
            public string field;
            public bool hasValue;
            public string value;
        }
    }
}
