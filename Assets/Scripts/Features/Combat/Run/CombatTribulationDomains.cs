using System;
using System.Collections.Generic;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>周天阵域一次真实伤害请求；范围与倍率来自TbTribulationSignature，调用方交给正式会话结算。</summary>
    public readonly struct TribulationDomainPulse
    {
        public readonly double XPixels, YPixels, RadiusPixels;
        public readonly int DamagePermille;
        public readonly bool Explosion;

        /// <summary>从已验证职业规则创建一次脉冲或重叠引爆。</summary>
        /// <param name="x">中心横像素。</param><param name="y">中心纵像素。</param><param name="radius">判定半径。</param>
        /// <param name="damage">源表伤害千分比。</param><param name="explosion">是否属于重叠引爆。</param>
        public TribulationDomainPulse(double x, double y, double radius, int damage, bool explosion)
        { XPixels = x; YPixels = y; RadiusPixels = radius; DamagePermille = damage; Explosion = explosion; }
    }

    /// <summary>按源表移动距离留阵，维护阵域寿命、脉冲及重叠消耗；只使用逻辑像素与模拟毫秒。</summary>
    public sealed class CombatTribulationDomains
    {
        /// <summary>当前存活阵域的只读视图；创建序号用于表现复用，不依赖列表位置。</summary>
        public sealed class Domain
        {
            public ulong Id { get; internal set; }
            public double XPixels { get; internal set; }
            public double YPixels { get; internal set; }
            public long BornMilli { get; internal set; }
            public long NextPulseMilli { get; internal set; }
        }
        private readonly TribulationSignatureConfig config;
        private readonly List<Domain> domains = new List<Domain>();
        private readonly List<TribulationDomainPulse> pulses = new List<TribulationDomainPulse>();
        private readonly List<Domain> overlaps = new List<Domain>();
        private double anchorX, anchorY;
        private bool initialized;
        private ulong sequence;
        public long ElapsedMilli { get; private set; }
        public ulong ExplosionCount { get; private set; }
        public IReadOnlyList<Domain> Domains => domains;
        public IReadOnlyList<TribulationDomainPulse> Pulses => pulses;

        /// <summary>选择周天后绑定TbTribulationSignature；不为其他职业创建阵域规则。</summary>
        /// <param name="config">经正式定义验证的Domains配置。</param>
        /// <exception cref="ArgumentException">配置不属于阵修或必要规则无效。</exception>
        public CombatTribulationDomains(TribulationSignatureConfig config)
        {
            if (config == null || config.Kind != ETribulationSignature.Domains || config.DomainStepPixels <= 0 || config.DomainDurationMilli <= 0 ||
                config.DomainPulseMilli <= 0 || config.MaxDomains <= 0 || config.OverlapCount < 2)
                throw new ArgumentException("TbTribulationSignature: invalid domain configuration.", nameof(config));
            this.config = config;
        }

        /// <summary>实际模拟推进后按移动路径补齐留阵点，随后发出各阵域到期脉冲；暂停不调用。</summary>
        /// <param name="milliseconds">真实模拟步对应的毫秒。</param><param name="xPixels">玩家权威横像素。</param><param name="yPixels">玩家权威纵像素。</param>
        /// <remarks>不修改生命；Pulses只供本次同步借用，下一次Advance会清空。过期及被引爆阵域立即退出当前列表。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">时间或位置非有限合法输入。</exception>
public void Advance(int milliseconds, double xPixels, double yPixels)
        {
            if (milliseconds < 0 || double.IsNaN(xPixels) || double.IsNaN(yPixels) || double.IsInfinity(xPixels) || double.IsInfinity(yPixels))
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            pulses.Clear();
            if (!initialized) { anchorX = xPixels; anchorY = yPixels; initialized = true; }
            if (milliseconds == 0) return;
            ElapsedMilli = checked(ElapsedMilli + milliseconds);
            CollectDuePulses();
            for (int i = domains.Count - 1; i >= 0; i--)
                if (ElapsedMilli - domains[i].BornMilli >= config.DomainDurationMilli) domains.RemoveAt(i);
            double dx = xPixels - anchorX, dy = yPixels - anchorY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            while (distance >= config.DomainStepPixels)
            {
                anchorX += dx / distance * config.DomainStepPixels;
                anchorY += dy / distance * config.DomainStepPixels;
                AddDomain(anchorX, anchorY);
                dx = xPixels - anchorX; dy = yPixels - anchorY; distance = Math.Sqrt(dx * dx + dy * dy);
            }
            CollectDuePulses();
        }

/// <summary>先补齐到期脉冲再回收阵域，确保大步长跨过寿命时不丢失寿命内已经到期的伤害。</summary>
        /// <remarks>NextPulseMilli在每次追加后前移，同一同步步重复收集不会重发。</remarks>
        private void CollectDuePulses()
        {
            foreach (var domain in domains)
                while (domain.NextPulseMilli <= ElapsedMilli && domain.NextPulseMilli - domain.BornMilli < config.DomainDurationMilli)
                {
                    pulses.Add(new TribulationDomainPulse(domain.XPixels, domain.YPixels, config.DomainRadiusPixels, config.DomainDamagePermille, false));
                    domain.NextPulseMilli += config.DomainPulseMilli;
                }
        }


        /// <summary>移动越过留阵距离时创建一个源表阵域；与旧阵达到表内重叠数量后消耗参与者并发出一次引爆。</summary>
        /// <param name="x">新阵中心横像素。</param><param name="y">新阵中心纵像素。</param>
        /// <remarks>达到容量时先回收最旧阵域。引爆只发正式伤害请求，不直接改变目标生命。</remarks>
        private void AddDomain(double x, double y)
        {
            if (domains.Count == config.MaxDomains) domains.RemoveAt(0);
            var created = new Domain { Id = ++sequence, XPixels = x, YPixels = y, BornMilli = ElapsedMilli, NextPulseMilli = ElapsedMilli };
            domains.Add(created); overlaps.Clear();
            double reach = config.DomainRadiusPixels * 2d;
            foreach (var domain in domains)
            {
                double dx = x - domain.XPixels, dy = y - domain.YPixels;
                if (dx * dx + dy * dy <= reach * reach) overlaps.Add(domain);
            }
            if (overlaps.Count < config.OverlapCount) return;
            foreach (var domain in overlaps) domains.Remove(domain);
            pulses.Add(new TribulationDomainPulse(x, y, config.OverlapRadiusPixels, config.OverlapDamagePermille, true));
            ExplosionCount++;
        }

        /// <summary>Boss破阵雷击结算时移除与真实命中圆相交的阵域。</summary>
        /// <param name="xPixels">雷击中心横像素。</param><param name="yPixels">雷击中心纵像素。</param><param name="radiusPixels">表内雷击半径。</param>
        /// <returns>被清除的实际阵域数量。</returns><remarks>只移除阵域，不追溯撤回已经结算的脉冲伤害。</remarks>
        public int ClearIntersecting(double xPixels, double yPixels, double radiusPixels)
        {
            int removed = 0; double reach = radiusPixels + config.DomainRadiusPixels;
            for (int i = domains.Count - 1; i >= 0; i--)
            {
                double dx = xPixels - domains[i].XPixels, dy = yPixels - domains[i].YPixels;
                if (dx * dx + dy * dy > reach * reach) continue;
                domains.RemoveAt(i); removed++;
            }
            return removed;
        }

        /// <summary>结算、重开或退出时清空本代次阵域和借用队列。</summary>
        /// <remarks>没有Unity资源或异步任务；下一代单局创建新的模型，不复用旧位置锚点。</remarks>
        public void Clear() { domains.Clear(); pulses.Clear(); overlaps.Clear(); }
    }
}
