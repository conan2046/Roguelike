using System;
using cfg;
using Roguelike.Features.Combat;

/// <summary>Exercises production combat code with real table bytes and explicitly test-only boundary fixtures.</summary>
internal static class RuntimeChecks
{
    private static int count;

    /// <summary>Runs attribute, lifecycle, formula, immunity and deterministic sampling acceptance cases.</summary>
    /// <param name="tables">Generated tables loaded by the configuration checker.</param>
    /// <returns>Number of passing runtime assertions.</returns>
    /// <exception cref="InvalidOperationException">A production behavior differs from the approved rule.</exception>
    internal static int Run(Tables tables)
    {
        count = 0;
        var attributes = new CombatAttributes(tables.TbAttributeProfile.Get(1));
        var rules = DamageRules.Capture(tables.TbCombatRules.Get(1));
        Assert(attributes.Get(EAttributeType.Attack) == 20, "Profile base attack");
        var modifier = new AttributeModifier { Attribute = EAttributeType.Attack, Flat = 10, PercentBp = 5000 };
        attributes.Replace(10, modifier);
        Assert(attributes.Get(EAttributeType.Attack) == 45, "Flat before percentage");
        attributes.Replace(10, modifier);
        Assert(attributes.Get(EAttributeType.Attack) == 45, "Source replacement does not stack");
        attributes.Replace(20, new AttributeModifier { Attribute = EAttributeType.Attack, PercentBp = 5000 });
        Assert(attributes.Get(EAttributeType.Attack) == 60, "Percentages add");
        Assert(attributes.Remove(10) && attributes.Get(EAttributeType.Attack) == 30, "Source removal recomputes");
        Assert(attributes.Remove(20) && !attributes.Remove(20) && attributes.Get(EAttributeType.Attack) == 20, "Base restored");
        attributes.Replace(10, new AttributeModifier { Attribute = EAttributeType.Attack, Flat = 0.9 });
        Assert(attributes.Get(EAttributeType.Attack) == 20, "Integer floor");
        Reject(() => attributes.Replace(10, new AttributeModifier { Attribute = EAttributeType.Attack, Flat = double.NaN }), "NaN modifier");
        Reject(() => attributes.Replace(10, new AttributeModifier { Attribute = EAttributeType.Attack, Flat = double.MaxValue, PercentBp = int.MaxValue }), "Overflow modifier");
        Assert(attributes.Get(EAttributeType.Attack) == 20, "Failed replace is atomic");
        attributes.Remove(10);
        attributes.Replace(10, new AttributeModifier { Attribute = EAttributeType.Attack, Flat = -1000 });
        Assert(attributes.Get(EAttributeType.Attack) == 0, "Lower clamp");
        attributes.Replace(10, new AttributeModifier { Attribute = EAttributeType.Attack, Flat = 1e9 });
        Assert(attributes.Get(EAttributeType.Attack) == tables.TbAttribute.Get(2).MaxValue, "Upper clamp");
        attributes.Remove(10);

        var target = DamageTarget.Spawn(attributes, 2, 2, true);
        target.Health = 60;
        attributes.Replace(30, new AttributeModifier { Attribute = EAttributeType.MaxHealth, Flat = 100 });
        target.Synchronize(attributes);
        Assert(target.MaxHealth == 200 && target.Health == 60, "Max health increase cannot heal");
        attributes.Replace(30, new AttributeModifier { Attribute = EAttributeType.MaxHealth, Flat = -50 });
        target.Synchronize(attributes);
        Assert(target.MaxHealth == 50 && target.Health == 50, "Max health decrease clamps");
        target.Health = 0;
        attributes.Remove(30);
        target.Synchronize(attributes);
        Assert(target.Health == 0 && target.Heal(100) == 0, "No implicit resurrection");
        target = DamageTarget.Spawn(attributes, 2, 2, true);
        target.Health = 99;
        Assert(target.Heal(long.MaxValue) == 1 && target.Health == 100, "Healing avoids overflow");

        Assert(CombatMath.RescaleCooldown(0.25, 0.5, 1) == 0.5, "Cooldown fraction preserved");
        Assert(CombatMath.RescaleCooldown(0, 0.5, 1) == 0, "Ready cooldown stays ready");
        Assert(Math.Abs(CombatMath.AttackInterval(0.5, 100, rulesFromTable(tables)) - rulesFromTable(tables)) < 1e-8, "Minimum interval from table");
        Reject(() => CombatMath.Damage(double.NaN, 0, 1, 10000, 15000, false), "NaN attack");
        Reject(() => CombatMath.Damage(double.MaxValue, 0, 1, 10000, 15000, false), "Attack square overflow");
        Reject(() => CombatMath.Damage(1, 0, 0, 10000, 15000, false), "Zero defense parameter");
        Reject(() => CombatMath.ProbabilityThreshold(-1, 1, 0), "Negative probability input");
        Reject(() => CombatMath.ProbabilityThreshold(double.MaxValue, double.MaxValue, 0), "Probability overflow");
        Reject(() => CombatMath.AttackInterval(1, 0, 0.05), "Zero attack speed");

        var attack = AttackSnapshot.Capture(attributes, 1, 1, 1);
        target = DamageTarget.Spawn(attributes, 2, 2, true);
        target.ImmunityCharges = 1;
        target.InvulnerableUntil = 1;
        var result = CombatDamage.Resolve(attack, ref target, 2, true, 0, 7, rules);
        Assert(result.Outcome == DamageOutcome.Invulnerable && target.ImmunityCharges == 1, "Invulnerability precedes charges");
        result = CombatDamage.Resolve(attack, ref target, 2, true, 1, 7, rules);
        Assert(result.Outcome == DamageOutcome.Immune && target.ImmunityCharges == 0, "Charge consumption");
        result = CombatDamage.Resolve(attack, ref target, 2, true, 1, 7, rules);
        Assert(result.HealthLost > 0 && target.InvulnerableUntil == 1 + rules.PlayerInvulnerability, "Positive damage starts invulnerability");
        long health = target.Health;
        result = CombatDamage.Resolve(attack, ref target, 2, true, 1, 7, rules);
        Assert(result.Outcome == DamageOutcome.Invulnerable && target.Health == health, "Same tick invulnerability");
        result = CombatDamage.Resolve(attack, ref target, 3, true, 2, 7, rules);
        Assert(result.Outcome == DamageOutcome.Rejected && target.Health == health, "Stale lifecycle rejected");
        result = CombatDamage.Resolve(attack, ref target, 2, false, 2, 7, rules);
        Assert(result.Outcome == DamageOutcome.Rejected, "No geometry contact");
        target.Faction = 1;
        Assert(CombatDamage.Resolve(attack, ref target, 2, true, 2, 7, rules).Outcome == DamageOutcome.Rejected, "Same faction");
        target = DamageTarget.Spawn(attributes, 2, 2, false);
        target.Health = 1;
        result = CombatDamage.Resolve(attack, ref target, 2, true, 2, 7, rules);
        Assert(result.Killed && result.HealthLost == 1 && target.Health == 0, "One lethal transition");
        result = CombatDamage.Resolve(attack, ref target, 2, true, 2, 7, rules);
        Assert(!result.Killed && result.Outcome == DamageOutcome.Rejected, "No duplicate death");
        target = DamageTarget.Spawn(attributes, 3, 2, true);
        attack.Attack = 0;
        result = CombatDamage.Resolve(attack, ref target, 3, true, 2, 7, rules);
        Assert(result.Outcome == DamageOutcome.Hit && result.HealthLost == 0 && target.InvulnerableUntil == 0, "Zero damage remains hit without invulnerability");
        attack.Hit = 0; target.Evasion = 1;
        result = CombatDamage.Resolve(attack, ref target, 3, true, 2, 7, rules);
        Assert(result.Outcome == DamageOutcome.Evaded && target.InvulnerableUntil == 0, "Evasion does not trigger invulnerability");
        var mutualA = DamageTarget.Spawn(attributes, 1, 1, false);
        var mutualB = DamageTarget.Spawn(attributes, 2, 2, false);
        mutualA.Health = mutualB.Health = 1;
        var attackA = AttackSnapshot.Capture(attributes, 1, 10, 1);
        var attackB = AttackSnapshot.Capture(attributes, 2, 11, 2);
        Assert(CombatDamage.Resolve(attackA, ref mutualB, 2, true, 0, 7, rules).Killed &&
            CombatDamage.Resolve(attackB, ref mutualA, 1, true, 0, 7, rules).Killed, "Pre-created attacks allow mutual kills");

        CheckSampling(tables, rules);
        Console.WriteLine($"COMBAT_RUNTIME_CHECKS_PASS checks={count}");
        return count;
    }

    /// <summary>Reads the actual rule value for a timing boundary fixture.</summary>
    /// <param name="tables">Loaded generated tables.</param>
    /// <returns>TbCombatRules.minAttackInterval.</returns>
    private static double rulesFromTable(Tables tables) => tables.TbCombatRules.Get(1).MinAttackInterval;

    /// <summary>Checks deterministic endpoint coverage and non-degenerate production hit/critical distributions.</summary>
    /// <param name="tables">Real probability profiles.</param>
    /// <param name="rules">Validated runtime damage rules.</param>
    /// <exception cref="InvalidOperationException">A deterministic or statistical assertion fails.</exception>
    private static void CheckSampling(Tables tables, DamageRules rules)
    {
        var attributes = new CombatAttributes(tables.TbAttributeProfile.Get(3));
        var defense = new CombatAttributes(tables.TbAttributeProfile.Get(4));
        int hits = 0, criticals = 0, minimum = int.MaxValue, maximum = 0;
        const int samples = 100000;
        for (ulong i = 1; i <= samples; i++)
        {
            var attack = AttackSnapshot.Capture(attributes, 1, i, 1);
            var target = DamageTarget.Spawn(defense, 2, 2, false);
            var result = CombatDamage.Resolve(attack, ref target, 2, true, 0, 20260905, rules);
            if (result.Outcome == DamageOutcome.Hit || result.Outcome == DamageOutcome.Critical) hits++;
            if (result.Outcome == DamageOutcome.Critical) criticals++;
            int sample = CombatRandom.Inclusive(7, i, 2, CombatRandomPurpose.Damage, rules.RandomMinBp, rules.RandomMaxBp);
            minimum = Math.Min(minimum, sample); maximum = Math.Max(maximum, sample);
            if (i == 1)
            {
                CombatRandom.Inclusive(7, 99, 333, CombatRandomPurpose.Hit, 1, 10000);
                Assert(sample == CombatRandom.Inclusive(7, i, 2, CombatRandomPurpose.Damage, rules.RandomMinBp, rules.RandomMaxBp), "Unrelated requests do not perturb samples");
            }
        }
        Assert(minimum == rules.RandomMinBp && maximum == rules.RandomMaxBp, "Both damage endpoints sampled");
        Assert(Math.Abs(hits / (double)samples - 0.8) < 0.01, "Hit distribution");
        Assert(Math.Abs(criticals / (double)hits - 0.2) < 0.01, "Conditional critical distribution");
        Assert(CombatRandom.Inclusive(7, 1, 2, CombatRandomPurpose.Damage, 9, 9) == 9, "Singleton range");
        Console.WriteLine($"COMBAT_RANDOM_SAMPLES count={samples} hits={hits} criticals={criticals} min={minimum} max={maximum}");
    }

    /// <summary>Requires the production validation path to reject invalid fixture data.</summary>
    /// <param name="action">Invalid call.</param>
    /// <param name="label">Failure context.</param>
    /// <exception cref="InvalidOperationException">Invalid data was accepted.</exception>
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { count++; return; }
        throw new InvalidOperationException("Runtime accepted: " + label);
    }

    /// <summary>Counts a successful assertion or reports the exact failed runtime contract.</summary>
    /// <param name="condition">Expected condition.</param>
    /// <param name="label">Failure context.</param>
    /// <exception cref="InvalidOperationException">The condition is false.</exception>
    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Runtime check failed: " + label);
        count++;
    }
}
