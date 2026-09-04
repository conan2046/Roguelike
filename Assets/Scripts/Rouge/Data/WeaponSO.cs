using UnityEngine;

/// <summary>
/// 武器数据（数据层 SO 骨架 · M1）。
/// 局内成长只走「星级」(1~5)，品质仅局外成长（见框架 §2.2 / §3.2）。
/// </summary>
[CreateAssetMenu(fileName = "WeaponSO", menuName = "Rouge/Data/WeaponSO")]
public class WeaponSO : ScriptableObject
{
    public string id;
    public string displayName;
    [Range(1, 5)] public int star = 1;          // 局内星级 1~5 → 超武
    public float baseDamage;
    public float attackInterval;                // 秒
    public string animKey;                       // 对应 Cocos .ani 关键帧
}
