using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>编辑器配置导出共用的整数单元格写入器，保留 Excel 中无关样式和 ZIP 内容。</summary>
    public static class ConfigWorkbookWriter
    {
        /// <summary>用户导出时按 ID 和列名更新既有源表，不新增未知 ID 或隐式字段。</summary>
        /// <param name="path">待更新业务 Excel。</param>
        /// <param name="fields">源表内已有的千分整数列。</param>
        /// <param name="updates">按 ID 索引的整数数组，与 fields 顺序一致。</param>
        /// <remarks>直接写文件；调用方必须备份源表并在失败时恢复。文件占用时抛出 IO 异常，不绕过 Excel 锁。</remarks>
        /// <exception cref="InvalidOperationException">字段数错误或某个 ID 不存在。</exception>
        public static void Patch(string path, string[] fields, Dictionary<int, int[]> updates)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            string[] strings = Array.Empty<string>();
            var shared = zip.GetEntry("xl/sharedStrings.xml");
            if (shared != null) { using var input = shared.Open(); strings = XDocument.Load(input).Descendants(ns + "si").Select(e => string.Concat(e.Descendants(ns + "t").Select(t => t.Value))).ToArray(); }
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml"); XDocument xml;
            using (var input = entry.Open()) xml = XDocument.Load(input);
            var rows = xml.Descendants(ns + "row").ToArray();
            var headers = rows.Single(r => (string)r.Attribute("r") == "1").Elements(ns + "c").ToDictionary(c => Read(c, strings, ns), Column);
            var found = new HashSet<int>();
            foreach (var row in rows)
            {
                var cells = row.Elements(ns + "c").ToArray();
                if (!int.TryParse(Read(cells.FirstOrDefault(c => Column(c) == headers["id"]), strings, ns), out int id) || !updates.TryGetValue(id, out var values)) continue;
                if (values.Length != fields.Length) throw new InvalidOperationException("导出字段数不匹配。");
                found.Add(id);
                for (int i = 0; i < fields.Length; i++)
                {
                    string address = headers[fields[i]] + (string)row.Attribute("r");
                    var cell = cells.FirstOrDefault(c => (string)c.Attribute("r") == address);
                    if (cell == null) { cell = new XElement(ns + "c", new XAttribute("r", address)); row.Add(cell); }
                    cell.Attribute("t")?.Remove(); cell.Elements().Remove();
                    cell.Add(new XElement(ns + "v", values[i].ToString(CultureInfo.InvariantCulture)));
                }
            }
            if (found.Count != updates.Count) throw new InvalidOperationException("预制体 ID 不存在于源表。");
            entry.Delete(); using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); xml.Save(output);
        }

        /// <summary>读取 Excel 的共享字符串、内联文本或原始数值，供列名与 ID 匹配。</summary>
        /// <param name="cell">待解析单元格，允许不存在。</param><param name="strings">共享字符串表。</param><param name="ns">工作表命名空间。</param>
        /// <returns>单元格文本，不存在时为空。</returns>
        private static string Read(XElement cell, string[] strings, XNamespace ns) => cell == null ? "" :
            (string)cell.Attribute("t") == "s" ? strings[int.Parse(cell.Element(ns + "v").Value)] :
            (string)cell.Attribute("t") == "inlineStr" ? string.Concat(cell.Descendants(ns + "t").Select(t => t.Value)) : cell.Element(ns + "v")?.Value ?? "";

        /// <summary>提取单元格地址中的列字母，导出时按列匹配字段。</summary>
        /// <param name="cell">包含 r 地址属性的单元格。</param><returns>列字母。</returns>
        private static string Column(XElement cell) => new string(((string)cell.Attribute("r")).TakeWhile(char.IsLetter).ToArray());
    }
}
