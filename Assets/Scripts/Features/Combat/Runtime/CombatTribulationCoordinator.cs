using System;
using cfg;
using Roguelike.Features.Combat.Run;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class CombatRunCoordinator
    {
        private ulong consumedGuardActivations;
        public CombatTribulationModel Tribulation { get; private set; }
        public CombatTribulationDomains Domains { get; private set; }
        public bool TribulationBlocksSimulation => Tribulation?.State == TribulationTrialState.RewardPending;

        /// <summary>每个正式新局创建独立切片代次；原关卡没有slice引用时保持关闭。</summary>
        /// <remarks>终止旧预警/领奖，重建职业状态；TbTribulationSlice.signatureId决定启用铁壁或留阵。</remarks>
        /// <exception cref="InvalidOperationException">TbTribulationRule.environmentFaction与玩家或怪物阵营冲突。</exception>
        private void InitializeTribulation()
        {
            Tribulation?.Terminate(); Domains?.Clear(); Domains = null; consumedGuardActivations = 0;
            var config = model.Definition.Tribulation;
            if (config == null) return;
            if (config.RuleId_Ref.EnvironmentFaction == (int)Ecs.CombatFaction.Player || config.RuleId_Ref.EnvironmentFaction == (int)Ecs.CombatFaction.Monster)
                throw new InvalidOperationException("TbTribulationRule.environmentFaction must differ from both combat sides.");
            Tribulation = new CombatTribulationModel(config, model.RunGeneration);
            if (config.SignatureId_Ref.Kind == ETribulationSignature.Guard) session.ConfigureTribulationGuard(config.SignatureId_Ref);
            else
            {
                Domains = new CombatTribulationDomains(config.SignatureId_Ref);
                var player = session.ReadUnit(0); float scale = model.Definition.CombatRules.WorldUnitsPerPixel;
                Domains.Advance(0, player.Position.x / scale, player.Position.y / scale);
            }
        }

        /// <summary>雷坛按钮只接受当前单局代次、真实玩家位置与有效战斗状态。</summary>
        /// <param name="runGeneration">按钮绑定时的单局代次。</param><param name="altarId">TbTribulationRule.altars中的ID。</param>
        /// <returns>确实消耗本坛并开始引劫时为真。</returns>
        public bool TryStartTribulation(ulong runGeneration, int altarId)
        {
            if (Tribulation == null || Paused || !IsSimulationActive(model.State) || session.Statistics.PlayerDead) return false;
            var player = session.ReadUnit(0); float scale = model.Definition.CombatRules.WorldUnitsPerPixel;
            return Tribulation.TryStart(runGeneration, altarId, player.Position.x / scale, player.Position.y / scale);
        }

        /// <summary>真实领奖后同步法器投递和恢复量，拒绝旧面板、重复回调及被其他正式面板冻结的选择。</summary>
        /// <param name="runGeneration">单局代次。</param><param name="rewardGeneration">试炼奖励代次。</param><param name="refinementId">本职业TbTribulationRefinement候选ID。</param>
        /// <returns>领取、构筑及ECS同步全部成功时为真。</returns>
        /// <remarks>修改当前本命法器技能、属性快照和生命；不改其他法器、不新增槽位。</remarks>
        /// <exception cref="InvalidOperationException">校验通过后正式模型或玩家生命周期异常变化。</exception>
        public bool ChooseRefinement(ulong runGeneration, ulong rewardGeneration, int refinementId)
        {
            if (Tribulation == null || Paused || !IsSimulationActive(model.State) || session.Statistics.PlayerDead ||
                !Tribulation.TryClaim(runGeneration, rewardGeneration, refinementId, out var refinement)) return false;
            if (!model.ApplyTribulationRefinement(refinement)) throw new InvalidOperationException("Validated refinement could not be applied.");
            var player = session.ReadUnit(0);
            if (!session.SynchronizeRunPlayer(player.Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth))
                throw new InvalidOperationException("Player lifetime changed during refinement.");
            session.SynchronizePlayerSkills(model.ActiveSkills.Keys, model.Build.CaptureWeapons());
            session.ApplyWeaponUpgrades(model.Build.CaptureWeapons(), model.AttributeSnapshot);
            session.Paused = Paused || !IsSimulationActive(model.State) || TribulationBlocksSimulation;
            return true;
        }

        /// <summary>真实固定步结束后，投递职业伤害与已到期落雷；所有死亡交给同帧ConsumeCombatEvents处理一次。</summary>
        /// <param name="milliseconds">实际固定tick换算后的毫秒，暂停不调用。</param>
        /// <remarks>范围、时限、倍率、环境阵营和表现引用全部消费TbTribulationRule/Signature；先结算实际命中再判断试炼截止。</remarks>
        private void AdvanceTribulation(int milliseconds)
        {
            if (Tribulation == null || milliseconds <= 0 || session.Statistics.PlayerDead) return;
            var slice = Tribulation.Config; var rule = slice.RuleId_Ref; var signature = slice.SignatureId_Ref;
            var player = session.ReadUnit(0); float scale = model.Definition.CombatRules.WorldUnitsPerPixel;
            double x = player.Position.x / scale, y = player.Position.y / scale;
            int? bossHealth = null;
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                if (unit.IsBoss && unit.Target.Health > 0) bossHealth = (int)(unit.Target.Health * 1000d / unit.Target.MaxHealth);
            }
            Tribulation.Advance(milliseconds, x, y, bossHealth);
            if (Domains != null)
            {
                Domains.Advance(milliseconds, x, y);
                foreach (var pulse in Domains.Pulses)
                    session.ApplyConfiguredArea(new float2((float)pulse.XPixels, (float)pulse.YPixels) * scale,
                        (float)pulse.RadiusPixels * scale, player.Attack, 0, pulse.DamagePermille, pulse.DamagePermille, signature.DomainSkillId_Ref);
            }
            foreach (var strike in Tribulation.DueStrikes)
            {
                if (session.Statistics.PlayerDead) break;
                var source = AttackSnapshot.Capture(new CombatAttributes(rule.AttackProfileId_Ref), player.Target.Lifetime, 0, rule.EnvironmentFaction);
                var result = session.ApplyConfiguredArea(new float2((float)strike.XPixels, (float)strike.YPixels) * scale,
                    (float)strike.RadiusPixels * scale, source, rule.PlayerDamagePermille, rule.EnemyDamagePermille, rule.BossDamagePermille, rule.EffectSkillId_Ref);
                Tribulation.RecordEnemyHits(strike, result.EnemyHits);
                if (strike.ClearsDomains) Domains?.ClearIntersecting(strike.XPixels, strike.YPixels, strike.RadiusPixels);
            }
            player = session.ReadUnit(0);
            if (signature.Kind == ETribulationSignature.Guard && player.Target.Health > 0)
            {
                while (consumedGuardActivations < player.Target.Guard.Activations)
                {
                    session.ApplyConfiguredArea(player.Position, signature.CounterRadiusPixels * scale, player.Attack,
                        0, signature.CounterDamagePermille, signature.CounterDamagePermille, signature.CounterSkillId_Ref);
                    consumedGuardActivations++;
                }
            }
            if (!session.Statistics.PlayerDead) Tribulation.ResolveTrialDeadline();
        }

        /// <summary>退出或正式结算时终止旧代次的雷击、阵域及领奖；重复调用安全。</summary>
        /// <remarks>只清理纯状态，World和正式资源仍由运行器所有者释放。</remarks>
        public void TerminateTribulation() { Tribulation?.Terminate(); Domains?.Clear(); }
    }
}
