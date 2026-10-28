using System;
using System.IO;
using System.Linq;
using System.Threading;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>真实配置与原生会话验证职业修饰收益、唯一事件及清阵边界；不接触玩家存档。</summary>
    public sealed class CombatBuildFeedbackTests
    {
        /// <summary>过量伤害只计实际生命损失，同事件重复回报无效；承伤与环境伤害不混入玩家输出。</summary>
        [Test]
        public void DamageLedgerClampsOverkillAndNeverDoubleCounts()
        {
            var tables=Tables(); var ledger=new CombatBuildFeedback(tables.TbContentGoal.Get(62001));
            var target=new DamageTarget { Lifetime=2,Health=5,MaxHealth=5,Faction=1,DefenseParameter=1 };
            var attack=new AttackSnapshot { AttackerLifetime=1,Attack=1000,Hit=100,Source=CombatDamageSource.Weapon };
            var result=CombatDamage.Resolve(attack,ref target,2,true,0,1,DamageRules.Capture(tables.TbCombatRules.DataList.First()));
            Assert.That(result.HealthLost,Is.EqualTo(5));
            var damage=new CombatLifeDamageEvent { EventId=1,RootCastId=1,OwnerId=1,TargetId=2,SkillId=20001,HealthLost=result.HealthLost,Source=CombatDamageSource.Weapon };
            Assert.That(ledger.RecordDamage(damage,1),Is.True);Assert.That(ledger.RecordDamage(damage,1),Is.False);
            damage.EventId=2;damage.Source=CombatDamageSource.Counter;damage.RangeBonusTarget=true;damage.RewardEligible=true;damage.HealthLost=7;ledger.RecordDamage(damage,1);
            damage.EventId=3;damage.Source=CombatDamageSource.Environment;damage.HealthLost=100;ledger.RecordDamage(damage,1);
            damage.EventId=4;damage.PlayerTarget=true;damage.HealthLost=9;ledger.RecordDamage(damage,1);
            ledger.AdvanceClock(2000);
            Assert.That(ledger.TotalDamage,Is.EqualTo(12));Assert.That(ledger.DamageBySource.Values.Sum(),Is.EqualTo(ledger.TotalDamage));
            Assert.That(ledger.IncomingDamage,Is.EqualTo(9));Assert.That(ledger.Dps,Is.EqualTo(6));
            Assert.That(ledger.RecordMechanismCast(20,CombatDamageSource.Counter,3,true),Is.False);
            ledger.RecordMechanismCast(20,CombatDamageSource.Counter,99,true);
            Assert.That(ledger.GoalEvents,Is.EqualTo(1));Assert.That(ledger.GoalCompleted(false),Is.False);Assert.That(ledger.GoalCompleted(true),Is.True);
        }

        /// <summary>同一步待提交的重叠引爆被Boss清阵取消；过期、清阵都不伪造有效引爆。</summary>
        [Test]
        public void BossClearCancelsPendingDetonationBeforeDamageCommit()
        {
            var config=Tables().TbTribulationSignature.Get(102);var domains=new CombatTribulationDomains(config);
            domains.Advance(1,0,0);domains.Advance(16,config.DomainStepPixels,0);
            domains.Advance(16,config.DomainStepPixels*2,0);domains.Advance(16,config.DomainStepPixels,0);
            var explosion=domains.Pulses.Single(p=>p.Explosion);
            Assert.That(explosion.Participants.Select(p=>p.Id).Distinct().Count(),Is.EqualTo(config.OverlapCount));
            domains.ClearIntersecting(explosion.XPixels,explosion.YPixels,config.DomainRadiusPixels);
            Assert.That(domains.Pulses.Any(p=>p.Explosion),Is.False);
            Assert.That(domains.Terminations.Values,Has.Member(DomainTerminationReason.BossClear));
            Assert.That(domains.ExplosionCount,Is.Zero);
        }

        /// <summary>环境和自伤不产生新卡／目标收益；免伤、零伤与致死不计蓄能，基准铁壁触发仍保持原规则。</summary>
        [Test]
        public void NonEnemyDamageCannotFarmModifierOrChallengeRewards()
        {
            var tables=Tables();var config=tables.TbTribulationSignature.Get(101);var rules=DamageRules.Capture(tables.TbCombatRules.DataList.First());
            rules.PlayerInvulnerability=0;
            var target=new DamageTarget { Lifetime=2,Faction=1,Health=1000000,MaxHealth=1000000,DefenseParameter=1,IsPlayer=true,Guard=CombatGuardState.Capture(config) };
            var attack=new AttackSnapshot { AttackerLifetime=1,Faction=3,Attack=100,Hit=100,Source=CombatDamageSource.Environment,SuppressMechanismRewards=true };
            for(int i=0;i<config.GuardChargeHits;i++)CombatDamage.Resolve(attack,ref target,2,true,i,1,rules);
            Assert.That(target.Guard.Activations,Is.EqualTo(1));Assert.That(target.Guard.ActivationRewardEligible,Is.False);
            target.Guard=CombatGuardState.Capture(config);target.InvulnerableUntil=0;target.ImmunityCharges=1;
            attack.Source=CombatDamageSource.Enemy;attack.SuppressMechanismRewards=false;
            CombatDamage.Resolve(attack,ref target,2,true,10,1,rules);Assert.That(target.Guard.Charge,Is.Zero);
            attack.Attack=0;CombatDamage.Resolve(attack,ref target,2,true,11,1,rules);Assert.That(target.Guard.Charge,Is.Zero);
            attack.Attack=100;target.Health=1;CombatDamage.Resolve(attack,ref target,2,true,12,1,rules);Assert.That(target.Guard.Charge,Is.Zero);
            var feedback=new CombatBuildFeedback(tables.TbContentGoal.Get(62001));
            feedback.RecordMechanismCast(1,CombatDamageSource.Counter,config.GuardChargeHits,false);
            Assert.That(feedback.EffectiveCounters,Is.EqualTo(1),"Actual damage remains observable even when its origin cannot earn content progress.");
            Assert.That(feedback.GoalEvents,Is.Zero);
        }

        /// <summary>真实原生反击扩大后命中基础范围外目标，单段扣血、来源和画面半径一致。</summary>
        /// <remarks>仅测试布置目标及已达蓄能，不作为自然游玩证据；World和会话均using释放。</remarks>
        [Test]
        public void CounterModifierHitsAdditionalTargetAndFormsBuild()
        {
            var tables=Tables();var definition=CombatRunDefinition.Create(tables,101);
            using var world=new World("P0 counter modifier");
            using var session=CombatSessionFactory.CreateAsync(world,definition,new CombatVisualTests.Files(tables),CancellationToken.None).GetAwaiter().GetResult();
            var model=new CombatRunModel(definition,new[]{definition.DraftPolicy.GoalId});var coordinator=new CombatRunCoordinator(model,session);
            SelectModifier(coordinator,ETribulationSignature.Guard);
            Assert.That(session.TrySpawnMonster(definition.Monsters.First(),definition.Map.SpawnRadiusPixels,1,false,out int slot),Is.True);
            float scale=definition.CombatRules.WorldUnitsPerPixel;float baseline=definition.Tribulation.SignatureId_Ref.CounterRadiusPixels;
            Place(world,session,slot,new float2(baseline*1.05f*scale,0));
            var player=session.ReadUnit(0);for(int i=0;i<player.Target.Guard.ChargeHits;i++)player.Target.Guard.RecordEffectiveHit(session.Statistics.Tick*session.StepSeconds,true);
            world.EntityManager.SetComponentData(session.UnitEntity(0),player);
            coordinator.Advance(session.StepSeconds,float2.zero);
            Assert.That(coordinator.CounterRadiusPixels,Is.GreaterThan(baseline));
            Assert.That(coordinator.BuildFeedback.CounterBonusTargets,Is.EqualTo(1));
            Assert.That(model.BuildFormed,Is.True);
            var events=Enumerable.Range(0,session.LifeDamageCount).Select(session.ReadLifeDamage).Where(e=>e.Source==CombatDamageSource.Counter).ToArray();
            Assert.That(events.Length,Is.EqualTo(1));Assert.That(events[0].RangeBonusTarget,Is.True);
            Assert.That(session.ReadArea(session.AreaCount-1).Radius,Is.EqualTo(coordinator.CounterRadiusPixels*scale).Within(.0001f));
        }

        /// <summary>实际移动留阵折返命中后触发交织；暂停冻结时长，到期恢复当前属性移速。</summary>
        /// <remarks>只布置测试World中的权威位置与高生命目标，资源与配置不修改。</remarks>
        [Test]
        public void EffectiveDetonationRefreshesWeaveAndPauseFreezesDuration()
        {
            var tables=Tables();var definition=CombatRunDefinition.Create(tables,102);
            using var world=new World("P0 weave modifier");
            using var session=CombatSessionFactory.CreateAsync(world,definition,new CombatVisualTests.Files(tables),CancellationToken.None).GetAwaiter().GetResult();
            var model=new CombatRunModel(definition,new[]{definition.DraftPolicy.GoalId});var coordinator=new CombatRunCoordinator(model,session);
            SelectModifier(coordinator,ETribulationSignature.Domains);
            var signature=definition.Tribulation.SignatureId_Ref;float scale=definition.CombatRules.WorldUnitsPerPixel;
            Assert.That(session.TrySpawnMonster(definition.Monsters.First(),definition.Map.SpawnRadiusPixels,1,false,out int slot),Is.True);
            Place(world,session,slot,new float2(signature.DomainStepPixels*scale,0));
            // 使用跨过阈值的路线，避免将浮点世界坐标的单ULP边缘误差当作留阵规则失败。
            foreach(float x in new[]{signature.DomainStepPixels*1.1f,signature.DomainStepPixels*2.1f,0f})
            { var player=session.ReadUnit(0);player.Position=player.PreviousPosition=new float2(x*scale,0);world.EntityManager.SetComponentData(session.UnitEntity(0),player);coordinator.Advance(session.StepSeconds,float2.zero); }
            Assert.That(coordinator.Domains.ExplosionCount,Is.EqualTo(1),"The authoritative route must actually cross the leave-domain threshold.");
            Assert.That(coordinator.BuildFeedback.EffectiveDetonations,Is.EqualTo(1));Assert.That(coordinator.WeaveRemainingSeconds,Is.GreaterThan(0));
            coordinator.Advance(session.StepSeconds,float2.zero);
            var modifier=model.SelectedMechanism(ETribulationSignature.Domains);
            Assert.That(session.ReadUnit(0).MoveSpeed,Is.EqualTo(model.AttributeSnapshot.MoveSpeed*scale*modifier.MoveSpeedPermille/1000d).Within(.0001));
            double remaining=coordinator.WeaveRemainingSeconds;coordinator.TogglePause();coordinator.Advance(10,float2.zero);
            Assert.That(coordinator.WeaveRemainingSeconds,Is.EqualTo(remaining));coordinator.TogglePause();
            int ticks=(int)Math.Ceiling(modifier.DurationMilli/1000d/session.StepSeconds)+2;
            for(int i=0;i<ticks;i++)coordinator.Advance(session.StepSeconds,float2.zero);
            Assert.That(coordinator.WeaveRemainingSeconds,Is.Zero);
            Assert.That(session.ReadUnit(0).MoveSpeed,Is.EqualTo(model.AttributeSnapshot.MoveSpeed*scale).Within(.0001));
            Assert.That(model.BuildFormed,Is.True);
        }

        /// <summary>批量追帧跨过交织截止时只在合法Tick加速，运动与逐Tick推进相同，不延长增益窗口。</summary>
        /// <remarks>使用两个独占World和正式会话，不创建表现或存档；四Tick只用于边界测试。</remarks>
        [Test]
        public void MovementBuffExpiryIsIndependentOfCatchUpBatching()
        {
            var tables=Tables();var definition=CombatRunDefinition.Create(tables,102);
            using var firstWorld=new World("P0 movement batching");using var secondWorld=new World("P0 movement individual ticks");
            using var first=CombatSessionFactory.CreateAsync(firstWorld,definition,new CombatVisualTests.Files(tables),CancellationToken.None).GetAwaiter().GetResult();
            using var second=CombatSessionFactory.CreateAsync(secondWorld,definition,new CombatVisualTests.Files(tables),CancellationToken.None).GetAwaiter().GetResult();
            double speed=new CombatRunModel(definition).AttributeSnapshot.MoveSpeed;
            double multiplier=definition.MechanismCards.Values.Single(c=>c.SignatureKind==ETribulationSignature.Domains).MoveSpeedPermille/1000d;
            first.SetFeedbackMoveSpeed(first.ReadUnit(0).Target.Lifetime,speed,multiplier,2);
            second.SetFeedbackMoveSpeed(second.ReadUnit(0).Target.Lifetime,speed,multiplier,2);
            var start=first.ReadUnit(0).Position;
            first.Advance(first.StepSeconds*(4+1e-6),new float2(1,0));
            for(int i=0;i<4 && first.Statistics.Tick<4;i++)first.Advance(0,new float2(1,0));
            Assert.That(first.Statistics.Tick,Is.EqualTo(4),"The batch must finish exactly four simulation ticks.");
            for(int i=0;i<4;i++)second.Advance(second.StepSeconds,new float2(1,0));
            Assert.That(math.distance(first.ReadUnit(0).Position,second.ReadUnit(0).Position),Is.LessThan(.00001f));
            double expected=speed*definition.CombatRules.WorldUnitsPerPixel*first.StepSeconds*(2*multiplier+2);
            Assert.That(first.ReadUnit(0).Position.x-start.x,Is.EqualTo(expected).Within(.00001));
        }

        /// <summary>受控经验仍走正式候选与选择，直到取得本职业修饰，不直接修改等级或选卡字典。</summary>
        /// <param name="coordinator">测试协调器。</param><param name="kind">本次机制。</param>
        private static void SelectModifier(CombatRunCoordinator coordinator,ETribulationSignature kind)
        {
            var model=coordinator.Model;var definition=model.Definition;
            for(int i=0;i<definition.ExperienceLevels.Count && model.SelectedMechanism(kind)==null;i++)
            {
                model.GrantExperience(definition.ExperienceLevels[model.Level-1].RequiredExperience-model.Experience);
                while(model.State==CombatRunState.UpgradeChoice)
                {
                    var card=model.CurrentChoices.FirstOrDefault(o=>o.Type==EUpgradeOptionType.MechanismModifier);
                    coordinator.ChooseUpgrade(model.UpgradePanelGeneration,(card??model.CurrentChoices.First()).Id);
                }
            }
            Assert.That(model.SelectedMechanism(kind),Is.Not.Null);
        }

        /// <summary>只在测试World布置不移动的高生命目标，隔离职业几何收益。</summary>
        /// <param name="world">测试World。</param><param name="session">会话。</param><param name="slot">目标槽。</param><param name="position">测试位置。</param>
        private static void Place(World world,CombatSession session,int slot,float2 position)
        {
            var unit=session.ReadUnit(slot);unit.Position=unit.PreviousPosition=position;unit.BodyOffset=float2.zero;unit.BodyHalfSegment=0;unit.Radius=0;
            unit.Target.Health=unit.Target.MaxHealth=1000000;unit.Target.Evasion=0;unit.MoveSpeed=0;
            world.EntityManager.SetComponentData(session.UnitEntity(slot),unit);
        }

        /// <summary>读取正式生成bytes，不使用存档或业务兜底配置。</summary><returns>独立表容器。</returns>
        private static Tables Tables()=>new Tables(name=>new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,"Config/Luban",name+".bytes"))));
    }
}
