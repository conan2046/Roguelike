using UnityEditor;

namespace Roguelike.App.Editor
{
    /// <summary>显式切片启动入口；不复制任何职业或关卡业务ID。</summary>
    public static class TribulationLaunchMenu
    {
        /// <summary>由菜单写入技术模式并进入Play，组合根从TbTribulationUi选择正式入口。</summary>
        /// <remarks>只修改当前项目Editor覆盖，不保存Scene、不构建发行包。</remarks>
        [MenuItem("Roguelike/启动/借劫炼器切片")]
        public static void EnterTribulation()
        {
            EditorLaunchOverride.Write("-tribulation"); EditorApplication.EnterPlaymode();
        }
    }
}
