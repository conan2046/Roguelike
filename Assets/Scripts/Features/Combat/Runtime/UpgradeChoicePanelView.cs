using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>UpgradeChoicePanel Prefab 的序列化绑定。</summary>
    public sealed class UpgradeChoicePanelView : MonoBehaviour
    {
        [SerializeField] private Text titleText;
        [SerializeField] private RectTransform cardContainer;

        public Text TitleText => titleText;
        public RectTransform CardContainer => cardContainer;
    }
}
