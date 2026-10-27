using System;
using System.Collections.Generic;
using Unity.Entities;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>创建战斗专用 ECS World，并限制其中可自动注册的系统来源。</summary>
    public static class CombatWorldFactory
    {
        public const string WorldName = "Roguelike Combat";

        /// <summary>创建仅包含 Unity ECS 基础设施系统的战斗 World。</summary>
        /// <returns>由调用方独占并负责释放的战斗 World。</returns>
        /// <remarks>业务系统不得通过 Default 自动发现进入；战斗固定步进系统由 CombatSession 显式创建和推进。</remarks>
        /// <exception cref="InvalidOperationException">Entities 系统目录包含空类型，或系统创建失败。</exception>
        public static World Create()
        {
            var world = new World(WorldName);
            try
            {
                IReadOnlyList<Type> candidates = DefaultWorldInitialization.GetAllSystems(
                    WorldSystemFilterFlags.Default);
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(
                    world,
                    CombatWorldSystemCatalog.SelectInfrastructureSystems(candidates));
                return world;
            }
            catch
            {
                world.Dispose();
                throw;
            }
        }
    }

    /// <summary>定义战斗 World 的自动系统所有权边界。</summary>
    internal static class CombatWorldSystemCatalog
    {
        private const string UnityAssemblyPrefix = "Unity.";

        /// <summary>从 Default 系统中保留 Unity 包提供的 ECS、变换与渲染基础设施。</summary>
        /// <param name="candidates">Entities 按 Default World 标记发现的候选系统。</param>
        /// <returns>允许在战斗 World 中自动创建的系统类型。</returns>
        /// <remarks>项目业务系统必须由明确入口创建，防止性能、遗留或未来系统污染独立战斗 World。</remarks>
        /// <exception cref="ArgumentNullException">候选系统集合为空。</exception>
        /// <exception cref="InvalidOperationException">候选集合包含空类型。</exception>
        internal static IReadOnlyList<Type> SelectInfrastructureSystems(IReadOnlyList<Type> candidates)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            var selected = new List<Type>(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                Type candidate = candidates[index] ??
                                 throw new InvalidOperationException("Default ECS system catalog contains a null type.");
                if (IsUnityInfrastructure(candidate)) selected.Add(candidate);
            }

            return selected;
        }

        /// <summary>判断系统是否由 Unity 包程序集提供，而不是项目或第三方业务程序集提供。</summary>
        /// <param name="systemType">待判断的候选系统类型。</param>
        /// <returns>程序集名称以 Unity. 开头时返回真。</returns>
        private static bool IsUnityInfrastructure(Type systemType)
        {
            string assemblyName = systemType.Assembly.GetName().Name;
            return !string.IsNullOrEmpty(assemblyName) &&
                   assemblyName.StartsWith(UnityAssemblyPrefix, StringComparison.Ordinal);
        }
    }
}
