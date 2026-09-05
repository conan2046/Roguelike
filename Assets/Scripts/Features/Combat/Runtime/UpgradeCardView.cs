using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>UpgradeCard Prefab 的序列化绑定；图标为空时保留可替换色块。</summary>
    public sealed class UpgradeCardView : MonoBehaviour
    {
        [SerializeField] private Button selectButton;
        [SerializeField] private Image iconImage;
        [SerializeField] private Text nameText;
        [SerializeField] private Text descriptionText;
        [SerializeField] private Text rankText;

        public Button SelectButton => selectButton;
        public Image IconImage => iconImage;
        public Text NameText => nameText;
        public Text DescriptionText => descriptionText;
        public Text RankText => rankText;
    }
}
