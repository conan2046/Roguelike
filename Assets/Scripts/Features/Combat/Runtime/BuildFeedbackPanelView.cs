using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式构筑反馈 Prefab 绑定；布局与样式序列化，运行时只刷新内容与交互。</summary>
    public sealed class BuildFeedbackPanelView : MonoBehaviour
    {
        public GameObject DraftControls, SettlementRoot;
        public Button RerollButton, SkipButton, RetryButton, DetailsButton;
        public Text RerollText, SkipText, DraftStatus, HudText, SummaryText, SaveText, RetryText, DetailsText;
    }
}
