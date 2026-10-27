using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式宝箱奖励 Prefab 的序列化绑定；布局与字号只在 Prefab 中维护。</summary>
    public sealed class ChestRewardPanelView : MonoBehaviour
    {
        [SerializeField] private Text titleText, rewardNameText, descriptionText, claimText;
        [SerializeField] private Button claimButton;
        [SerializeField] private Transform cardContainer;
        [SerializeField] private GameObject singleRewardRoot;
        public Text TitleText => titleText;
        public Text RewardNameText => rewardNameText;
        public Text DescriptionText => descriptionText;
        public Text ClaimText => claimText;
        public Button ClaimButton => claimButton;
        public Transform CardContainer => cardContainer;
        public GameObject SingleRewardRoot => singleRewardRoot;
    }
}
