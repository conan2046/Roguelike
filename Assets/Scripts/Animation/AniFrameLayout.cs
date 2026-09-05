using System;
using ProjectX.Migration;

namespace Roguelike.Animation
{
    /// <summary>一块模块的图集 UV 与相对锚点像素几何；不依赖 Unity 或旧 UI 播放器。</summary>
    public struct AniQuad
    {
        public float U, V, UWidth, VHeight;
        public float CenterX, CenterY, Width, Height;
    }

    /// <summary>旧 ANI 到渲染几何的共享出口，保留每帧模块顺序、负偏移及翻转语义。</summary>
    public static class AniFrameLayout
    {
        /// <summary>加载配对纹理后检查所有矩形和标志，不允许越界裁切被纹理 Clamp 掩盖。</summary>
        /// <param name="data">共享解析器产出的完整 ANI。</param>
        /// <param name="textureWidth">配对纹理实际宽度。</param>
        /// <param name="textureHeight">配对纹理实际高度。</param>
        /// <exception cref="InvalidOperationException">矩形越界、尺寸非法或存在尚未支持的翻转标志。</exception>
        public static void ValidateAtlas(CocosAniData data, int textureWidth, int textureHeight)
        {
            if (data == null || textureWidth <= 0 || textureHeight <= 0) throw new InvalidOperationException("Missing ANI or texture dimensions.");
            foreach (var module in data.modules)
                if (module.x < 0 || module.y < 0 || module.width <= 0 || module.height <= 0 ||
                    module.x + module.width > textureWidth || module.y + module.height > textureHeight)
                    throw new InvalidOperationException($"ANI rectangle {module.x},{module.y},{module.width},{module.height} is outside texture {textureWidth}x{textureHeight}.");
            foreach (var frame in data.frames)
                foreach (var module in frame.modules)
                    if ((module.flags & ~3) != 0) throw new InvalidOperationException("Unsupported ANI module flags.");
        }

        /// <summary>资源预处理时为一个动作帧生成全部模块，不将多模块帧截成第一个模块。</summary>
        /// <param name="data">已验证的 ANI。</param>
        /// <param name="actionIndex">调用方明确选择的动作索引，不推测方向映射。</param>
        /// <param name="actionFrame">动作内帧索引。</param>
        /// <param name="textureWidth">配对纹理宽。</param>
        /// <param name="textureHeight">配对纹理高。</param>
        /// <returns>保持原模块顺序的 UV 与像素几何数组。</returns>
        /// <exception cref="ArgumentOutOfRangeException">动作或帧索引非法。</exception>
        public static AniQuad[] Build(CocosAniData data, int actionIndex, int actionFrame, int textureWidth, int textureHeight)
        {
            if (actionIndex < 0 || actionIndex >= data.actions.Length) throw new ArgumentOutOfRangeException(nameof(actionIndex));
            var action = data.actions[actionIndex];
            if (actionFrame < 0 || actionFrame >= action.frames.Length) throw new ArgumentOutOfRangeException(nameof(actionFrame));
            var frame = data.frames[action.frames[actionFrame]];
            var result = new AniQuad[frame.modules.Length];
            for (int index = 0; index < result.Length; index++)
            {
                var part = frame.modules[index];
                var module = data.modules[part.moduleId];
                float u = module.x / (float)textureWidth, v = 1f - (module.y + module.height) / (float)textureHeight;
                float width = module.width / (float)textureWidth, height = module.height / (float)textureHeight;
                // 文件标志沿用旧播放器：bit 1 为水平翻转，bit 0 为垂直翻转。
                if ((part.flags & 2) != 0) { u += width; width = -width; }
                if ((part.flags & 1) != 0) { v += height; height = -height; }
                result[index] = new AniQuad { U = u, V = v, UWidth = width, VHeight = height,
                    CenterX = part.x + module.width * 0.5f, CenterY = -part.y - module.height * 0.5f,
                    Width = module.width, Height = module.height };
            }
            return result;
        }
    }
}
