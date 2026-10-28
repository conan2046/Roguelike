using System;
using System.IO;
using System.Linq;
using cfg;
using Luban;
using Roguelike.Features.Combat.Run;

/// <summary>由Unity测试启动的跨进程Profile探针，只允许明确的临时隔离目录，拒绝玩家目录。</summary>
internal static class Program
{
    /// <summary>新进程加载正式配置并写入或验证测试目标；进程退出后不保留任何全局状态。</summary>
    /// <param name="args">模式、正式bytes目录、隔离Profile目录、目标ID。</param><returns>验证成功为零。</returns>
    /// <exception cref="ArgumentException">参数或隔离目录不合法。</exception>
    private static int Main(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Expected mode, data directory, isolated directory and goal id.");
        string directory = Path.GetFullPath(args[2]);
        string permitted = Path.Combine(Path.GetTempPath(), "roguelike-feedback-profile-tests") + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(permitted, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Probe refuses a non-isolated Profile directory.");
        var tables = new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(args[1], name + ".bytes"))));
        var goal = tables.TbContentGoal.Get(int.Parse(args[3]));
        var profile = new BuildFeedbackProfileStore(directory, tables.TbBuildFeedbackUi.DataList.Single());
        if (args[0] == "write")
        {
            profile.ApplySettlement(goal, new BuildFeedbackSettlement { RunId = "cross-process-test", SettlementId = "final",
                StageId = goal.StageIds.First(), CharacterId = goal.CharacterId, Victory = true,
                QualifiedEvents = goal.RequiredEvents, BestTargets = goal.MinimumTargets });
            profile.SetEnabled(goal.Id, true);
        }
        else if (args[0] != "verify") throw new ArgumentException("Unknown probe operation.");
        bool verified = profile.UnlockedIds.Contains(goal.Id) && profile.EnabledIds.Contains(goal.Id);
        Console.WriteLine(verified ? "PROFILE_PROCESS_VERIFIED" : "PROFILE_PROCESS_FAILED");
        return verified ? 0 : 1;
    }
}
