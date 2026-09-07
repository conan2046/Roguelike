using Roguelike.App;
using UnityEditor;

namespace Roguelike.App.Editor
{
    /// <summary>编辑器启动模式菜单：以显式选择的模式进入 Play，等价于向编辑器进程附加命令行参数。</summary>
    /// <remarks>
    /// Unity Hub 启动编辑器时无法附加自定义参数，导致点 Play 只能进入普通启动（无战斗内容）。
    /// 本菜单通过 EditorLaunchOverride 写入等价参数，组合根在启动最早阶段消费；不影响真机行为。
    /// </remarks>
    public static class EditorLaunchMenu
    {
        /// <summary>正式关卡 ID 在 EditorPrefs 中的键；默认 1（当前 TbStage 唯一条目）。</summary>
        private const string StageIdKey = "Roguelike.EditorLaunch.StageId";

        /// <summary>以正式关卡（TbStage）进入 Play 模式。</summary>
        /// <remarks>先写入启动参数覆盖，再进入 Play；组合根据此创建 Roguelike.Stage 运行器。</remarks>
        [MenuItem("Roguelike/启动/正式关卡 (Stage)")]
        public static void EnterStage()
        {
            int stageId = EditorPrefs.GetInt(StageIdKey, 1);
            EditorLaunchOverride.Write("-stage " + stageId);
            EditorApplication.EnterPlaymode();
        }

        /// <summary>以战斗场景（TbPerformanceScenario，Kind=Combat）进入 Play 模式。</summary>
        /// <remarks>场景 ID 4 为当前已验收的战斗场景，与命令行 -combatScenario 4 等价。</remarks>
        [MenuItem("Roguelike/启动/战斗场景 (Combat 4)")]
        public static void EnterCombat()
        {
            EditorLaunchOverride.Write("-combatScenario 4");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>清除启动模式覆盖，回到普通启动（不进入任何战斗场景）。</summary>
        [MenuItem("Roguelike/启动/普通启动 (清除覆盖)")]
        public static void EnterNormal()
        {
            EditorLaunchOverride.Write(string.Empty);
            EditorApplication.EnterPlaymode();
        }

        /// <summary>设置正式关卡 ID；当前 TbStage 仅有 id=1，新增关卡后可改此值。</summary>
        /// <remarks>只改 EditorPrefs，不修改任何配置表或场景。</remarks>
        [MenuItem("Roguelike/启动/设置正式关卡 ID = 1")]
        public static void SetStageId1()
        {
            EditorPrefs.SetInt(StageIdKey, 1);
            UnityEngine.Debug.Log("[Roguelike] 正式关卡 ID 已设为 1。");
        }
    }
}
