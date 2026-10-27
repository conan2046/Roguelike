using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Mathematics;

namespace Roguelike.Tests
{
    /// <summary>正式十二形态的实际 ECS 命中、命中预算、脉冲生命周期及六条进化前置回归。</summary>
    public sealed class CombatWeaponFormTests
    {
        /// <summary>逐条验证五星彩配对、稳定候选、重复领取拒绝和同槽技能替换；等级变化不自动进化。</summary>
        /// <param name="weaponId">TbRunWeapon 的本批武器。</param>
        [TestCase(50001)] [TestCase(50002)] [TestCase(50003)]
        [TestCase(50004)] [TestCase(50005)] [TestCase(50006)]
        public void EvolutionRequiresBothPrerequisitesAndReplacesOneSlot(int weaponId)
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Evolution gate " + weaponId);
            var build = new CombatBuildState(fixture.Definition.BuildConfig);
            RunWeaponConfig weapon = build.Config.WeaponIds_Ref.Single(item => item.Id == weaponId);
            WeaponEvolutionConfig evolution = weapon.EvolutionId_Ref;
            Assert.That(build.TryEvolve(evolution.Id), Is.False);
            while (build.CanUpgrade(weapon)) build.Upgrade(weapon);
            Assert.That(build.EligibleEvolutions(), Is.Empty, "Full star alone must not evolve.");
            build.Upgrade(evolution.PassiveId_Ref);
            Assert.That(build.EvolvedWeapons, Is.Empty, "Acquiring a passive must not claim a chest.");
            Assert.That(build.EligibleEvolutions().Select(item => item.Id), Does.Contain(evolution.Id));
            int slots = build.WeaponRanks.Count;
            Assert.That(build.TryEvolve(evolution.Id), Is.True);
            Assert.That(build.TryEvolve(evolution.Id), Is.False);
            Assert.That(build.WeaponRanks.Count, Is.EqualTo(slots));
            Assert.That(build.CaptureWeapons().Select(item => item.SkillId), Does.Contain(evolution.ResultSkillId));
            Assert.That(build.CaptureWeapons().Select(item => item.SkillId), Has.No.Member(weapon.Ranks.Last().SkillId));
            Assert.That(build.CanUpgrade(weapon), Is.False);
            Assert.That(build.WeaponDisplayName(weaponId), Is.EqualTo(evolution.Name));
            Assert.That(fixture.Definition.AvailableSkills.Select(item => item.Id), Does.Contain(evolution.ResultSkillId));
        }

        /// <summary>仅释放一次正式配置技能，逐 tick 验证伤害、扇射数量、穿透/连锁去重预算与持续区域脉冲并最终清场。</summary>
        /// <param name="weaponId">TbRunWeapon ID。</param>
        /// <param name="evolved">选择原形或 TbWeaponEvolution 结果形态。</param>
        [TestCase(50001, false)] [TestCase(50001, true)]
        [TestCase(50002, false)] [TestCase(50002, true)]
        [TestCase(50003, false)] [TestCase(50003, true)]
        [TestCase(50004, false)] [TestCase(50004, true)]
        [TestCase(50005, false)] [TestCase(50005, true)]
        [TestCase(50006, false)] [TestCase(50006, true)]
        public void EveryFormDealsRealDamageAndHonorsHitAndPulseBudgets(int weaponId, bool evolved)
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Weapon form " + weaponId + " " + evolved);
            RunWeaponConfig weapon = fixture.Definition.BuildConfig.WeaponIds_Ref.Single(item => item.Id == weaponId);
            SkillConfig skill = evolved ? weapon.EvolutionId_Ref.ResultSkillId_Ref : weapon.Ranks[0].SkillId_Ref;
            SkillCombatConfig combat = skill.CombatProfileId_Ref;
            fixture.Session.SynchronizePlayerSkills(new[] { skill.Id });
            var upgrade = new CombatWeaponUpgrade(evolved ? weapon.Ranks.Last() : weapon.Ranks[0],
                fixture.Definition.BuildConfig.WeaponDamagePermille, evolved ? (int?)skill.Id : null);
            fixture.Session.ApplyWeaponUpgrades(new[] { upgrade }, fixture.Model.AttributeSnapshot);
            int[] slots = PrepareTargets(fixture);
            var contacts = new HashSet<(ulong, ulong)>();
            var hitCounts = new Dictionary<ulong, int>();
            int pulses = 0;
            var centers = new HashSet<float2>();
            var radii = new HashSet<float>();
            double duration = Math.Max(combat.ProjectileLifetimeMilli.GetValueOrDefault(), combat.AreaDurationMilli) / 1000d;
            int ticks = (int)Math.Ceiling(duration / fixture.Session.StepSeconds) + 3;
            for (int tick = 0; tick < ticks; tick++)
            {
                fixture.Session.Advance(fixture.Session.StepSeconds, float2.zero);
                if (tick == 0)
                {
                    Assert.That(fixture.Session.Statistics.Attacks, Is.EqualTo(1));
                    Assert.That(fixture.Session.Statistics.ProjectileSpawns, Is.EqualTo(combat.ProjectileCount));
                    fixture.Session.SynchronizePlayerSkills(Array.Empty<int>());
                }
                pulses += fixture.Session.AreaCount;
                for (int index = 0; index < fixture.Session.AreaCount; index++)
                { CombatAreaEvent area = fixture.Session.ReadArea(index); centers.Add(area.Position); radii.Add(area.Radius); }
                for (int index = 0; index < fixture.Session.ImpactCount; index++)
                {
                    CombatImpactEvent impact = fixture.Session.ReadImpact(index);
                    Assert.That(contacts.Add((impact.AttackSequence, impact.TargetLifetime)), Is.True, "One projectile hit a lifetime twice.");
                    hitCounts[impact.AttackSequence] = hitCounts.TryGetValue(impact.AttackSequence, out int count) ? count + 1 : 1;
                }
            }
            Assert.That(fixture.Session.Statistics.DamageEvents, Is.GreaterThan(0), "ANI playback alone cannot pass a form.");
            Assert.That(slots.Any(slot => fixture.Session.ReadUnit(slot).Target.Health < 1000000), Is.True);
            if (combat.DeliveryType == ESkillDeliveryType.Projectile)
            {
                Assert.That(hitCounts.Values.All(count => count <= 1 + combat.PierceCount + combat.ChainCount), Is.True);
                if (combat.PierceCount > 0 || combat.ChainCount > 0)
                    Assert.That(hitCounts.Values.Max(), Is.GreaterThan(1), "Configured multi-target behavior never triggered.");
            }
            else
                Assert.That(pulses, Is.EqualTo((combat.AreaDurationMilli == 0 ? 1 :
                    (int)Math.Ceiling(combat.AreaDurationMilli / (double)combat.AreaPulseIntervalMilli)) * Math.Max(1, combat.AreaRingCount)));
            if (combat.AreaRetargets) Assert.That(centers.Count, Is.GreaterThan(1), "Continuous lightning reused one target center.");
            if (combat.AreaRingCount > 0) Assert.That(radii.Count, Is.EqualTo(combat.AreaRingCount), "Multiple rings were not emitted.");
            if (combat.ImpactRadiusPixels > 0)
            {
                Assert.That(fixture.Session.Statistics.SplashCasts, Is.GreaterThan(0), "Explosion never triggered.");
                Assert.That(slots.Count(slot => fixture.Session.ReadUnit(slot).Target.Health < 1000000), Is.GreaterThan(1));
            }
            Assert.That(fixture.Session.Statistics.ActiveProjectiles, Is.Zero);
            fixture.Session.Restart();
            fixture.Session.Advance(fixture.Session.StepSeconds, float2.zero);
            Assert.That(fixture.Session.AreaCount, Is.Zero, "Old persistent area survived restart.");
            Assert.That(fixture.Session.Statistics.DamageEvents, Is.Zero);
        }

        /// <summary>进化更换预加载技能时转移冷却比例，原弹丸继续持有旧快照，下一次释放采用结果技能。</summary>
        [Test]
        public void EvolutionTransfersCooldownAndKeepsInFlightSnapshot()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Evolution native cooldown");
            RunWeaponConfig weapon = fixture.Definition.BuildConfig.InitialWeaponIds_Ref[0];
            var build = new CombatBuildState(fixture.Definition.BuildConfig);
            while (build.CanUpgrade(weapon)) build.Upgrade(weapon);
            build.Upgrade(weapon.EvolutionId_Ref.PassiveId_Ref);
            fixture.Session.ApplyWeaponUpgrades(build.CaptureWeapons(), fixture.Model.AttributeSnapshot);
            PrepareTargets(fixture);
            fixture.Session.Advance(fixture.Session.StepSeconds, float2.zero);
            CombatWeaponState original = ReadWeapon(fixture, weapon.Ranks.Last().SkillId);
            int inFlight = Enumerable.Range(0, fixture.Session.ProjectileCapacity).First(index => fixture.Session.ReadProjectile(index).Active);
            CombatProjectile projectile = fixture.Session.ReadProjectile(inFlight);
            Assert.That(build.TryEvolve(weapon.EvolutionId), Is.True);
            fixture.Session.SynchronizePlayerSkills(build.CaptureWeapons().Select(item => item.SkillId), build.CaptureWeapons());
            fixture.Session.ApplyWeaponUpgrades(build.CaptureWeapons(), fixture.Model.AttributeSnapshot);
            CombatWeaponState result = ReadWeapon(fixture, weapon.EvolutionId_Ref.ResultSkillId);
            Assert.That(result.Cooldown / result.Interval, Is.EqualTo(original.Cooldown / original.Interval).Within(1e-9));
            Assert.That(ReadWeapon(fixture, original.SkillId).Active, Is.False);
            Assert.That(result.Active, Is.True);
            Assert.That(fixture.Session.ReadProjectile(inFlight).Attack.Attack, Is.EqualTo(projectile.Attack.Attack));
            Assert.That(fixture.Session.ReadProjectile(inFlight).SkillId, Is.EqualTo(original.SkillId));
        }

        /// <summary>已出生区域在拥有者移动后保持固定落点或跟随中心；暂停期间不推进脉冲，重开清空旧代次。</summary>
        /// <param name="weaponId">固定寒阵或随身罡环。</param>
        [TestCase(50004)] [TestCase(50005)]
        public void PersistentAreaAnchorAndPauseFollowConfiguration(int weaponId)
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Area ownership " + weaponId);
            SkillConfig skill = fixture.Definition.BuildConfig.WeaponIds_Ref.Single(item => item.Id == weaponId).Ranks[0].SkillId_Ref;
            fixture.Session.SynchronizePlayerSkills(new[] { skill.Id });
            PrepareTargets(fixture);
            fixture.Session.Advance(fixture.Session.StepSeconds, float2.zero);
            CombatAreaEvent initial = fixture.Session.ReadArea(0);
            fixture.Session.SynchronizePlayerSkills(Array.Empty<int>());
            CombatUnit player = fixture.Session.ReadUnit(0);
            player.Position += new float2(0, 3);
            player.PreviousPosition = player.Position;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(0), player);
            fixture.Session.Paused = true;
            ulong tick = fixture.Session.Statistics.Tick;
            fixture.Session.Advance(2, float2.zero);
            Assert.That(fixture.Session.Statistics.Tick, Is.EqualTo(tick));
            fixture.Session.Paused = false;
            int bound = skill.CombatProfileId_Ref.AreaPulseIntervalMilli * fixture.Definition.CombatRules.SimulationHz / 1000 + 2;
            CombatAreaEvent next = default;
            bool sawPulse = false;
            for (int index = 0; index < bound && !sawPulse; index++)
            {
                fixture.Session.Advance(fixture.Session.StepSeconds, float2.zero);
                if (fixture.Session.AreaCount > 0) { next = fixture.Session.ReadArea(0); sawPulse = true; }
            }
            Assert.That(sawPulse, Is.True);
            float2 expected = skill.CombatProfileId_Ref.AreaFollowsOwner ? player.Position + player.BodyOffset : initial.Position;
            Assert.That(math.distance(next.Position, expected), Is.LessThan(1e-5));
            Assert.That(next.OwnerLifetime, Is.EqualTo(player.Target.Lifetime));
        }

        /// <summary>建立耐久静止目标，关闭闪避和主动攻击，使结论来自真实几何与伤害而非随机死亡。</summary>
        /// <param name="fixture">本测试拥有的正式配置与会话。</param>
        /// <returns>按距离排序的目标槽。</returns>
        /// <remarks>仅写入隔离测试 World；不修改正式表、Scene 或 Prefab。</remarks>
        private static int[] PrepareTargets(CombatRunCoordinatorTests.CoordinatorFixture fixture)
        {
            var slots = new int[6];
            for (int index = 0; index < slots.Length; index++)
            {
                Assert.That(fixture.Session.TrySpawnMonster(fixture.Definition.Monsters[0],
                    fixture.Definition.Map.SpawnRadiusPixels, (ulong)(index + 1), false, out slots[index]), Is.True);
                CombatUnit monster = fixture.Session.ReadUnit(slots[index]);
                monster.Position = new float2(0.8f + index * 0.65f, 0);
                monster.PreviousPosition = monster.Position;
                monster.MoveSpeed = 0;
                monster.Cooldown = 100000;
                monster.Target.Health = monster.Target.MaxHealth = 1000000;
                monster.Target.Evasion = 0;
                fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(slots[index]), monster);
            }
            return slots;
        }

        /// <summary>按正式技能身份读取其独立原生槽，避免测试依赖预加载排序。</summary>
        /// <param name="fixture">当前测试会话。</param>
        /// <param name="skillId">TbSkill.id。</param>
        /// <returns>唯一匹配的武器快照。</returns>
        private static CombatWeaponState ReadWeapon(CombatRunCoordinatorTests.CoordinatorFixture fixture, int skillId) =>
            Enumerable.Range(0, fixture.Session.PlayerWeaponCount).Select(fixture.Session.ReadPlayerWeapon).Single(item => item.SkillId == skillId);
    }
}
