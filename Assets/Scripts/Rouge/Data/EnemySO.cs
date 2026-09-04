using UnityEngine;

/// <summary>
/// 敌人系别（共 7 系，非 81×7；见框架 §2.5 / 诚实边界）。
/// </summary>
public enum EnemyFaction
{
    Fire, Water, Wood, Metal, Earth, Light, Dark
}

/// <summary>
/// 敌人数据（数据层 SO 骨架 · M1）。
/// </summary>
[CreateAssetMenu(fileName = "EnemySO", menuName = "Rouge/Data/EnemySO")]
public class EnemySO : ScriptableObject
{
    public string id;
    public EnemyFaction faction;
    public float hp;
    public float speed;
    public float damage;
    public int expValue;
    public string animKey;                        // 对应 Cocos .ani 关键帧
}
