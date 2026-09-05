using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>SettlementPanel Prefab 的序列化绑定。</summary>
    public sealed class SettlementPanelView : MonoBehaviour
    {
        [SerializeField] private Text resultText;
        [SerializeField] private Text statisticsText;
        [SerializeField] private Text restartText;
        [SerializeField] private Button restartButton;

        public Text ResultText => resultText;
        public Text StatisticsText => statisticsText;
        public Text RestartText => restartText;
        public Button RestartButton => restartButton;
    }
}
