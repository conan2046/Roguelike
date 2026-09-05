using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>CombatHud Prefab 的序列化绑定；布局和样式保留在 Prefab 中供策划调整。</summary>
    public sealed class CombatHudView : MonoBehaviour
    {
        [SerializeField] private Text healthText;
        [SerializeField] private Text timerText;
        [SerializeField] private Text levelText;
        [SerializeField] private Text experienceText;
        [SerializeField] private Text killsText;
        [SerializeField] private Text waveText;
        [SerializeField] private Slider experienceBar;

        public Text HealthText => healthText;
        public Text TimerText => timerText;
        public Text LevelText => levelText;
        public Text ExperienceText => experienceText;
        public Text KillsText => killsText;
        public Text WaveText => waveText;
        public Slider ExperienceBar => experienceBar;
    }
}
