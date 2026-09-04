using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ProjectX.Migration
{
    [Serializable]
    public sealed class CocosMountData
    {
        public int id;
        public string name;
        public int type;
        public int isSameState;
        public string png;
        public string ani;
        public int isStandState;

        public bool SharesStandAndRun => isSameState != 0;

        public static Dictionary<int, CocosMountData> LoadAll()
        {
            string path = Path.Combine(
                Application.streamingAssetsPath,
                CocosAniPlayer.StreamingRoot,
                "ConfigData/mount_client.dat");
            if (!File.Exists(path))
                throw new FileNotFoundException("Cocos mount configuration was not found.", path);

            byte[] bytes = File.ReadAllBytes(path);
            Dictionary<int, CocosMountData> result = new Dictionary<int, CocosMountData>();
            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, false))
            {
                uint count = reader.ReadUInt32();
                for (int i = 0; i < count; i++)
                {
                    CocosMountData data = new CocosMountData
                    {
                        id = checked((int)reader.ReadUInt32()),
                        name = ReadUtf8(reader),
                        type = checked((int)reader.ReadUInt32()),
                        isSameState = checked((int)reader.ReadUInt32()),
                        png = ReadUtf8(reader),
                        ani = ReadUtf8(reader),
                        isStandState = checked((int)reader.ReadUInt32())
                    };
                    result.Add(data.id, data);
                }
                if (stream.Position != stream.Length)
                    throw new InvalidOperationException("mount_client.dat contains unread trailing bytes.");
            }
            return result;
        }

        private static string ReadUtf8(BinaryReader reader)
        {
            ushort length = reader.ReadUInt16();
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException("mount_client.dat string ended unexpectedly.");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
