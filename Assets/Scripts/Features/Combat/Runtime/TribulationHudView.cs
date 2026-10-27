using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式切片HUD绑定；布局、字体和层级全部保存在专用Prefab。</summary>
    public sealed class TribulationHudView : MonoBehaviour
    {
        public Text TitleText, MechanismText, AltarText, RefinementText, BossText;
        public Button ActivateButton, ReturnButton;
        public Text ActivateLabel, ReturnLabel;
        public GameObject RewardRoot;
        public Text RewardTitleText, RewardIntroText;
        public Button[] RefinementButtons;
        public Text[] RefinementNames, RefinementDescriptions;
    }
}
