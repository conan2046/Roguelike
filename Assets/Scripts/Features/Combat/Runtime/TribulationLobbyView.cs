using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式选角界面序列化绑定；槽数与布局由Prefab定义，运行时校验与入口表一致。</summary>
    public sealed class TribulationLobbyView : MonoBehaviour
    {
        public Text TitleText, IntroText, ErrorText;
        public Button[] RoleButtons;
        public Text[] RoleNames, RoleDescriptions, RoleButtonLabels;
        public Text[] GoalDescriptions, ContentLabels;
        public Toggle[] ContentToggles;
    }
}
