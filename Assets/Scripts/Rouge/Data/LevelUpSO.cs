using UnityEngine;

/// <summary>
/// 升级三选一卡池类型（见框架 §2.4 升级卡池规则）。
/// </summary>
public enum LevelUpOptionType
{
    NewWeapon,        // 获得未持有武器（武器槽未满）
    WeaponUpgrade,    // 已持有武器升 1★
    NewPassive,       // 获得未持有被动（被动槽未满）
    PassiveUpgrade,   // 已持有被动升级
    Heal              // 回血（M1 简化占位）
}

/// <summary>
/// 升级选项数据（数据层 SO 骨架 · M1）。
/// 数值占位，M3 前由 game-numerical-designer 定。
/// </summary>
[CreateAssetMenu(fileName = "LevelUpSO", menuName = "Rouge/Data/LevelUpSO")]
public class LevelUpSO : ScriptableObject
{
    public LevelUpOptionType optionType;
    public WeaponSO weapon;                       // NewWeapon / WeaponUpgrade 关联
    // TODO(M3): 关联 PassiveSO、数值字段
}
