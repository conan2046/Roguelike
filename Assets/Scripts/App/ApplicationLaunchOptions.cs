using System;
using System.Collections.Generic;
using System.Globalization;

namespace Roguelike.App
{
    /// <summary>组合根的互斥启动分支，不承载任何玩法默认值。</summary>
    public enum ApplicationMode { Normal, Stage, Performance, Combat }

    /// <summary>只解析调用方显式请求的模式和表 ID；无参数时保持普通启动。</summary>
    public sealed class ApplicationLaunchOptions
    {
        public ApplicationMode Mode { get; private set; }
        public int? ScenarioId { get; private set; }

        /// <summary>启动最早阶段解析命令行，拒绝重复、缺值、非法 ID 和模式冲突。</summary>
        /// <param name="arguments">进程参数，测试可传入隔离参数序列。</param>
        /// <returns>唯一启动分支；无请求不添加默认场景 ID。</returns>
        /// <exception cref="ArgumentException">参数非法或相互冲突。</exception>
        public static ApplicationLaunchOptions Parse(IReadOnlyList<string> arguments)
        {
            var result = new ApplicationLaunchOptions();
            for (int i = 0; i < arguments.Count; i++)
            {
                string argument = arguments[i];
                if (argument != "-stage" && argument != "-combatScenario" && argument != "-performanceScenario") continue;
                if (result.Mode != ApplicationMode.Normal) throw new ArgumentException("Launch modes must be specified exactly once and cannot be combined.");
                if (++i >= arguments.Count || !int.TryParse(arguments[i], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0)
                    throw new ArgumentException("Scenario argument requires a positive Luban ID.");
                result.Mode = argument == "-stage" ? ApplicationMode.Stage :
                    argument == "-combatScenario" ? ApplicationMode.Combat : ApplicationMode.Performance;
                result.ScenarioId = id;
            }
            return result;
        }
    }
}
