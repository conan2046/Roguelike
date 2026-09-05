using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Roguelike.Animation.Rendering
{
    /// <summary>共享的 ANI 帧到 Unity 渲染出口；网格可在资源加载时预建并被多个实体共享。</summary>
    public static class AniMeshFactory
    {
        /// <summary>资源准备阶段将完整模块布局转换为带锚点的世界单位网格，不在播放帧循环创建网格。</summary>
        /// <param name="quads">共享布局解析结果，包含所有模块与翻转 UV。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel 与 TbVisualSet.scalePermille 换算后的比例。</param>
        /// <returns>供实例化绘制共享的只读网格。</returns>
        /// <remarks>创建 Unity Mesh，调用方持有并在所有实例释放后销毁；不修改源纹理。</remarks>
        /// <exception cref="ArgumentException">帧为空或比例非法。</exception>
        public static Mesh CreateFrame(AniQuad[] quads, float worldUnitsPerPixel)
        {
            if (quads == null || quads.Length == 0 || float.IsNaN(worldUnitsPerPixel) || float.IsInfinity(worldUnitsPerPixel) || worldUnitsPerPixel <= 0)
                throw new ArgumentException("ANI frame and pixel scale must be valid.");
            var vertices = new Vector3[quads.Length * 4];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[quads.Length * 6];
            for (int index = 0; index < quads.Length; index++)
            {
                var quad = quads[index];
                float left = (quad.CenterX - quad.Width * 0.5f) * worldUnitsPerPixel;
                float right = (quad.CenterX + quad.Width * 0.5f) * worldUnitsPerPixel;
                float bottom = (quad.CenterY - quad.Height * 0.5f) * worldUnitsPerPixel;
                float top = (quad.CenterY + quad.Height * 0.5f) * worldUnitsPerPixel;
                int vertex = index * 4, triangle = index * 6;
                vertices[vertex] = new Vector3(left, bottom);
                vertices[vertex + 1] = new Vector3(left, top);
                vertices[vertex + 2] = new Vector3(right, top);
                vertices[vertex + 3] = new Vector3(right, bottom);
                uv[vertex] = new Vector2(quad.U, quad.V);
                uv[vertex + 1] = new Vector2(quad.U, quad.V + quad.VHeight);
                uv[vertex + 2] = new Vector2(quad.U + quad.UWidth, quad.V + quad.VHeight);
                uv[vertex + 3] = new Vector2(quad.U + quad.UWidth, quad.V);
                triangles[triangle] = vertex; triangles[triangle + 1] = vertex + 1; triangles[triangle + 2] = vertex + 2;
                triangles[triangle + 3] = vertex; triangles[triangle + 4] = vertex + 2; triangles[triangle + 5] = vertex + 3;
            }
            var mesh = new Mesh { name = "ANI frame", indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
