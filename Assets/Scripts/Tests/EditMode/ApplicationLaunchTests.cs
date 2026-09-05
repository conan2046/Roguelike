using System;
using System.Reflection;
using NUnit.Framework;
using Roguelike.App;

namespace Roguelike.Tests
{
    /// <summary>组合根启动协议与临时场景隔离回归，不保存场景或修改配置。</summary>
    public sealed class ApplicationLaunchTests
    {
        /// <summary>无参数保持普通启动；正式关卡、显式战斗和性能各自选择唯一分支并保留传入 ID。</summary>
        [Test]
        public void ExplicitModesPreserveRequestedScenario()
        {
            var normal = ApplicationLaunchOptions.Parse(new[] { "game.exe" });
            Assert.That(normal.Mode, Is.EqualTo(ApplicationMode.Normal)); Assert.That(normal.ScenarioId, Is.Null);
            var stage = ApplicationLaunchOptions.Parse(new[] { "game.exe", "-stage", "1" });
            Assert.That(stage.Mode, Is.EqualTo(ApplicationMode.Stage)); Assert.That(stage.ScenarioId, Is.EqualTo(1));
            var combat = ApplicationLaunchOptions.Parse(new[] { "game.exe", "-combatScenario", "4" });
            Assert.That(combat.Mode, Is.EqualTo(ApplicationMode.Combat)); Assert.That(combat.ScenarioId, Is.EqualTo(4));
            var performance = ApplicationLaunchOptions.Parse(new[] { "-performanceScenario", "1" });
            Assert.That(performance.Mode, Is.EqualTo(ApplicationMode.Performance));
        }

        /// <summary>冲突、重复、缺值、负数和非整数均在创建对象前拒绝。</summary>
        /// <param name="text">测试命令行片段。</param>
        [TestCase("-combatScenario 4 -performanceScenario 1")]
        [TestCase("-stage 1 -combatScenario 4")]
        [TestCase("-performanceScenario 1 -stage 1")]
        [TestCase("-stage 1 -stage 1")]
        [TestCase("-stage")]
        [TestCase("-stage -1")]
        [TestCase("-stage 0")]
        [TestCase("-stage x")]
        [TestCase("-combatScenario 4 -combatScenario 4")]
        [TestCase("-combatScenario")]
        [TestCase("-combatScenario -1")]
        [TestCase("-combatScenario 0")]
        [TestCase("-combatScenario x")]
        public void InvalidModesAreRejected(string text) => Assert.Throws<ArgumentException>(() => ApplicationLaunchOptions.Parse(text.Split(' ')));

        /// <summary>隔离旧场景时保留拥有者与原本停用状态，退出只恢复本轮停用节点。</summary>
        /// <remarks>反射只用于访问组合根内部隔离器；所有临时对象与活动状态在 finally 恢复，不保存资产。</remarks>
        [Test]
        public void SceneIsolationRestoresOnlySuspendedRoots()
        {
            var owner = new UnityEngine.GameObject("Isolation owner");
            var active = new UnityEngine.GameObject("Old active root");
            var inactive = new UnityEngine.GameObject("Old inactive root"); inactive.SetActive(false);
            IDisposable isolation = null;
            try
            {
                var type = typeof(GameApplicationBootstrap).Assembly.GetType("Roguelike.App.CombatSceneIsolation", true);
                isolation = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { owner }, null);
                Assert.That(owner.activeSelf, Is.True);
                Assert.That(active.activeSelf, Is.False); Assert.That(inactive.activeSelf, Is.False);
                isolation.Dispose(); isolation.Dispose();
                Assert.That(active.activeSelf, Is.True); Assert.That(inactive.activeSelf, Is.False);
            }
            finally
            {
                isolation?.Dispose();
                UnityEngine.Object.DestroyImmediate(owner); UnityEngine.Object.DestroyImmediate(active); UnityEngine.Object.DestroyImmediate(inactive);
            }
        }
    }
}
