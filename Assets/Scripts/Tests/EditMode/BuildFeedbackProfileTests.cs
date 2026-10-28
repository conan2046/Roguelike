using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using SerializationException = System.Runtime.Serialization.SerializationException;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>仅使用独立临时目录验证原子保存、内容启用、旧版本、幂等与真正跨进程重启。</summary>
    public sealed class BuildFeedbackProfileTests
    {
        private string directory;
        private Tables tables;

        /// <summary>每项测试建立唯一隔离目录；从不引用Application.persistentDataPath。</summary>
        /// <remarks>创建本项测试独占目录，在TearDown验证归属后删除；Luban只读加载。</remarks>
        [SetUp]
        public void SetUp()
        {
            directory=Path.Combine(Path.GetTempPath(),"roguelike-feedback-profile-tests",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            tables=new Tables(name=>new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,"Config/Luban",name+".bytes"))));
        }

        /// <summary>清理本项测试独占目录，先验证绝对路径仍位于固定隔离根，拒绝删除其他目录。</summary>
        /// <remarks>仅删除SetUp创建的隔离测试目录，不访问玩家Profile。</remarks>
        [TearDown]
        public void TearDown()
        {
            string permitted=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"roguelike-feedback-profile-tests"))+Path.DirectorySeparatorChar;
            if(directory!=null && Path.GetFullPath(directory).StartsWith(permitted,StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) Directory.Delete(directory,true);
        }

        /// <summary>失败局只保存本局最佳，不能跨局凑次数；胜利解锁不自动启用，重启与重复结算不重复发奖。</summary>
        /// <param name="goalId">两个正式内容目标。</param>
        [TestCase(62001)]
        [TestCase(62002)]
        public void GoalsRequireOneWinningRunAndSettlementIsIdempotent(int goalId)
        {
            var goal=tables.TbContentGoal.Get(goalId);var ui=tables.TbBuildFeedbackUi.DataList.Single();
            var store=new BuildFeedbackProfileStore(directory,ui);
            Assert.That(File.Exists(store.FilePath),Is.False);
            var submission=Submission(goal,false);store.ApplySettlement(goal,submission);
            Assert.That(store.UnlockedIds,Is.Empty);
            var restarted=new BuildFeedbackProfileStore(directory,ui);
            Assert.That(restarted.Progress.Single().BestEvents,Is.EqualTo(goal.RequiredEvents));
            submission.RunId="winning-without-mechanism";submission.Victory=true;submission.QualifiedEvents=0;restarted.ApplySettlement(goal,submission);
            Assert.That(restarted.UnlockedIds,Is.Empty,"A prior failed run cannot provide this winning run's mechanism progress.");
            submission.RunId="winning-run";submission.QualifiedEvents=goal.RequiredEvents;restarted.ApplySettlement(goal,submission);
            Assert.That(restarted.UnlockedIds,Has.Member(goalId));Assert.That(restarted.EnabledIds,Is.Empty);
            Assert.That(restarted.SetEnabled(goalId,true),Is.True);
            byte[] before=File.ReadAllBytes(store.FilePath);
            new BuildFeedbackProfileStore(directory,ui).ApplySettlement(goal,submission);
            Assert.That(File.ReadAllBytes(store.FilePath),Is.EqualTo(before));
            Assert.That(new BuildFeedbackProfileStore(directory,ui).EnabledIds,Has.Member(goalId));
            Assert.That(restarted.SetEnabled(int.MaxValue,true),Is.False);
        }

        /// <summary>文件占用导致保存失败时不发布解锁；释放后使用原结算身份可重试且只处理一次。</summary>
        [Test]
        public void FailedSavePreservesConfirmedProfileAndCanRetry()
        {
            var goal=tables.TbContentGoal.DataList.First();var ui=tables.TbBuildFeedbackUi.DataList.Single();
            var store=new BuildFeedbackProfileStore(directory,ui);var submission=Submission(goal,true);
            using(var blocker=new FileStream(store.FilePath+".lock",FileMode.Create,FileAccess.ReadWrite,FileShare.None))
                Assert.Throws<IOException>(()=>store.ApplySettlement(goal,submission));
            Assert.That(store.UnlockedIds,Is.Empty);Assert.That(File.Exists(store.FilePath),Is.False);
            store.ApplySettlement(goal,submission);store.ApplySettlement(goal,submission);
            Assert.That(store.UnlockedIds.Count,Is.EqualTo(1));
            using(var stream=File.OpenRead(store.FilePath))
                Assert.That(((BuildFeedbackProfile)new DataContractJsonSerializer(typeof(BuildFeedbackProfile)).ReadObject(stream)).ProcessedSettlements.Count,Is.EqualTo(1));
        }

        /// <summary>已知旧封套迁移保留解锁；未来版本和损坏数据明确拒绝，原文件字节不变。</summary>
        [Test]
        public void VersionMigrationDoesNotOverwriteFutureOrCorruptData()
        {
            var ui=tables.TbBuildFeedbackUi.DataList.Single();string file=Path.Combine(directory,ui.ProfileFileName);
            using(var stream=File.Create(file))new DataContractJsonSerializer(typeof(BuildFeedbackProfile)).WriteObject(stream,
                new BuildFeedbackProfile { SchemaVersion=0,UnlockedIds={tables.TbContentGoal.DataList.First().Id} });
            var store=new BuildFeedbackProfileStore(directory,ui);
            Assert.That(store.UnlockedIds.Count,Is.EqualTo(1));
            using(var stream=File.Create(file))new DataContractJsonSerializer(typeof(BuildFeedbackProfile)).WriteObject(stream,new BuildFeedbackProfile { SchemaVersion=ui.ProfileSchemaVersion+1 });
            byte[] future=File.ReadAllBytes(file);Assert.Throws<SerializationException>(()=>new BuildFeedbackProfileStore(directory,ui));
            Assert.That(File.ReadAllBytes(file),Is.EqualTo(future));
            File.WriteAllText(file,"broken");Assert.Throws<SerializationException>(()=>new BuildFeedbackProfileStore(directory,ui));
            Assert.That(File.ReadAllText(file),Is.EqualTo("broken"));
        }

        /// <summary>启动两个独立.NET进程：第一进程保存后退出，第二进程重新读取并验证启用解锁。</summary>
        /// <remarks>探针自身也验证目录位于临时隔离根；不启动Unity进程，不访问真实玩家Profile。</remarks>
        [Test]
        public void UnlockSurvivesActualProcessExitAndRestart()
        {
            string probe=Path.Combine(Directory.GetParent(Application.dataPath).FullName,".local/build-feedback-p0-20261027/profile-probe/ProfileProbe.dll");
            Assert.That(File.Exists(probe),Is.True,"Build Tools/ProfileProbe/ProfileProbe.csproj before this cross-process verification.");
            foreach(string mode in new[]{"write","verify"})
            {
                var info=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
                info.ArgumentList.Add(probe);info.ArgumentList.Add(mode);info.ArgumentList.Add(Path.Combine(Application.streamingAssetsPath,"Config/Luban"));
                info.ArgumentList.Add(directory);info.ArgumentList.Add(tables.TbContentGoal.DataList.First().Id.ToString());
                using(var process=Process.Start(info))
                { Assert.That(process.WaitForExit(30000),Is.True);string output=process.StandardOutput.ReadToEnd();string error=process.StandardError.ReadToEnd();
                    Assert.That(process.ExitCode,Is.Zero,error);Assert.That(output,Does.Contain("PROFILE_PROCESS_VERIFIED")); }
            }
        }

        /// <summary>按当前表阈值建立独立单局提交，避免测试复制正式目标数值。</summary>
        /// <param name="goal">正式目标。</param><param name="victory">正式结算结果。</param><returns>可重试的稳定身份快照。</returns>
        private static BuildFeedbackSettlement Submission(ContentGoalConfig goal,bool victory)=>new BuildFeedbackSettlement {
            RunId="isolated-run",SettlementId="final",StageId=goal.StageIds.First(),CharacterId=goal.CharacterId,
            Victory=victory,QualifiedEvents=goal.RequiredEvents,BestTargets=goal.MinimumTargets };
    }
}
