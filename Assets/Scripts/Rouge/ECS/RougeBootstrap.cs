using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

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

            if (enemyMesh == null || enemyMaterial == null)
            {
                Debug.LogError("[Rouge] enemyMesh / enemyMaterial 未配置，无法创建 ECS 渲染实体", this);
                return;
            }

            // Entities Graphics 1.0：运行时实体通过 RenderMeshArray 索引材质与网格。
            var desc = new RenderMeshDescription(
                ShadowCastingMode.Off,
                receiveShadows: false);
            var renderMeshArray = new RenderMeshArray(
                new[] { enemyMaterial },
                new[] { enemyMesh });
            var materialMeshInfo = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);

            for (int i = 0; i < enemyCount; i++)
            {
                Entity e = em.CreateEntity();
                em.AddComponentData(e, LocalTransform.FromPositionRotationScale(
                    new float3(
                        UnityEngine.Random.Range(-12f, 12f),
                        0f,
                        UnityEngine.Random.Range(-12f, 12f)),
                    quaternion.identity,
                    1f));
                em.AddComponentData(e, new MoveSpeed { Value = UnityEngine.Random.Range(1.5f, 3f) });
                em.AddComponent(e, typeof(EnemyTag));
                RenderMeshUtility.AddComponents(e, em, desc, renderMeshArray, materialMeshInfo);
            }

            Debug.Log($"[Rouge] M1.1 已生成 {enemyCount} 个 ECS 敌人实体（实例化 quad 管线验证）");
        }
    }
}
