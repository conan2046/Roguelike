using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Roguelike.App.Editor
{
    /// <summary>管理战斗碰撞线框的 Standalone 编译宏，并在菜单上显示当前开关状态。</summary>
    /// <remarks>修改的是 PlayerSettings 编译符号；Unity 会自动重编译，不触碰 Scene、Prefab 或配置表。</remarks>
    public static class CombatCollisionDebugMenu
    {
        private const string Define = "ROGUELIKE_COMBAT_COLLISION_DEBUG";
        private const string MenuPath = "Roguelike/调试/显示战斗碰撞框";

        /// <summary>从 Unity 菜单切换战斗碰撞线框编译宏。</summary>
        [MenuItem(MenuPath)]
        public static void Toggle()
        {
            SetEnabled(!IsEnabled());
        }

        /// <summary>供命令行 executeMethod 或自动化显式启用碰撞线框宏。</summary>
        public static void Enable()
        {
            SetEnabled(true);
        }

        /// <summary>供命令行 executeMethod 或自动化显式关闭碰撞线框宏。</summary>
        public static void Disable()
        {
            SetEnabled(false);
        }

        /// <summary>Unity 刷新菜单时同步勾选状态。</summary>
        /// <returns>始终允许用户点击该菜单。</returns>
        [MenuItem(MenuPath, true)]
        private static bool ValidateToggle()
        {
            Menu.SetChecked(MenuPath, IsEnabled());
            return true;
        }

        /// <summary>读取 Standalone 当前宏集合并判断碰撞调试宏是否存在。</summary>
        /// <returns>已启用 ROGUELIKE_COMBAT_COLLISION_DEBUG 时为真。</returns>
        private static bool IsEnabled()
        {
            return ReadSymbols().Contains(Define);
        }

        /// <summary>保留其他宏，只增删碰撞调试宏并请求 Unity 重编译。</summary>
        /// <param name="enabled">真为启用，假为关闭。</param>
        /// <remarks>重复设置相同状态时不写 PlayerSettings，避免无意义重编译。</remarks>
        private static void SetEnabled(bool enabled)
        {
            HashSet<string> symbols = ReadSymbols();
            bool changed = enabled ? symbols.Add(Define) : symbols.Remove(Define);
            if (!changed)
            {
                Debug.Log($"[Roguelike] 战斗碰撞框已经{(enabled ? "启用" : "关闭")}。");
                return;
            }

            NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Standalone);
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols.OrderBy(item => item, StringComparer.Ordinal)));
            Menu.SetChecked(MenuPath, enabled);
            Debug.Log($"[Roguelike] 战斗碰撞框已{(enabled ? "启用" : "关闭")}；宏 {Define} 将在编译完成后生效。");
        }

        /// <summary>把 Standalone 分号分隔的宏字符串解析为忽略空项的集合。</summary>
        /// <returns>当前 Standalone 编译宏集合。</returns>
        private static HashSet<string> ReadSymbols()
        {
            NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Standalone);
            string value = PlayerSettings.GetScriptingDefineSymbols(target);
            return new HashSet<string>((value ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
        }
    }
}
