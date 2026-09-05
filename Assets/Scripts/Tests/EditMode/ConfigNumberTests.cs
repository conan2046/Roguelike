using System;
using cfg;
using NUnit.Framework;

namespace Roguelike.Tests
{
    /// <summary>验证表格整数协议的舍入、溢出和长整数属性边界，独立于业务默认数值。</summary>
    public sealed class ConfigNumberTests
    {
        /// <summary>导出应按三位小数处理正负半值与旧导出尾差，不静默截断负数。</summary>
        /// <param name="value">原始单位输入。</param><param name="expected">预期千分整数。</param>
        [TestCase(0.080, 80)]
        [TestCase(1.2345, 1235)]
        [TestCase(-1.2345, -1235)]
        [TestCase(61.0000038, 61000)]
        public void ExportRoundsToThreePlaces(double value, int expected) => Assert.That(ConfigNumber.Encode(value), Is.EqualTo(expected));

        /// <summary>非有限数或放大溢出在编辑导出边界拒绝，不能进入整数配置。</summary>
        /// <param name="value">无效输入。</param>
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(2147484.0)]
        public void InvalidExportFails(double value) => Assert.Throws<OverflowException>(() => ConfigNumber.Encode(value));

        /// <summary>属性十亿上限按长整数存储，缩放后还原不发生 Int32 溢出。</summary>
        [Test]
        public void LargeAttributeLimitRemainsExact() => Assert.That(ConfigNumber.Decode(1000000000000L), Is.EqualTo(1000000000d));
    }
}
