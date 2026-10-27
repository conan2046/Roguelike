using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>事件通知、精英距离和可进化状态的正式 HUD 绑定。</summary>
    public sealed class RunEventHudView : MonoBehaviour
    {
        [SerializeField] private Text noticeText, eliteText, statusText;
        [SerializeField] private RectTransform chestMarkerContainer;
        public Text NoticeText => noticeText;
        public Text EliteText => eliteText;
        public Text StatusText => statusText;
        public RectTransform ChestMarkerContainer => chestMarkerContainer;
    }
}
