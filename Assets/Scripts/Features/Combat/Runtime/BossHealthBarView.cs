using UnityEngine;
using UnityEngine.UI;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>BossHealthBar Prefab 的序列化绑定。</summary>
    public sealed class BossHealthBarView : MonoBehaviour
    {
        [SerializeField] private Text nameText;
        [SerializeField] private Text healthText;
        [SerializeField] private Slider healthBar;

        public Text NameText => nameText;
        public Text HealthText => healthText;
        public Slider HealthBar => healthBar;
    }
}
