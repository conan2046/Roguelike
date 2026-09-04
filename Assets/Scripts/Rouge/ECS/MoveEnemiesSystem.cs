using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Rouge.ECS
{
    /// <summary>
    /// M1.1 验证系统：让所有 EnemyTag 实体朝 -z 匀速移动，证明
    /// Translation 写入 → TransformSystemGroup → Entities Graphics 渲染更新链路。
    /// M1.2 起替换为「朝玩家移动 / 寻路」逻辑。
    /// </summary>
    public partial class MoveEnemiesSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;
            Entities.WithAll<EnemyTag>().ForEach(
                (ref Translation t, in MoveSpeed s) =>
                {
                    t.Value += new float3(0f, 0f, -s.Value * dt);
                }).Schedule();
        }
    }
}
