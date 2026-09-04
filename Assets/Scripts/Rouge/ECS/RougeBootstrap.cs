using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace Rouge.ECS
{
    /// <summary>
    /// M1.1 桥梁：在默认 ECS World 中生成实例化 quad 敌人，验证
    /// Unity.Entities + Entities Graphics 渲染管线是否打通。
    /// 仅做管线 proof-of-concept，不实现玩法（玩法在 M1.2+）。
    /// </summary>
    public class RougeBootstrap : MonoBehaviour
    {
        [Header("ECS 渲染资源（Editor 中赋值 URP + DOTS Instancing 材质）")]
        public Mesh enemyMesh;
        public Material enemyMaterial;
        public int enemyCount = 200;

        void Start()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) { Debug.LogError("[Rouge] Default ECS World 未就绪"); return; }
            var em = world.EntityManager;

            // Entities Graphics 渲染描述：mesh + 支持 DOTS Instancing 的 URP 材质
            var desc = new RenderMeshDescription(enemyMesh, enemyMaterial);

            for (int i = 0; i < enemyCount; i++)
            {
                Entity e = em.CreateEntity();
                em.AddComponentData(e, new Translation
                {
                    Value = new float3(Random.Range(-12f, 12f), 0f, Random.Range(-12f, 12f))
                });
                em.AddComponentData(e, new Rotation { Value = quaternion.identity });
                em.AddComponentData(e, new MoveSpeed { Value = Random.Range(1.5f, 3f) });
                em.AddComponent(e, typeof(EnemyTag));
                // 渲染相关组件由 AddComponents 注入（RenderMesh / RenderBounds / LocalToWorld / MaterialMeshInfo）
                RenderMeshUtility.AddComponents(e, em, desc);
            }

            Debug.Log($"[Rouge] M1.1 已生成 {enemyCount} 个 ECS 敌人实体（实例化 quad 管线验证）");
        }
    }
}
