using UnityEngine;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>独立武器层编辑标识；通过 TbHeroWeapon 关联本体、档位及表现，不参与移动碰撞。</summary>
    [DisallowMultipleComponent]
    public sealed class HeroWeaponAuthoring : MonoBehaviour
    {
        [Tooltip("TbHeroWeapon.id；所属神将、档位和表现均由该表读取。")]
        public int WeaponConfigId;
    }
}
