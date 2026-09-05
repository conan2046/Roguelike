using System;
using System.Collections.Generic;
using cfg;

namespace Roguelike.Features.Combat
{
    /// <summary>单个来源实例提供的属性修改；数值由上层 Luban 配置消费入口提供。</summary>
    public struct AttributeModifier
    {
        public EAttributeType Attribute;
        public double Flat;
        public int PercentBp;
    }

    /// <summary>主线程属性聚合器；仅属性变化时分配和重算，不能传入 Job。</summary>
    public sealed class CombatAttributes
    {
        private readonly SortedDictionary<EAttributeType, AttributeConfig> definitions = new SortedDictionary<EAttributeType, AttributeConfig>();
        private readonly Dictionary<EAttributeType, double> bases = new Dictionary<EAttributeType, double>();
        private SortedDictionary<ulong, AttributeModifier[]> sources = new SortedDictionary<ulong, AttributeModifier[]>();
        private Dictionary<EAttributeType, double> values;

        /// <summary>入局时从统一配置入口提供的 TbAttributeProfile 与 TbAttribute 建立全量属性，不补默认值。</summary>
        /// <param name="profile">已解析引用的属性方案。</param>
        /// <exception cref="InvalidOperationException">缺失、重复、越界或语义非法。</exception>
        public CombatAttributes(AttributeProfileConfig profile)
        {
            if (profile == null) throw new InvalidOperationException("TbAttributeProfile: missing profile.");
            foreach (var entry in profile.Values)
            {
                var definition = entry?.AttributeId_Ref;
                if (definition == null) throw new InvalidOperationException($"TbAttributeProfile {profile.Id}: unresolved attribute.");
                if (!Enum.IsDefined(typeof(EAttributeType), definition.AttributeType) || definitions.ContainsKey(definition.AttributeType))
                    throw new InvalidOperationException($"TbAttributeProfile {profile.Id}: duplicate/unknown attribute {definition.Id}.");
                CombatMath.NonNegative(definition.MinValue); CombatMath.NonNegative(definition.MaxValue);
                if (definition.MaxValue < definition.MinValue || !Enum.IsDefined(typeof(EAttributeValueKind), definition.ValueKind))
                    throw new InvalidOperationException($"TbAttribute {definition.Id}: invalid bounds/type.");
                CombatMath.NonNegative(entry.Value);
                if (entry.Value < definition.MinValue || entry.Value > definition.MaxValue ||
                    (definition.ValueKind == EAttributeValueKind.Integer && entry.Value != Math.Floor(entry.Value)))
                    throw new InvalidOperationException($"TbAttributeProfile {profile.Id}: attribute {definition.Id} out of range/type.");
                definitions.Add(definition.AttributeType, definition);
                bases.Add(definition.AttributeType, entry.Value);
            }
            foreach (EAttributeType type in Enum.GetValues(typeof(EAttributeType)))
                if (!definitions.ContainsKey(type)) throw new InvalidOperationException($"TbAttributeProfile {profile.Id}: missing {type}.");
            values = Recalculate(sources);
        }

        /// <summary>快照创建或属性同步时读取已聚合值，不返回兜底属性。</summary>
        /// <param name="attribute">生成枚举定义的属性语义。</param>
        /// <returns>按 TbAttribute 范围截断并按 valueKind 取整后的数值。</returns>
        /// <exception cref="KeyNotFoundException">调用者请求未定义的语义。</exception>
        public double Get(EAttributeType attribute) => values[attribute];

        /// <summary>配置效果变化时原子替换来源实例；先验证重算，失败不会污染现有属性。</summary>
        /// <param name="source">来源实例标识，不是效果类型 ID。</param>
        /// <param name="modifiers">来源配置解析后的修改集合；复制以隔离调用方后续修改。</param>
        /// <remarks>修改本聚合器状态；调用方随后同步生命上限、攻速冷却和 ECS 属性快照。</remarks>
        /// <exception cref="InvalidOperationException">未知属性、非有限修改或聚合溢出。</exception>
        public void Replace(ulong source, params AttributeModifier[] modifiers)
        {
            if (modifiers == null) throw new InvalidOperationException("Missing attribute modifiers.");
            var copy = (AttributeModifier[])modifiers.Clone();
            foreach (var item in copy)
                if (!definitions.ContainsKey(item.Attribute) || double.IsNaN(item.Flat) || double.IsInfinity(item.Flat))
                    throw new InvalidOperationException("Invalid attribute modifier.");
            var candidate = new SortedDictionary<ulong, AttributeModifier[]>(sources) { [source] = copy };
            var computed = Recalculate(candidate);
            sources = candidate;
            values = computed;
        }

        /// <summary>来源结束时从基础值重新聚合，避免反向浮点扣除积累误差。</summary>
        /// <param name="source">要移除的来源实例标识。</param>
        /// <returns>是否存在并移除了该来源。</returns>
        /// <remarks>成功时更新本聚合器；上层负责同步 ECS。</remarks>
        /// <exception cref="InvalidOperationException">移除后剩余修改聚合溢出。</exception>
        public bool Remove(ulong source)
        {
            if (!sources.ContainsKey(source)) return false;
            var candidate = new SortedDictionary<ulong, AttributeModifier[]>(sources);
            candidate.Remove(source);
            var computed = Recalculate(candidate);
            sources = candidate;
            values = computed;
            return true;
        }

        /// <summary>变更时按来源序号稳定累加；范围来自 TbAttribute，基础来自 TbAttributeProfile.values。</summary>
        /// <param name="candidate">待验证的来源集合。</param>
        /// <returns>完整的新属性集合。</returns>
        /// <exception cref="InvalidOperationException">计算出现非有限值或关键参数为零。</exception>
        private Dictionary<EAttributeType, double> Recalculate(SortedDictionary<ulong, AttributeModifier[]> candidate)
        {
            var result = new Dictionary<EAttributeType, double>();
            foreach (var pair in definitions)
            {
                double flat = 0, percent = 0;
                foreach (var source in candidate.Values)
                    foreach (var item in source)
                        if (item.Attribute == pair.Key) { flat += item.Flat; percent += item.PercentBp; }
                double value = (bases[pair.Key] + flat) * (1 + percent / CombatMath.BasisPoints);
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException($"Attribute {pair.Key}: aggregation overflow.");
                value = Math.Max(pair.Value.MinValue, Math.Min(pair.Value.MaxValue, value));
                if (pair.Value.ValueKind == EAttributeValueKind.Integer) value = Math.Floor(value);
                if (value < pair.Value.MinValue) throw new InvalidOperationException($"TbAttribute {pair.Value.Id}: integer bounds contain no legal value.");
                result.Add(pair.Key, value);
            }
            CombatMath.Positive(result[EAttributeType.MaxHealth]);
            CombatMath.Positive(result[EAttributeType.AttackSpeedMultiplier]);
            CombatMath.Positive(result[EAttributeType.DefenseParameter]);
            CombatMath.Positive(result[EAttributeType.HitParameter]);
            CombatMath.Positive(result[EAttributeType.EvasionParameter]);
            CombatMath.Positive(result[EAttributeType.CriticalParameter]);
            CombatMath.Positive(result[EAttributeType.CriticalResistanceParameter]);
            return result;
        }
    }
}
