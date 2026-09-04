using UnityEngine;

/// <summary>
/// Spawn 权重项（M1 仅用 timeWindow + spawnTable 最简版）。
/// </summary>
[System.Serializable]
public class SpawnEntry
{
    public EnemySO enemy;
    public int weight = 1;
    public int count = 1;
}

/// <summary>
/// 波次数据（数据层 SO 骨架 · M1）。
/// 完整 Spawn Director 见框架 §2.7（固定时间曲线 + 有限动态）。
/// </summary>
[CreateAssetMenu(fileName = "WaveSO", menuName = "Rouge/Data/WaveSO")]
public class WaveSO : ScriptableObject
{
    public float timeWindowStart;                 // 分钟
    public float timeWindowEnd;                   // 分钟
    public SpawnEntry[] spawnTable;
}
