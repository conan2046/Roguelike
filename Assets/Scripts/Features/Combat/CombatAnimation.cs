using System;
using cfg;
using ProjectX.Migration;

namespace Roguelike.Features.Combat
{
    /// <summary>加载期预计算的 ANI 时序；以模拟时间采样，不依赖 Unity 墙钟。</summary>
    public sealed class CombatAnimation
    {
        private readonly byte[][] frames;
        private readonly double[][] ends;
        private readonly bool loop;

        /// <summary>资源加载后复制帧索引并按 TbCombatPresentation 的时钟及兼容单位建立累计时长。</summary>
        /// <param name="data">已完整解析的 ANI 数据。</param>
        /// <param name="config">表现表 sourceTickHz、durationOneUnits、minDurationUnits。</param>
        /// <param name="loop">TbAnimationClip 的循环标记。</param>
        /// <exception cref="ArgumentNullException">缺少资源或配置。</exception>
        /// <exception cref="ArgumentException">时序或帧引用非法。</exception>
        public CombatAnimation(CocosAniData data, CombatPresentationConfig config, bool loop)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.SourceTickHz <= 0 || config.DurationOneUnits <= 0 || config.MinDurationUnits <= 0 ||
                data.actions == null || data.actions.Length == 0 || data.frames == null)
                throw new ArgumentException("Invalid ANI timing configuration.");
            this.loop = loop;
            frames = new byte[data.actions.Length][];
            ends = new double[data.actions.Length][];
            for (int a = 0; a < frames.Length; a++)
            {
                var action = data.actions[a];
                if (action == null || action.frames == null || action.durations == null ||
                    action.frames.Length == 0 || action.frames.Length != action.durations.Length)
                    throw new ArgumentException("Invalid ANI action.", nameof(data));
                frames[a] = (byte[])action.frames.Clone();
                ends[a] = new double[action.frames.Length];
                long units = 0;
                for (int f = 0; f < action.frames.Length; f++)
                {
                    if (frames[a][f] >= data.frames.Length)
                        throw new ArgumentException("Invalid ANI frame reference.", nameof(data));
                    int raw = action.durations[f];
                    // The value one is an ANI compatibility sentinel, not a gameplay duration.
                    units += Math.Max(config.MinDurationUnits, raw == 1 ? config.DurationOneUnits : raw);
                    ends[a][f] = (double)units / config.SourceTickHz;
                }
            }
        }

        public int ActionCount => frames.Length;

        /// <summary>加载攻击时间线时读取指定动作帧的进入秒数，不复制 ANI 时长到业务表。</summary>
        /// <param name="actionIndex">动作组索引。</param>
        /// <param name="frameIndex">TbAttackDirection.hitFrameIndex。</param>
        /// <returns>按 TbCombatPresentation 时钟计算的原速进入时间。</returns>
        /// <exception cref="ArgumentOutOfRangeException">索引非法。</exception>
        public double FrameStartSeconds(int actionIndex, int frameIndex)
        {
            HoldFrame(actionIndex, frameIndex);
            return frameIndex == 0 ? 0 : ends[actionIndex][frameIndex - 1];
        }

        /// <summary>加载攻击时间线时读取原速动作总时长，供后摇结束与表现采样共同使用。</summary>
        /// <param name="actionIndex">攻击映射中的动作索引。</param>
        /// <returns>完整播放秒数。</returns>
        /// <exception cref="ArgumentOutOfRangeException">索引非法。</exception>
        public double DurationSeconds(int actionIndex)
        {
            ValidateAction(actionIndex);
            return ends[actionIndex][ends[actionIndex].Length - 1];
        }

        /// <summary>每次渲染按动作内模拟秒数二分采样；恰到边界使用下一帧，非循环结束保持末帧。</summary>
        /// <param name="actionIndex">方向表 actionIndex。</param>
        /// <param name="elapsedSeconds">状态开始后累计的非负模拟时间；暂停时调用方不推进。</param>
        /// <returns>ANI 全局帧索引。</returns>
        /// <exception cref="ArgumentOutOfRangeException">动作索引或时间非法。</exception>
        public int Sample(int actionIndex, double elapsedSeconds)
        {
            ValidateAction(actionIndex);
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            double[] actionEnds = ends[actionIndex];
            double duration = actionEnds[actionEnds.Length - 1];
            double time = loop ? elapsedSeconds % duration : elapsedSeconds;
            int low = 0, high = actionEnds.Length - 1;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (time < actionEnds[middle]) high = middle;
                else low = middle + 1;
            }
            return frames[actionIndex][low];
        }

        /// <summary>无站立片段时读取 TbCombatPresentation.idleFrameIndex 指定的动作内静止帧。</summary>
        /// <param name="actionIndex">方向表动作索引。</param>
        /// <param name="actionFrameIndex">表现表静止帧索引，而非 ANI 全局帧索引。</param>
        /// <returns>对应 ANI 全局帧索引。</returns>
        /// <exception cref="ArgumentOutOfRangeException">任一索引越界。</exception>
        public int HoldFrame(int actionIndex, int actionFrameIndex)
        {
            ValidateAction(actionIndex);
            if ((uint)actionFrameIndex >= (uint)frames[actionIndex].Length)
                throw new ArgumentOutOfRangeException(nameof(actionFrameIndex));
            return frames[actionIndex][actionFrameIndex];
        }

        /// <summary>采样前统一拒绝未包含在当前 ANI 中的方向动作。</summary>
        /// <param name="actionIndex">待检查索引。</param>
        /// <exception cref="ArgumentOutOfRangeException">索引越界。</exception>
        private void ValidateAction(int actionIndex)
        {
            if ((uint)actionIndex >= (uint)frames.Length)
                throw new ArgumentOutOfRangeException(nameof(actionIndex));
        }
    }

    /// <summary>由 Luban 方向表构建的只读映射，可由全部角色共享。</summary>
    public sealed class CombatDirections
    {
        private readonly AnimationDirectionConfig[] directions;
        private readonly double[] x;
        private readonly double[] y;
        public int InitialIndex { get; }

        /// <summary>初始化时归一化 TbAnimationDirection 向量，并检查初始方向及动作引用。</summary>
        /// <param name="config">表现表 directionIds 和 initialDirectionId，已解析外键。</param>
        /// <param name="animation">当前角色动画，校验 actionIndex 和 idleFrameIndex。</param>
        /// <exception cref="ArgumentNullException">缺少依赖。</exception>
        /// <exception cref="ArgumentException">方向为空、重复、非法或初始方向不在列表中。</exception>
        public CombatDirections(CombatPresentationConfig config, CombatAnimation animation)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (animation == null) throw new ArgumentNullException(nameof(animation));
            if (config.DirectionIds_Ref == null || config.DirectionIds_Ref.Count == 0)
                throw new ArgumentException("Presentation has no resolved directions.", nameof(config));
            directions = config.DirectionIds_Ref.ToArray();
            x = new double[directions.Length];
            y = new double[directions.Length];
            int initial = -1;
            for (int i = 0; i < directions.Length; i++)
            {
                var d = directions[i];
                if (d == null) throw new ArgumentException("Missing direction reference.", nameof(config));
                double length = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y);
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length))
                    throw new ArgumentException("Direction must be finite and nonzero.", nameof(config));
                x[i] = d.X / length;
                y[i] = d.Y / length;
                for (int previous = 0; previous < i; previous++)
                    if (directions[previous].Id == d.Id || (x[previous] == x[i] && y[previous] == y[i]))
                        throw new ArgumentException("Duplicate direction.", nameof(config));
                animation.HoldFrame(d.ActionIndex, config.IdleFrameIndex);
                if (d.Id == config.InitialDirectionId) initial = i;
            }
            if (initial < 0) throw new ArgumentException("Initial direction is not in directionIds.", nameof(config));
            InitialIndex = initial;
        }

        /// <summary>移动后按最大点积选方向；静止保留上一次朝向，等角边界按表内顺序稳定选择。</summary>
        /// <param name="moveX">模拟位移横分量。</param>
        /// <param name="moveY">模拟位移纵分量。</param>
        /// <param name="previousIndex">上次方向，出生时使用 InitialIndex。</param>
        /// <returns>方向数组索引，可传入 Read。</returns>
        /// <exception cref="ArgumentOutOfRangeException">输入非有限或历史索引越界。</exception>
        public int Select(float moveX, float moveY, int previousIndex)
        {
            Read(previousIndex);
            if (float.IsNaN(moveX) || float.IsInfinity(moveX) || float.IsNaN(moveY) || float.IsInfinity(moveY))
                throw new ArgumentOutOfRangeException(nameof(moveX));
            if (moveX == 0 && moveY == 0) return previousIndex;
            int best = 0;
            double score = double.NegativeInfinity;
            for (int i = 0; i < directions.Length; i++)
            {
                double dot = moveX * x[i] + moveY * y[i];
                if (dot > score) { score = dot; best = i; }
            }
            return best;
        }

        /// <summary>渲染时取得已选择方向的动作索引及水平镜像标志，不复制或猜测映射。</summary>
        /// <param name="index">Select 或 InitialIndex 返回的索引。</param>
        /// <returns>对应 TbAnimationDirection 行。</returns>
        /// <exception cref="ArgumentOutOfRangeException">索引越界。</exception>
        public AnimationDirectionConfig Read(int index)
        {
            if ((uint)index >= (uint)directions.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return directions[index];
        }
    }
}
