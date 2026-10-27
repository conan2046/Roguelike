using System;
using System.Collections.Generic;
using NUnit.Framework;
using Roguelike.Features.Combat.Runtime;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;

namespace Roguelike.Tests
{
    /// <summary>验证战斗 World 的系统白名单和生命周期边界。</summary>
    public sealed class CombatWorldFactoryTests
    {
        /// <summary>系统目录只允许 Unity 基础设施程序集，拒绝项目测试程序集。</summary>
        [Test]
        public void CatalogSelectsOnlyUnityInfrastructureSystems()
        {
            IReadOnlyList<Type> selected = CombatWorldSystemCatalog.SelectInfrastructureSystems(new[]
            {
                typeof(InitializationSystemGroup),
                typeof(LocalToWorldSystem),
                typeof(CombatWorldFactoryTests)
            });

            Assert.That(selected, Does.Contain(typeof(InitializationSystemGroup)));
            Assert.That(selected, Does.Contain(typeof(LocalToWorldSystem)));
            CollectionAssert.DoesNotContain(new List<Type>(selected), typeof(CombatWorldFactoryTests));
        }

        /// <summary>创建的 World 保留变换与 Entities Graphics，同时排除 Default 发现的项目业务系统。</summary>
        [Test]
        public void CreateKeepsRenderingInfrastructureAndExcludesProjectSystems()
        {
            using World world = CombatWorldFactory.Create();

            Assert.That(world.Name, Is.EqualTo(CombatWorldFactory.WorldName));
            Assert.That(world.GetExistingSystemManaged<InitializationSystemGroup>(), Is.Not.Null);
            Assert.That(world.GetExistingSystemManaged<SimulationSystemGroup>(), Is.Not.Null);
            Assert.That(world.GetExistingSystemManaged<PresentationSystemGroup>(), Is.Not.Null);
            Assert.That(world.GetExistingSystem(typeof(LocalToWorldSystem)), Is.Not.EqualTo(SystemHandle.Null));
            Assert.That(world.GetExistingSystemManaged<EntitiesGraphicsSystem>(), Is.Not.Null);

            int excludedProjectSystems = 0;
            IReadOnlyList<Type> defaults = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
            for (int index = 0; index < defaults.Count; index++)
            {
                Type candidate = defaults[index];
                string assemblyName = candidate.Assembly.GetName().Name;
                if (!IsProjectSystemAssembly(assemblyName)) continue;
                excludedProjectSystems++;
                Assert.That(world.GetExistingSystem(candidate), Is.EqualTo(SystemHandle.Null), candidate.FullName);
            }

            Assert.That(excludedProjectSystems, Is.GreaterThan(0),
                "Expected at least one project ECS system to exercise the exclusion boundary.");
        }

        /// <summary>识别当前项目程序集和遗留 Assembly-CSharp 中的业务系统。</summary>
        /// <param name="assemblyName">候选系统所属程序集名称。</param>
        /// <returns>属于 Roguelike 项目程序集或 Assembly-CSharp 时返回真。</returns>
        private static bool IsProjectSystemAssembly(string assemblyName)
        {
            return string.Equals(assemblyName, "Assembly-CSharp", StringComparison.Ordinal) ||
                   assemblyName.StartsWith("Roguelike.", StringComparison.Ordinal);
        }
    }
}
