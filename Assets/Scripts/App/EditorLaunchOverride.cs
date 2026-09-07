#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace Roguelike.App
{
    /// <summary>
    /// 编辑器专用的启动模式覆盖：把显式选择的启动参数写入本地文件，供组合根在编辑器环境下读取。
    /// </summary>
    /// <remarks>
    /// Unity Hub 启动编辑器时无法附加自定义命令行参数，因此点 Play 只能进入普通启动；
    /// 本类提供等价的显式选择通道。文件写入 Library 目录，不纳入版本控制、不参与构建；
    /// 整个类型只在 UNITY_EDITOR 下编译，真机始终只解析命令行。
    /// </remarks>
    public static class EditorLaunchOverride
    {
        /// <summary>覆盖文件的绝对路径；置于 Library 下以避免进入版本控制。</summary>
        public static string FilePath =>
            Path.Combine(Application.dataPath, "..", "Library", "Roguelike.LaunchMode.txt");

        /// <summary>写入显式启动参数；传入空白表示清除覆盖、回到普通启动。</summary>
        /// <param name="arguments">与命令行等价的参数序列，例如 "-stage 1"。</param>
        /// <remarks>只写本地文件，不修改场景、预制体或任何配置表。</remarks>
        public static void Write(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, arguments.Trim());
        }

        /// <summary>读取当前覆盖的启动参数；无覆盖时返回空序列。</summary>
        /// <returns>与命令行等价的参数序列；空序列表示未设置覆盖。</returns>
        public static string[] Read()
        {
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            string content = File.ReadAllText(FilePath);
            return string.IsNullOrWhiteSpace(content)
                ? Array.Empty<string>()
                : content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
#endif
