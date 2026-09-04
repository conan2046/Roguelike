using System;
using System.Collections.Generic;

namespace ProjectX.Migration
{
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

        public float GetFrameDuration(int actionIndex, int actionFrameIndex)
        {
            Action action = actions[actionIndex];
            int duration = action.durations[actionFrameIndex];
            if (duration == 1)
                duration = 5; // Matches the legacy compatibility rule in ImodAnim.cpp.
            return Math.Max(1, duration) * FrameRate;
        }

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

            public Reader(byte[] value) => bytes = value;
            public int Position => position;

            public byte ReadByte()
            {
                Require(1);
                return bytes[position++];
            }

            public short ReadInt16()
            {
                Require(2);
                short value = (short)(bytes[position] | (bytes[position + 1] << 8));
                position += 2;
                return value;
            }

            private void Require(int count)
            {
                if (position + count > bytes.Length)
                    throw new InvalidOperationException("ANI ended unexpectedly at byte " + position + ".");
            }
        }
    }
}
