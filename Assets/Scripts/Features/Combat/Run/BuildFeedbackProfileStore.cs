using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>Profile 文件封套；字段名和顺序属于持久化技术协议，不存局内可恢复状态。</summary>
    [DataContract]
    public sealed class BuildFeedbackProfile
    {
        [DataMember(Order = 0)] public int SchemaVersion;
        [DataMember(Order = 1)] public List<int> UnlockedIds = new List<int>();
        [DataMember(Order = 2)] public List<int> EnabledIds = new List<int>();
        [DataMember(Order = 3)] public List<BuildFeedbackGoalRecord> Progress = new List<BuildFeedbackGoalRecord>();
        [DataMember(Order = 4)] public List<string> ProcessedSettlements = new List<string>();
    }

    /// <summary>只保存单局历史最佳，不跨局累加要求同一局完成的挑战次数。</summary>
    [DataContract]
    public sealed class BuildFeedbackGoalRecord
    {
        [DataMember(Order = 0)] public int GoalId;
        [DataMember(Order = 1)] public int BestEvents;
        [DataMember(Order = 2)] public int BestTargets;
    }

    /// <summary>带稳定身份的结算提交，能在失败重试与进程重启后识别同一结算。</summary>
    public sealed class BuildFeedbackSettlement
    {
        public string RunId, SettlementId;
        public int StageId, CharacterId, QualifiedEvents, BestTargets;
        public bool Victory;
    }

    /// <summary>显式目录拥有的最小 Profile；不使用全局实例、不推导测试路径、不提供局中续玩。</summary>
    public sealed class BuildFeedbackProfileStore
    {
        private readonly string filePath;
        private readonly int schemaVersion;
        private readonly object gate = new object();
        private BuildFeedbackProfile current;
        public string FilePath => filePath;
        public IReadOnlyList<int> UnlockedIds => current.UnlockedIds.ToArray();
        public IReadOnlyList<int> EnabledIds => current.EnabledIds.Intersect(current.UnlockedIds).ToArray();
        public IReadOnlyList<BuildFeedbackGoalRecord> Progress => current.Progress.Select(p => new BuildFeedbackGoalRecord
            { GoalId = p.GoalId, BestEvents = p.BestEvents, BestTargets = p.BestTargets }).ToArray();

        /// <summary>组合根显式传入目录与 TbBuildFeedbackUi 存储协议，首次仅加载，不创建或覆盖文件。</summary>
        /// <param name="directory">玩家目录或测试专属隔离目录。</param><param name="config">正式文件名和版本配置。</param>
        /// <exception cref="ArgumentException">路径或版本配置非法。</exception>
        /// <exception cref="SerializationException">存档损坏、缺少字段或版本不受支持。</exception>
        /// <exception cref="IOException">读取文件失败。</exception>
        /// <remarks>只读取显式目录中TbBuildFeedbackUi.profileFileName对应文件；不自动创建真实或测试档案。</remarks>
        public BuildFeedbackProfileStore(string directory, BuildFeedbackUiConfig config)
        {
            if (string.IsNullOrWhiteSpace(directory) || config == null || config.ProfileSchemaVersion <= 0 ||
                string.IsNullOrWhiteSpace(config.ProfileFileName) || Path.GetFileName(config.ProfileFileName) != config.ProfileFileName)
                throw new ArgumentException("Invalid explicit Profile storage configuration.");
            filePath = Path.Combine(Path.GetFullPath(directory), config.ProfileFileName); schemaVersion = config.ProfileSchemaVersion;
            current = Load();
        }

        /// <summary>开局前启用／禁用已解锁内容，原子保存成功后才更新内存；不反向锁已有基础内容。</summary>
        /// <param name="contentId">TbContentGoal.id 对应的平行玩法内容。</param><param name="enabled">本局前希望固定的开关。</param>
        /// <returns>内容已解锁并完成提交时为真。</returns>
        /// <remarks>写 Profile 与原子替换文件；调用方失败时保留原 UI 状态并允许重试。</remarks>
        /// <exception cref="IOException">目录、锁、刷盘或原子替换失败。</exception>
        public bool SetEnabled(int contentId, bool enabled)
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                using (var lease = new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var next = Load(); if (!next.UnlockedIds.Contains(contentId)) return false;
                    if (enabled && !next.EnabledIds.Contains(contentId)) next.EnabledIds.Add(contentId);
                    if (!enabled) next.EnabledIds.Remove(contentId);
                    Save(next); current = next; return true;
                }
            }
        }

        /// <summary>局末以 runId＋settlementId 同事务提交历史最佳、胜利目标解锁和已处理标记。</summary>
        /// <param name="goal">本局 TbContentGoal。</param><param name="submission">稳定结算快照，不是多局累计。</param>
        /// <returns>已保存或此前已保存同一结算时为真；重复不会增加奖励。</returns>
        /// <remarks>独占文件锁后重读最新档案；临时文件刷盘并原子替换后才发布内存结果。</remarks>
        /// <exception cref="ArgumentException">身份、模式、角色或进度非法。</exception>
        /// <exception cref="IOException">保存失败；原已确认档案不变，可用原 submission 重试。</exception>
        /// <exception cref="SerializationException">现有档案损坏或版本不受支持。</exception>
        public bool ApplySettlement(ContentGoalConfig goal, BuildFeedbackSettlement submission)
        {
            if (goal == null || submission == null || string.IsNullOrWhiteSpace(submission.RunId) ||
                string.IsNullOrWhiteSpace(submission.SettlementId) || !goal.StageIds.Contains(submission.StageId) ||
                goal.CharacterId != submission.CharacterId || submission.QualifiedEvents < 0 || submission.BestTargets < 0)
                throw new ArgumentException("Invalid or unavailable content-goal settlement.");
            string key = submission.RunId + ":" + submission.SettlementId;
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                using (var lease = new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var next = Load();
                    if (next.ProcessedSettlements.Contains(key)) { current = next; return true; }
                    var progress = next.Progress.FirstOrDefault(p => p.GoalId == goal.Id);
                    if (progress == null) { progress = new BuildFeedbackGoalRecord { GoalId = goal.Id }; next.Progress.Add(progress); }
                    progress.BestEvents = Math.Max(progress.BestEvents, submission.QualifiedEvents);
                    progress.BestTargets = Math.Max(progress.BestTargets, submission.BestTargets);
                    bool completed = submission.QualifiedEvents >= goal.RequiredEvents && submission.BestTargets >= goal.MinimumTargets &&
                        (!goal.RequiresVictory || submission.Victory);
                    if (completed && !next.UnlockedIds.Contains(goal.Id)) next.UnlockedIds.Add(goal.Id);
                    next.ProcessedSettlements.Add(key); Save(next); current = next; return true;
                }
            }
        }

        /// <summary>原子提交或构造时读取完整封套；已知旧技术版本迁移到当前表版本，未来版本与损坏文件明确失败。</summary>
        /// <returns>独立可提交的 DTO，不暴露当前内存状态。</returns>
        /// <exception cref="SerializationException">封套无效或版本不受支持。</exception>
        /// <exception cref="IOException">读取失败。</exception>
        /// <remarks>只借用读流并立即释放；迁移仅在返回DTO中进行，下一次成功原子保存前不改变原文件。</remarks>
        private BuildFeedbackProfile Load()
        {
            if (!File.Exists(filePath)) return new BuildFeedbackProfile { SchemaVersion = schemaVersion };
            BuildFeedbackProfile data;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                data = new DataContractJsonSerializer(typeof(BuildFeedbackProfile)).ReadObject(stream) as BuildFeedbackProfile;
            if (data == null || data.SchemaVersion < 0 || data.SchemaVersion > schemaVersion ||
                (data.SchemaVersion != schemaVersion && data.SchemaVersion != 0)) throw new SerializationException("Unsupported Profile version.");
            if (data.SchemaVersion == 0)
            {
                data.SchemaVersion = schemaVersion;
                data.UnlockedIds = data.UnlockedIds ?? new List<int>(); data.EnabledIds = data.EnabledIds ?? new List<int>();
                data.Progress = data.Progress ?? new List<BuildFeedbackGoalRecord>();
                data.ProcessedSettlements = data.ProcessedSettlements ?? new List<string>();
            }
            if (data.UnlockedIds == null || data.EnabledIds == null || data.Progress == null || data.ProcessedSettlements == null ||
                data.UnlockedIds.Any(id => id <= 0) || data.Progress.Any(p => p == null || p.GoalId <= 0 || p.BestEvents < 0 || p.BestTargets < 0))
                throw new SerializationException("Invalid Profile payload.");
            return data;
        }

        /// <summary>在同一目录创建技术临时文件并刷盘；替换和幂等标记同一提交，不使用删除原档再移动的窗口。</summary>
        /// <param name="data">独占锁内重新读取并修改的完整档案。</param>
        /// <remarks>仅创建本存储所有的临时／备份文件；失败不更新内存，finally清理本次临时文件。</remarks>
        /// <exception cref="IOException">序列化、刷盘或原子替换失败。</exception>
        private void Save(BuildFeedbackProfile data)
        {
            string temporary = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { new DataContractJsonSerializer(typeof(BuildFeedbackProfile)).WriteObject(stream, data); stream.Flush(true); }
                if (File.Exists(filePath)) File.Replace(temporary, filePath, filePath + ".bak");
                else File.Move(temporary, filePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
