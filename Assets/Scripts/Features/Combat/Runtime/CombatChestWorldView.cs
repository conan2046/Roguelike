using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>场景宝箱正式屏幕投影绑定；位置跟随世界坐标，图形布局在 Prefab 维护，名称读取 TbChestProfile。</summary>
    public sealed class CombatChestWorldView : MonoBehaviour
    {
        [SerializeField] private Text nameText;
        public Text NameText => nameText;
    }
}
