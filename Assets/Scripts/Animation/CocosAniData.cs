using System;

namespace ProjectX.Migration
{
    /// <summary>共享的旧 ANI 二进制结构；保留命名空间以兼容既有 Kapai 播放器。</summary>
    [Serializable]
    public sealed class CocosAniData
    {
        public const float FrameRate = 1f / 30f;

        [Serializable]
        public struct Module
        {
            public short x;
            public short y;
            public short width;
            public short height;
        }

        [Serializable]
        public struct FrameModule
        {
            public short x;
            public short y;
            public byte moduleId;
            public byte flags;
        }

        [Serializable]
        public sealed class Frame
        {
            public FrameModule[] modules = Array.Empty<FrameModule>();
        }

        [Serializable]
        public sealed class Action
        {
            public byte[] frames = Array.Empty<byte>();
            public byte[] durations = Array.Empty<byte>();
        }

        public Module[] modules = Array.Empty<Module>();
        public Frame[] frames = Array.Empty<Frame>();
        public Action[] actions = Array.Empty<Action>();
        public byte sourceTickUnits = 5;
        public float SourceTickDuration => (sourceTickUnits == 1 ? 5 : Math.Max(1, (int)sourceTickUnits)) * FrameRate;

        /// <summary>资源加载后按字节计数和小端有符号坐标解析模块、帧和动作；不猜测图集排列。</summary>
        /// <param name="bytes">通过资源入口取得的原始 ANI。</param>
        /// <returns>完整解析且引用合法的数据。</returns>
        /// <exception cref="ArgumentException">输入为空。</exception>
        /// <exception cref="InvalidOperationException">数据截断、残留或引用非法。</exception>
        public static CocosAniData Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("ANI data is empty.", nameof(bytes));

            Reader reader = new Reader(bytes);
            CocosAniData data = new CocosAniData();

            int moduleCount = reader.ReadByte();
            data.modules = new Module[moduleCount];
            for (int i = 0; i < moduleCount; i++)
            {
                data.modules[i] = new Module
                {
                    x = reader.ReadInt16(),
                    y = reader.ReadInt16(),
                    width = reader.ReadInt16(),
                    height = reader.ReadInt16()
                };
            }

            int frameCount = reader.ReadByte();
            data.frames = new Frame[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                int count = reader.ReadByte();
                FrameModule[] modules = new FrameModule[count];
                for (int j = 0; j < count; j++)
                {
                    modules[j] = new FrameModule
                    {
                        x = reader.ReadInt16(),
                        y = reader.ReadInt16(),
                        moduleId = reader.ReadByte(),
                        flags = reader.ReadByte()
                    };
                }
                data.frames[i] = new Frame { modules = modules };
            }

            int actionCount = reader.ReadByte();
            data.actions = new Action[actionCount];
            for (int i = 0; i < actionCount; i++)
            {
                int count = reader.ReadByte();
                byte[] actionFrames = new byte[count];
                byte[] durations = new byte[count];
                for (int j = 0; j < count; j++)
                {
                    actionFrames[j] = reader.ReadByte();
                    durations[j] = reader.ReadByte();
                    data.sourceTickUnits = durations[j];
                }
                data.actions[i] = new Action { frames = actionFrames, durations = durations };
            }

            data.Validate(reader.Position, bytes.Length);
            return data;
        }

        /// <summary>仅供遗留 Kapai 播放器复现旧时钟规则；新战斗播放不得以此代替 Luban 时间配置。</summary>
        /// <param name="actionIndex">已验证的动作索引。</param>
        /// <param name="actionFrameIndex">动作内帧索引。</param>
        /// <returns>旧播放器兼容的帧秒数。</returns>
        /// <exception cref="IndexOutOfRangeException">索引非法。</exception>
        public float GetFrameDuration(int actionIndex, int actionFrameIndex)
        {
            Action action = actions[actionIndex];
            int duration = action.durations[actionFrameIndex];
            if (duration == 1)
                duration = 5; // Matches the legacy compatibility rule in ImodAnim.cpp.
            return Math.Max(1, duration) * FrameRate;
        }

        /// <summary>解析结束后检查消费长度与跨表索引，拒绝部分成功的数据。</summary>
        /// <param name="consumedBytes">已消费字节数。</param>
        /// <param name="totalBytes">资源总字节数。</param>
        /// <exception cref="InvalidOperationException">结构为空或引用非法。</exception>
        private void Validate(int consumedBytes, int totalBytes)
        {
            if (consumedBytes != totalBytes)
                throw new InvalidOperationException("ANI has " + (totalBytes - consumedBytes) + " unread trailing bytes.");
            if (modules.Length == 0 || frames.Length == 0 || actions.Length == 0)
                throw new InvalidOperationException("ANI must contain modules, frames and actions.");

            for (int i = 0; i < frames.Length; i++)
            {
                foreach (FrameModule frameModule in frames[i].modules)
                {
                    if (frameModule.moduleId >= modules.Length)
                        throw new InvalidOperationException("ANI frame " + i + " references missing module " + frameModule.moduleId + ".");
                }
            }
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].frames.Length == 0)
                    throw new InvalidOperationException("ANI action " + i + " is empty.");
                foreach (byte frame in actions[i].frames)
                {
                    if (frame >= frames.Length)
                        throw new InvalidOperationException("ANI action " + i + " references missing frame " + frame + ".");
                }
            }
        }

        private sealed class Reader
        {
            private readonly byte[] bytes;
            private int position;

            /// <summary>为一次解析建立独立游标，不共享可变读取状态。</summary>
            /// <param name="value">已检查非空的源字节。</param>
            public Reader(byte[] value) => bytes = value;
            public int Position => position;

            /// <summary>解析计数或索引时先检查剩余长度再推进一个字节。</summary>
            /// <returns>无符号字节。</returns>
            /// <exception cref="InvalidOperationException">资源截断。</exception>
            public byte ReadByte()
            {
                Require(1);
                return bytes[position++];
            }

            /// <summary>解析图集矩形及帧偏移时读取小端有符号短整数。</summary>
            /// <returns>保留负偏移的有符号值。</returns>
            /// <exception cref="InvalidOperationException">资源截断。</exception>
            public short ReadInt16()
            {
                Require(2);
                short value = (short)(bytes[position] | (bytes[position + 1] << 8));
                position += 2;
                return value;
            }

            /// <summary>每次读取前防止游标越过文件末尾。</summary>
            /// <param name="count">即将消费的字节数。</param>
            /// <exception cref="InvalidOperationException">资源截断。</exception>
            private void Require(int count)
            {
                if (position + count > bytes.Length)
                    throw new InvalidOperationException("ANI ended unexpectedly at byte " + position + ".");
            }
        }
    }
}
