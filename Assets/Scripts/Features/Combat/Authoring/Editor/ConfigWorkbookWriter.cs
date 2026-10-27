using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>编辑器配置导出共用的数值单元格写入器，保留 Excel 中无关样式和 ZIP 内容。</summary>
    public static class ConfigWorkbookWriter
    {
        /// <summary>用户导出时按 ID 和列名更新既有源表，不新增未知 ID 或隐式字段。</summary>
        /// <param name="path">待更新业务 Excel。</param>
        /// <param name="fields">源表内已有的千分整数列。</param>
        /// <param name="updates">按 ID 索引的整数数组，与 fields 顺序一致。</param>
        /// <remarks>直接写文件；调用方必须备份源表并在失败时恢复。文件占用时抛出 IO 异常，不绕过 Excel 锁。</remarks>
        /// <exception cref="InvalidOperationException">源表缺少字段、字段数错误或某个 ID 不存在。</exception>
        public static void Patch(string path, string[] fields, Dictionary<int, int[]> updates)
        {
            PatchValues(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray()));
        }

        /// <summary>在作者事务保护下更新千分整数列；Dry Run 时只读取和报告。</summary>
        /// <param name="path">待检查业务 Excel。</param>
        /// <param name="fields">Prefab 拥有的整数列。</param>
        /// <param name="updates">按 ID 索引的整数数组。</param>
        /// <param name="transaction">字段所有权事务。</param>
        internal static void Patch(string path, string[] fields, Dictionary<int, int[]> updates,
            AuthoringExportTransaction transaction)
        {
            PatchGuarded(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray()), transaction);
        }

        /// <summary>用户导出时按 ID 和列名更新直接像素浮点列。</summary>
        /// <param name="path">待更新业务 Excel。</param>
        /// <param name="fields">源表内已有的 float 列。</param>
        /// <param name="updates">按 ID 索引的浮点数组，与 fields 顺序一致。</param>
        /// <remarks>像素只量化到三位小数，不再写成乘一千整数。</remarks>
        public static void Patch(string path, string[] fields, Dictionary<int, float[]> updates)
        {
            PatchValues(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()));
        }

        /// <summary>在作者事务保护下更新直接像素浮点列；Dry Run 时只读取和报告。</summary>
        /// <param name="path">待检查业务 Excel。</param>
        /// <param name="fields">Prefab 拥有的浮点列。</param>
        /// <param name="updates">按 ID 索引的浮点数组。</param>
        /// <param name="transaction">字段所有权事务。</param>
        internal static void Patch(string path, string[] fields, Dictionary<int, float[]> updates,
            AuthoringExportTransaction transaction)
        {
            PatchGuarded(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()), transaction);
        }

        /// <summary>按 ID 更新可空整数列；空值会删除单元格内容，保持资源目录行没有伪造的零配置。</summary>
        /// <param name="path">待更新业务 Excel。</param>
        /// <param name="fields">源表内已有的可空整数列。</param>
        /// <param name="updates">按 ID 索引的可空整数数组，与 fields 顺序一致。</param>
        /// <remarks>直接写文件；调用方必须备份源表并在失败时恢复。</remarks>
        /// <exception cref="InvalidOperationException">源表缺少字段、字段数错误或某个 ID 不存在。</exception>
        public static void PatchOptional(string path, string[] fields, Dictionary<int, int?[]> updates)
        {
            PatchValues(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value?.ToString(CultureInfo.InvariantCulture)).ToArray()));
        }

        /// <summary>在作者事务保护下更新可空整数列；Dry Run 时只读取和报告。</summary>
        /// <param name="path">待检查业务 Excel。</param>
        /// <param name="fields">Prefab 拥有的可空整数列。</param>
        /// <param name="updates">按 ID 索引的可空整数数组。</param>
        /// <param name="transaction">字段所有权事务。</param>
        internal static void PatchOptional(string path, string[] fields, Dictionary<int, int?[]> updates,
            AuthoringExportTransaction transaction)
        {
            PatchGuarded(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value?.ToString(CultureInfo.InvariantCulture)).ToArray()), transaction);
        }

        /// <summary>按 ID 更新可空直接像素浮点列；空值会删除单元格内容。</summary>
        /// <param name="path">待更新业务 Excel。</param>
        /// <param name="fields">源表内已有的 float? 列。</param>
        /// <param name="updates">按 ID 索引的可空浮点数组，与 fields 顺序一致。</param>
        /// <remarks>像素只量化到三位小数，不再写成乘一千整数。</remarks>
        public static void PatchOptional(string path, string[] fields, Dictionary<int, float?[]> updates)
        {
            PatchValues(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value?.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()));
        }

        /// <summary>在作者事务保护下更新可空直接像素浮点列；Dry Run 时只读取和报告。</summary>
        /// <param name="path">待检查业务 Excel。</param>
        /// <param name="fields">Prefab 拥有的可空浮点列。</param>
        /// <param name="updates">按 ID 索引的可空浮点数组。</param>
        /// <param name="transaction">字段所有权事务。</param>
        internal static void PatchOptional(string path, string[] fields, Dictionary<int, float?[]> updates,
            AuthoringExportTransaction transaction)
        {
            PatchGuarded(path, fields, updates.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(value => value?.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()), transaction);
        }

        /// <summary>先执行字段所有权冲突检查，再按事务模式决定是否写入工作簿。</summary>
        /// <param name="path">待检查业务 Excel。</param>
        /// <param name="fields">Prefab 拥有字段。</param>
        /// <param name="updates">规范化可空数值文本。</param>
        /// <param name="transaction">字段所有权事务。</param>
        private static void PatchGuarded(string path, string[] fields, Dictionary<int, string[]> updates,
            AuthoringExportTransaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            Dictionary<int, string[]> normalized = NormalizeUpdates(fields, updates);
            transaction.Observe(path, fields, normalized);
            if (!transaction.IsDryRun) PatchValues(path, fields, normalized);
        }

        /// <summary>按 ID 和字段读取当前 Excel 数值，并规范化为稳定比较文本。</summary>
        /// <param name="path">业务 Excel 路径。</param>
        /// <param name="fields">待读取字段。</param>
        /// <param name="ids">待读取行 ID。</param>
        /// <returns>按 ID 索引、与 fields 顺序一致的可空规范数值。</returns>
        /// <exception cref="InvalidOperationException">字段缺失、ID 不存在或单元格不是数值。</exception>
        internal static Dictionary<int, string[]> ReadValues(string path, string[] fields, IEnumerable<int> ids)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var zip = ZipFile.OpenRead(path);
            string[] strings = ReadSharedStrings(zip, ns);
            XDocument xml = ReadSheet(zip);
            XElement[] rows = xml.Descendants(ns + "row").ToArray();
            Dictionary<string, string> headers = ReadHeaders(rows, strings, ns);
            ValidateFields(headers, fields);
            var required = new HashSet<int>(ids);
            var result = new Dictionary<int, string[]>();
            foreach (XElement row in rows)
            {
                XElement[] cells = row.Elements(ns + "c").ToArray();
                if (!int.TryParse(Read(cells.FirstOrDefault(c => Column(c) == headers["id"]), strings, ns),
                        out int id) || !required.Contains(id)) continue;
                result[id] = fields.Select(field => CanonicalNumber(
                    Read(cells.FirstOrDefault(c => Column(c) == headers[field]), strings, ns))).ToArray();
            }

            if (result.Count != required.Count) throw new InvalidOperationException("预制体 ID 不存在于源表。");
            return result;
        }

        /// <summary>将已格式化的数值写入工作表，供整数协议和直接像素浮点协议共用。</summary>
        /// <param name="path">待更新业务 Excel。</param>
        /// <param name="fields">源表字段名。</param>
        /// <param name="updates">按 ID 索引的可空数值文本。</param>
        /// <remarks>直接更新 sheet1.xml；不改变其他 ZIP 条目。</remarks>
        /// <exception cref="InvalidOperationException">源表缺少字段、字段数错误或某个 ID 不存在。</exception>
        private static void PatchValues(string path, string[] fields, Dictionary<int, string[]> updates)
        {
            updates = NormalizeUpdates(fields, updates);
            Dictionary<int, string[]> current = ReadValues(path, fields, updates.Keys);
            if (updates.All(pair => pair.Value.SequenceEqual(current[pair.Key], StringComparer.Ordinal))) return;

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            string[] strings = ReadSharedStrings(zip, ns);
            ZipArchiveEntry entry = zip.GetEntry("xl/worksheets/sheet1.xml") ??
                                    throw new InvalidOperationException("源表缺少 sheet1.xml。");
            XDocument xml;
            using (var input = entry.Open()) xml = XDocument.Load(input);
            XElement[] rows = xml.Descendants(ns + "row").ToArray();
            Dictionary<string, string> headers = ReadHeaders(rows, strings, ns);
            ValidateFields(headers, fields);
            var found = new HashSet<int>();
            foreach (XElement row in rows)
            {
                XElement[] cells = row.Elements(ns + "c").ToArray();
                if (!int.TryParse(Read(cells.FirstOrDefault(c => Column(c) == headers["id"]), strings, ns), out int id) || !updates.TryGetValue(id, out var values)) continue;
                found.Add(id);
                for (int i = 0; i < fields.Length; i++)
                {
                    string address = headers[fields[i]] + (string)row.Attribute("r");
                    XElement cell = cells.FirstOrDefault(c => (string)c.Attribute("r") == address);
                    string oldValue = CanonicalNumber(Read(cell, strings, ns));
                    if (string.Equals(oldValue, values[i], StringComparison.Ordinal)) continue;
                    if (values[i] == null)
                    {
                        cell?.Remove();
                        continue;
                    }
                    if (cell == null) { cell = new XElement(ns + "c", new XAttribute("r", address)); row.Add(cell); }
                    cell.Attribute("t")?.Remove(); cell.Elements().Remove();
                    cell.Add(new XElement(ns + "v", values[i]));
                }
            }
            if (found.Count != updates.Count) throw new InvalidOperationException("预制体 ID 不存在于源表。");
            entry.Delete(); using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); xml.Save(output);
        }

        /// <summary>校验并规范化本次写入值，拒绝 NaN、无穷值和字段数不一致。</summary>
        /// <param name="fields">目标字段列表。</param>
        /// <param name="updates">按 ID 索引的可空数值文本。</param>
        /// <returns>可安全比较及写入 SpreadsheetML 的规范十进制值。</returns>
        /// <exception cref="InvalidOperationException">字段数不一致或值不是有限十进制数。</exception>
        private static Dictionary<int, string[]> NormalizeUpdates(string[] fields,
            Dictionary<int, string[]> updates)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            if (updates == null) throw new ArgumentNullException(nameof(updates));
            return updates.ToDictionary(pair => pair.Key, pair =>
            {
                if (pair.Value == null || pair.Value.Length != fields.Length)
                    throw new InvalidOperationException("导出字段数不匹配。");
                return pair.Value.Select(CanonicalNumber).ToArray();
            });
        }

        /// <summary>读取共享字符串表；没有共享字符串条目时返回空数组。</summary>
        /// <param name="zip">已打开的工作簿 ZIP。</param>
        /// <param name="ns">SpreadsheetML 命名空间。</param>
        /// <returns>按索引排列的共享字符串。</returns>
        private static string[] ReadSharedStrings(ZipArchive zip, XNamespace ns)
        {
            ZipArchiveEntry shared = zip.GetEntry("xl/sharedStrings.xml");
            if (shared == null) return Array.Empty<string>();
            using Stream input = shared.Open();
            return XDocument.Load(input).Descendants(ns + "si")
                .Select(element => string.Concat(element.Descendants(ns + "t").Select(text => text.Value)))
                .ToArray();
        }

        /// <summary>读取第一个工作表 XML。</summary>
        /// <param name="zip">已打开的工作簿 ZIP。</param>
        /// <returns>完整工作表文档。</returns>
        /// <exception cref="InvalidOperationException">工作簿缺少 sheet1.xml。</exception>
        private static XDocument ReadSheet(ZipArchive zip)
        {
            ZipArchiveEntry entry = zip.GetEntry("xl/worksheets/sheet1.xml") ??
                                    throw new InvalidOperationException("源表缺少 sheet1.xml。");
            using Stream input = entry.Open();
            return XDocument.Load(input);
        }

        /// <summary>从第一行读取规范字段名与 Excel 列字母。</summary>
        /// <param name="rows">工作表全部行。</param>
        /// <param name="strings">共享字符串。</param>
        /// <param name="ns">SpreadsheetML 命名空间。</param>
        /// <returns>字段名到列字母的映射。</returns>
        /// <exception cref="InvalidOperationException">缺少或重复表头。</exception>
        private static Dictionary<string, string> ReadHeaders(XElement[] rows, string[] strings, XNamespace ns)
        {
            XElement headerRow = rows.SingleOrDefault(row => (string)row.Attribute("r") == "1") ??
                                 throw new InvalidOperationException("源表缺少第一行字段名。");
            return headerRow.Elements(ns + "c")
                .Select(cell => new { Name = NormalizeHeader(Read(cell, strings, ns)), Column = Column(cell) })
                .Where(header => header.Name.Length > 0)
                .ToDictionary(header => header.Name, header => header.Column, StringComparer.Ordinal);
        }

        /// <summary>确认 ID 列和全部 Prefab 拥有字段已存在。</summary>
        /// <param name="headers">工作表字段映射。</param>
        /// <param name="fields">本次访问字段。</param>
        /// <exception cref="InvalidOperationException">任一字段缺失。</exception>
        private static void ValidateFields(Dictionary<string, string> headers, string[] fields)
        {
            string[] missingFields = fields.Prepend("id").Where(field => !headers.ContainsKey(field)).Distinct().ToArray();
            if (missingFields.Length > 0)
                throw new InvalidOperationException($"源表缺少导出字段：{string.Join(", ", missingFields)}。");
        }

        /// <summary>把 Excel 数值文本规范为无多余零的稳定十进制形式。</summary>
        /// <param name="value">共享字符串、内联字符串或 v 节点值。</param>
        /// <returns>空单元格返回空；有效数值返回规范文本。</returns>
        /// <exception cref="InvalidOperationException">非空单元格不是有限十进制数。</exception>
        private static string CanonicalNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
                throw new InvalidOperationException("Prefab 拥有字段包含非数值单元格：" + value);
            return number.ToString("G29", CultureInfo.InvariantCulture);
        }

        /// <summary>读取 Excel 的共享字符串、内联文本或原始数值，供列名与 ID 匹配。</summary>
        /// <param name="cell">待解析单元格，允许不存在。</param><param name="strings">共享字符串表。</param><param name="ns">工作表命名空间。</param>
        /// <returns>单元格文本，不存在时为空。</returns>
        private static string Read(XElement cell, string[] strings, XNamespace ns) => cell == null ? "" :
            (string)cell.Attribute("t") == "s" ? strings[int.Parse(cell.Element(ns + "v").Value)] :
            (string)cell.Attribute("t") == "inlineStr" ? string.Concat(cell.Descendants(ns + "t").Select(t => t.Value)) : cell.Element(ns + "v")?.Value ?? "";

        /// <summary>移除 Excel 表头单元格意外携带的首尾换行和缩进，保持字段名匹配稳定。</summary>
        /// <param name="header">从共享字符串或内联文本读取的原始表头。</param>
        /// <returns>用于精确查找的规范字段名。</returns>
        private static string NormalizeHeader(string header) => (header ?? string.Empty).Trim();

        /// <summary>提取单元格地址中的列字母，导出时按列匹配字段。</summary>
        /// <param name="cell">包含 r 地址属性的单元格。</param><returns>列字母。</returns>
        private static string Column(XElement cell) => new string(((string)cell.Attribute("r")).TakeWhile(char.IsLetter).ToArray());
    }
}
