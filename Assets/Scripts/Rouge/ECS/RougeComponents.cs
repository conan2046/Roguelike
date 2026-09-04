using Unity.Entities;

namespace Rouge.ECS
{
    /// <summary>M1 通用 ECS 组件（IComponentData）。数据层与渲染层共享同一套组件。</summary>

    public struct EnemyTag : IComponentData { }
    public struct PlayerTag : IComponentData { }

    public struct MoveSpeed : IComponentData { public float Value; }
    public struct Health : IComponentData { public float Value; }
    public struct Damage : IComponentData { public float Value; }
    public struct AttackTimer : IComponentData { public float Value; }
}
