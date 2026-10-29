using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>通过正式选角、输入帧与UI射线观察磐岳自然胜负及跨局目标，不注入战斗结果。</summary>
        /// <returns>有界自然流程、真实结算与退出清理协程。</returns>
        /// <remarks>测试允许自然失败；通过仅证明观察流程及生命周期有效，胜负与目标另存报告。使用隔离Profile。</remarks>
        [UnityTest, Timeout(600000), Explicit("双职业自然验收专项；读取报告判断胜负和目标，不能以测试通过代替自然通关。")]
        public IEnumerator NaturalGuardRunRecordsOutcomeAndGoal() => ObserveNaturalRole(0);

        /// <summary>通过正式选角、折返移动与UI射线观察周天自然胜负及跨局目标，不注入战斗结果。</summary>
        /// <returns>有界自然流程、真实结算与退出清理协程。</returns>
        /// <remarks>独立加载正式表与资源，不继承磐岳存档；报告区分机制事件、目标、胜负和保存。</remarks>
        [UnityTest, Timeout(600000), Explicit("双职业自然验收专项；读取报告判断胜负和目标，不能以测试通过代替自然通关。")]
        public IEnumerator NaturalDomainRunRecordsOutcomeAndGoal() => ObserveNaturalRole(1);

        /// <summary>磐岳保留全部升级收益，尝试把雷击引向自然敌群，区分输入策略与正式规则的影响。</summary>
        /// <returns>第二种输入策略的自然流程与独立报告。</returns>
        /// <remarks>只调整移动和选卡决策，仍不注入战斗状态；结算与清理沿原验收链。</remarks>
        [UnityTest, Timeout(600000), Explicit("不消耗选卡次数的引雷输入样本；胜负以报告为准。")]
        public IEnumerator NaturalGuardLureWithoutDraftSpending() => ObserveNaturalRole(0, true);

        /// <summary>周天保留升级收益并主动引雷，补充折返策略以外的自然可达性样本。</summary>
        /// <returns>第二种输入策略的自然流程与独立报告。</returns>
        /// <remarks>读取同一正式表，使用新的隔离存档；不能把一次通关推断为平衡已通过。</remarks>
        [UnityTest, Timeout(600000), Explicit("不消耗选卡次数的引雷输入样本；胜负以报告为准。")]
        public IEnumerator NaturalDomainLureWithoutDraftSpending() => ObserveNaturalRole(1, true);

        /// <summary>从TbTribulationUi入口表选择角色，按正式模拟步输入移动并真实点击升级、炼器与结算；记录自然终局。</summary>
        /// <param name="role">入口表中的角色索引；只决定输入策略，不改变任何业务配置。</param>
        /// <param name="deliberateLure">是否保留选卡次数并主动靠近自然敌群引雷。</param>
        /// <returns>加载、自然输入、报告与资源清理协程。</returns>
        /// <remarks>测试协议上限为六分钟模拟时间；每渲染帧输入一个正式模拟步，不等同真人试玩。没有HP、经验、敌人、时钟或随机种子注入。</remarks>
        private IEnumerator ObserveNaturalRole(int role, bool deliberateLure = false)
        {
#if UNITY_EDITOR
            using var gameView = new HudCoverageGameView();
            gameView.Select(1920, 1080);
            yield return null;
#endif
            tribulationTestInitializer = new YooAssetPackageInitializer();
            yield return Wait(tribulationTestInitializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(tribulationTestInitializer);
            yield return Wait(config.InitializeAsync(CancellationToken.None));
            var entryConfig = config.Tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry);
            var definition = CombatRunDefinition.Create(config.Tables, entryConfig.StageIds[role]);
            var resources = new TribulationTrackingResources(new YooAssetResourceService(tribulationTestInitializer, config));
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../.local/natural-acceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + role));
            Directory.CreateDirectory(folder);
            var profile = new BuildFeedbackProfileStore(Path.Combine(folder, "isolated-profile"), definition.DraftPolicy.UiId_Ref);
            int baseline = World.All.Count;
            tribulationTestRoot = new GameObject("Natural role acceptance");
            var entry = tribulationTestRoot.AddComponent<TribulationEntryController>();
            yield return Wait(entry.InitializeAsync(config.Tables, resources, CancellationToken.None, profile));
            var lobby = entry.LobbyRoot.GetComponent<TribulationLobbyView>();
            yield return ClickFeedbackButtonThroughRaycast(lobby.RoleButtons[role]);
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((entry.ActiveRunner == null || !entry.ActiveRunner.Ready) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            var runner = entry.ActiveRunner;
            Assert.That(runner != null && runner.Ready, Is.True, "Formal role load must complete.");
            runner.enabled = false;
            var model = runner.RunModel;
            var trial = runner.Coordinator.Tribulation;
            double observationStart = Time.realtimeSinceStartupAsDouble;
            var report = new NaturalAcceptanceReport { role = definition.Tribulation.RoleName, stageId = entryConfig.StageIds[role], deliberateLure = deliberateLure };
            Assert.That(runner.TribulationUiRoot.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(runner.TribulationUiRoot.GetComponent<Canvas>().sortingOrder,
                Is.GreaterThan(runner.FormalUiRoot.GetComponentInChildren<CombatHudView>().GetComponent<Canvas>().sortingOrder));
            var events = new List<string>();
            int frames = 0;
            bool rerolled = deliberateLure, skipped = deliberateLure, bossCaptured = false;
            TribulationTrialState lastTrial = trial.State;
            long simulationLimit = 360000;
            while (!model.Result.HasValue && model.ElapsedMilli < simulationLimit && frames < 30000)
            {
                if (model.State == CombatRunState.UpgradeChoice)
                {
                    runner.SendMessage("LateUpdate");
                    yield return null;
                    var panel = runner.BuildFeedbackUiRoot.GetComponent<BuildFeedbackPanelView>();
                    if (!rerolled && model.CanReroll)
                    {
                        int before = model.RerollsRemaining;
                        yield return ClickFeedbackButtonThroughRaycast(panel.RerollButton);
                        Assert.That(model.RerollsRemaining, Is.EqualTo(before - 1));
                        events.Add(model.ElapsedMilli + ":reroll"); rerolled = true;
                    }
                    else if (!skipped && model.SkipsRemaining > 0 && model.Level >= 4)
                    {
                        int before = model.SkipsRemaining;
                        yield return ClickFeedbackButtonThroughRaycast(panel.SkipButton);
                        Assert.That(model.SkipsRemaining, Is.EqualTo(before - 1));
                        events.Add(model.ElapsedMilli + ":skip"); skipped = true;
                    }
                    else
                    {
                        int selected = SelectNaturalUpgrade(model);
                        int id = model.CurrentChoices[selected].Id;
                        var cards = runner.FormalUiRoot.GetComponentsInChildren<UpgradeCardView>();
                        yield return ClickFeedbackButtonThroughRaycast(cards[selected].SelectButton);
                        events.Add(model.ElapsedMilli + ":upgrade:" + id);
                    }
                    runner.AdvanceFrame(0, default);
                    continue;
                }
                if (model.State == CombatRunState.ChestReward)
                {
                    var panel = runner.FormalUiRoot.GetComponentInChildren<ChestRewardPanelView>();
                    var cards = panel.GetComponentsInChildren<UpgradeCardView>();
                    yield return ClickFeedbackButtonThroughRaycast(cards.Length > 0 ? cards[0].SelectButton : panel.ClaimButton);
                    events.Add(model.ElapsedMilli + ":chest");
                    runner.AdvanceFrame(0, default);
                    continue;
                }
                var hud = runner.TribulationUiRoot.GetComponent<TribulationHudView>();
                if (trial.State == TribulationTrialState.RewardPending)
                {
                    int prior = trial.CurrentRefinementId.HasValue ? trial.Config.RefinementIds.IndexOf(trial.CurrentRefinementId.Value) : -1;
                    int selected = (prior + 1) % trial.Config.RefinementIds.Count;
                    yield return ClickFeedbackButtonThroughRaycast(hud.RefinementButtons[selected]);
                    events.Add(model.ElapsedMilli + ":refine:" + trial.Config.RefinementIds[selected]);
                    runner.AdvanceFrame(0, default);
                    continue;
                }
                var player = runner.Session.ReadUnit(0);
                float scale = definition.CombatRules.WorldUnitsPerPixel;
                float2 pos = player.Position / scale;
                var altar = trial.NearestAvailableAltar(pos.x, pos.y);
                bool seekAltar = !trial.CurrentRefinementId.HasValue;
                if (seekAltar && altar != null && (trial.State == TribulationTrialState.Idle || trial.State == TribulationTrialState.Failed) && hud.ActivateButton.gameObject.activeInHierarchy)
                {
                    yield return ClickFeedbackButtonThroughRaycast(hud.ActivateButton);
                    events.Add(model.ElapsedMilli + ":altar:" + altar.Id);
                }
                float2 movement = NaturalMovement(runner, role, seekAltar, deliberateLure);
                runner.AdvanceFrame(runner.Session.StepSeconds, new CombatInputFrame { Movement = movement });
                if (trial.State != lastTrial)
                {
                    events.Add(model.ElapsedMilli + ":trial:" + trial.State + ":hits:" + trial.EnemyHits);
                    lastTrial = trial.State;
                }
                frames++;
                runner.SendMessage("LateUpdate");
                yield return null;
                if (!bossCaptured && trial.BossPhase > 0)
                {
                    bossCaptured = true;
                    yield return CaptureGameScreen("natural-" + Path.GetFileName(folder) + "-boss.png");
                }
                if (frames % 600 == 0)
                    File.WriteAllText(Path.Combine(folder, "progress.json"), JsonUtility.ToJson(new NaturalProgress { time = model.ElapsedMilli, health = model.CurrentHealth, kills = model.Kills, level = model.Level, trial = trial.State.ToString(), trialHits = trial.EnemyHits, bossPhase = trial.BossPhase }, true));
            }
            runner.SendMessage("LateUpdate");
            yield return null;
            var feedback = runner.Coordinator.BuildFeedback;
            var goal = definition.DraftPolicy.GoalId_Ref;
            report.result = model.Result.HasValue ? model.Result.Value.ToString() : "ObservationLimit";
            report.wallSeconds = Time.realtimeSinceStartupAsDouble - observationStart;
            report.simulatedSecondsPerWallSecond = model.ElapsedMilli / 1000d / report.wallSeconds;
            report.elapsedMilli = model.ElapsedMilli; report.health = runner.Session.ReadUnit(0).Target.Health;
            report.kills = model.Kills; report.level = model.Level; report.goalEvents = feedback.GoalEvents;
            report.requiredEvents = goal.RequiredEvents; report.minimumTargets = goal.MinimumTargets; report.bestTargets = feedback.BestTargets;
            report.goalCompleted = feedback.GoalCompleted(model.Result == cfg.ERunResultType.Victory);
            report.counterEvents = feedback.EffectiveCounters; report.detonationEvents = feedback.EffectiveDetonations;
            report.damage = feedback.TotalDamage; report.incomingDamage = feedback.IncomingDamage;
            report.deathSource = feedback.DeathSource.ToString(); report.refinement = trial.CurrentRefinementId ?? 0;
            report.bossPhases = feedback.BossPhaseEntries.Keys.ToArray(); report.actions = events.ToArray();
            report.unlocked = profile.UnlockedIds.ToArray(); report.saved = runner.TryFinalizeProfile();
            report.sources = feedback.DamageBySource.Select(p => p.Key + ":" + p.Value).ToArray();
            report.weapons = model.Build.CaptureWeapons().Select(w => w.SkillId).ToArray();
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonUtility.ToJson(report, true));
            yield return CaptureGameScreen("natural-" + Path.GetFileName(folder) + "-settlement.png");
            Assert.That(model.Result.HasValue, Is.True, "No natural settlement within the observation window; inspect report, do not force victory.");
            Assert.That(report.damage, Is.GreaterThan(0));
            Assert.That(report.saved, Is.True);
            var reread = new BuildFeedbackProfileStore(Path.Combine(folder, "isolated-profile"), definition.DraftPolicy.UiId_Ref);
            Assert.That(reread.UnlockedIds.Contains(goal.Id), Is.EqualTo(report.goalCompleted));
            Assert.That(reread.EnabledIds, Is.Empty, "Unlock must not silently enable new content.");
            if (model.Result.HasValue)
            {
                var settlement = runner.FormalUiRoot.GetComponentInChildren<SettlementPanelView>();
                yield return ClickFeedbackButtonThroughRaycast(settlement.RestartButton);
                Assert.That(runner.RunModel.Result.HasValue, Is.False);
                Assert.That(runner.RunModel.ElapsedMilli, Is.Zero);
            }
            yield return ClickFeedbackButtonThroughRaycast(runner.TribulationUiRoot.GetComponent<TribulationHudView>().ReturnButton);
            yield return null;
            Assert.That(entry.ActiveRunner, Is.Null);
            Assert.That(entry.LobbyRoot.activeInHierarchy, Is.True);
            Assert.That(World.All.Count, Is.EqualTo(baseline));
            yield return Wait(entry.ShutdownAsync()); yield return null;
            Assert.That(resources.Outstanding, Is.Zero);
            Assert.That(World.All.Count, Is.EqualTo(baseline));
        }

        /// <summary>只对当前合法候选排序：低血优先治疗，其次补武器，再升级已有武器与被动。</summary>
        /// <param name="model">真实升级候选与玩家状态。</param><returns>当前候选索引；没有新增或修改候选。</returns>
        private static int SelectNaturalUpgrade(CombatRunModel model)
        {
            int selected = 0; double best = double.NegativeInfinity;
            for (int i = 0; i < model.CurrentChoices.Count; i++)
            {
                var option = model.CurrentChoices[i]; double score = 0;
                if (option.Type == cfg.EUpgradeOptionType.Heal) score = model.CurrentHealth < model.MaxHealth * .65 ? 100 : 0;
                if (option.Type == cfg.EUpgradeOptionType.RunWeapon) score = model.Build.WeaponRanks.ContainsKey(option.RunWeaponId.Value) ? 30 : 50;
                if (option.Type == cfg.EUpgradeOptionType.RunPassive) score = 20;
                if (score > best) { best = score; selected = i; }
            }
            return selected;
        }

        /// <summary>生成可复现的环行或折返方向，追逐可用雷坛并避开正式预警；只读单位位置与配置。</summary>
        /// <param name="runner">真实战斗运行器。</param><param name="role">输入策略索引。</param><param name="seekAltar">是否还需要取得首次炼器。</param>
        /// <param name="deliberateLure">是否根据自然敌人分布靠近敌群并延后闪避。</param>
        /// <returns>归一化移动方向；不写入玩家坐标。</returns>
        private static float2 NaturalMovement(CombatRuntimeRunner runner, int role, bool seekAltar, bool deliberateLure)
        {
            var trial = runner.Coordinator.Tribulation; var model = runner.RunModel;
            var player = runner.Session.ReadUnit(0); float scale = model.Definition.CombatRules.WorldUnitsPerPixel;
            var pos = player.Position / scale;
            double angle = trial.ElapsedMilli / 1000d * .6;
            float2 desired = role == 1 ? new float2((float)Math.Sin(angle * 2) * 320, (float)Math.Sin(angle * 4) * 160)
                : new float2((float)Math.Cos(angle) * 580, (float)Math.Sin(angle) * 420);
            var next = trial.Config.RuleId_Ref.Altars.Where(a => !trial.SpentAltars.Contains(a.Id)).OrderBy(a => a.AvailableAfterMilli).FirstOrDefault();
            if (seekAltar && trial.State != TribulationTrialState.Active && next != null && trial.ElapsedMilli >= next.AvailableAfterMilli)
                desired = new float2(next.XPixels, next.YPixels);
            if (deliberateLure && trial.State == TribulationTrialState.Active)
            {
                float2 center = float2.zero; int nearby = 0;
                for (int slot = 1; slot < runner.Session.UnitCount; slot++)
                {
                    var enemy = runner.Session.ReadUnit(slot);
                    if (enemy.Target.Health <= 0 || math.distance(enemy.Position / scale, pos) > 600) continue;
                    center += enemy.Position / scale; nearby++;
                }
                if (nearby > 0)
                {
                    center /= nearby;
                    desired = center + math.normalizesafe(pos - center, new float2(1, 0)) * 120;
                }
            }
            if (trial.Warnings.Count > 0)
            {
                // 只改变输入策略：先让自然怪物靠近锁点，再给角色足够的真实移动时间离开预警。
                if (deliberateLure && trial.State == TribulationTrialState.Active &&
                    trial.Warnings.All(w => w.HitAtMilli - trial.ElapsedMilli > 850))
                    return float2.zero;
                float best = float.NegativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    double heading = i * Math.PI / 4;
                    var candidate = pos + new float2((float)Math.Cos(heading), (float)Math.Sin(heading)) * 320;
                    float margin = (player.Radius + player.BodyHalfSegment) / scale;
                    var bounds = new float2(model.Definition.Map.ArenaHalfWidthPixels - margin, model.Definition.Map.ArenaHalfHeightPixels - margin);
                    candidate = math.clamp(candidate, -bounds, bounds);
                    float score = float.PositiveInfinity;
                    foreach (var warning in trial.Warnings)
                        score = math.min(score, math.distance(candidate + player.BodyOffset / scale, new float2((float)warning.XPixels, (float)warning.YPixels)) - (float)warning.RadiusPixels - margin);
                    if (score > best) { best = score; desired = candidate; }
                }
            }
            var delta = desired - pos;
            return math.length(delta) > 5 ? math.normalizesafe(delta) : float2.zero;
        }

        [Serializable] private sealed class NaturalProgress
        { public long time; public double health; public int kills, level, bossPhase, trialHits; public string trial; }

        [Serializable] private sealed class NaturalAcceptanceReport
        {
            public string role, result, deathSource;
            public int stageId, kills, level, goalEvents, requiredEvents, minimumTargets, bestTargets, counterEvents, detonationEvents, refinement;
            public long elapsedMilli, health, damage, incomingDamage;
            public bool deliberateLure, goalCompleted, saved;
            public double wallSeconds, simulatedSecondsPerWallSecond;
            public int[] unlocked, bossPhases, weapons;
            public string[] actions, sources;
        }
    }
}
