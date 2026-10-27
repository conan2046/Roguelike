using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using cfg;
using NUnit.Framework;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>CPU改造前后逐Tick位级指纹；独立专项只写.local证据，不改变正式配置。</summary>
    public sealed class CombatCpuTrajectoryTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>专项通过现有测试资源替身创建正式关卡，记录出生、外部写入、攻击、事件、重开后的逐Tick状态。</summary>
        /// <remarks>只复用既有测试的私有装载方法，防止新增正式资源入口；创建/释放隔离World与会话，覆盖.local中的最新轨迹。</remarks>
        [Test, Explicit("CPU修复位级轨迹专项，由MCP独立选择；修复前后文件另存比较。")]
        public void CaptureFormalTrajectory()
        {
            var tables = (Tables)typeof(CombatSessionTests).GetMethod("LoadTables", PrivateStatic).Invoke(null, new object[] { false });
            var definition = CombatRunDefinition.Create(tables, 1);
            var factory = typeof(CombatSessionTests).GetMethods(PrivateStatic).Single(method =>
                method.Name == "CreateSession" && method.GetParameters().Length == 3);
            using var world = new World("CPU trajectory isolated world");
            using var session = (CombatSession)factory.Invoke(null, new object[] { world, definition, tables });
            var settings = (CombatSessionSettings)typeof(CombatSession).GetField("settings", PrivateInstance).GetValue(session);
            typeof(CombatSessionSettings).GetProperty("Arena").SetValue(settings, new float2(40));
            typeof(CombatSessionSettings).GetProperty("RestorePlayerAfterDamage").SetValue(settings, true);
            typeof(CombatSessionSettings).GetProperty("ReplenishOnDeath").SetValue(settings, true);
            var monsters = definition.Monsters.Where(monster => !monster.IsElite).Take(8).ToArray();
            for (int index = 0; index < 48; index++)
                Assert.That(session.TrySpawnMonster(monsters[index % monsters.Length], 10000 + index * 1000,
                    (ulong)index + 1, false, out _), Is.True);
            float span = 0;
            for (int slot = 0; slot < session.UnitCount; slot++)
            {
                var body = session.ReadUnit(slot).Movement;
                span = math.max(span, 2 * (body.Radius + body.HalfSegment2D + math.length(body.Offset)));
            }
            float spacing = span + 0.25f;
            var templates = (NativeList<CombatUnit>)typeof(CombatSession).GetField("templates", PrivateInstance).GetValue(session);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                int column = (slot - 1) % 8, row = (slot - 1) / 8;
                unit.Position = new float2((column - 3.5f) * spacing, (row + 1.5f) * spacing);
                unit.PreviousPosition = unit.Position; unit.SpawnPosition = unit.Position;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
                templates[slot] = unit;
            }
            for (int slot = 0; slot < session.UnitCount; slot++)
            {
                CombatUnit unit = session.ReadUnit(slot);
                float2 center = unit.Position + unit.Movement.Offset;
                float2 extent = new float2(unit.Movement.Radius, unit.Movement.Radius + unit.Movement.HalfSegment2D);
                Assert.That(math.all(math.abs(center) + extent <= settings.Arena), Is.True, "initial arena slot " + slot);
                for (int otherSlot = 0; otherSlot < slot; otherSlot++)
                {
                    CombatUnit other = session.ReadUnit(otherSlot);
                    float radius = unit.Movement.Radius + other.Movement.Radius;
                    Assert.That(CombatGeometry.VerticalSegmentDistanceSquared(center - other.Position - other.Movement.Offset,
                        unit.Movement.HalfSegment2D + other.Movement.HalfSegment2D), Is.GreaterThanOrEqualTo(radius * radius),
                        "initial overlap " + slot + "/" + otherSlot);
                }
            }
            var lines = new List<string>();
            using var hash = SHA256.Create();
            for (int tick = 0; tick < 600; tick++)
            {
                if (tick == 400) session.Restart();
                float2 input = tick < 200 ? float2.zero : new float2(tick < 300 ? 1 : -1, 0.5f);
                session.Advance(session.StepSeconds, input);
                using var memory = new MemoryStream();
                using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                {
                    WriteValue(writer, session.Statistics);
                    for (int slot = 0; slot < session.UnitCount; slot++) WriteValue(writer, session.ReadUnit(slot));
                    for (int slot = 0; slot < session.ProjectileCapacity; slot++) WriteValue(writer, session.ReadProjectile(slot));
                    for (int slot = 0; slot < session.PlayerWeaponCount; slot++) WriteValue(writer, session.ReadPlayerWeapon(slot));
                    var persistent = (NativeList<CombatPersistentArea>)typeof(CombatSession).GetField("persistentAreas", PrivateInstance).GetValue(session);
                    writer.Write(persistent.Length);
                    for (int index = 0; index < persistent.Length; index++) WriteValue(writer, persistent[index]);
                    writer.Write(session.ImpactCount); writer.Write(session.AreaCount); writer.Write(session.DeathCount);
                    for (int index = 0; index < session.ImpactCount; index++) WriteValue(writer, session.ReadImpact(index));
                    for (int index = 0; index < session.AreaCount; index++) WriteValue(writer, session.ReadArea(index));
                    for (int index = 0; index < session.DeathCount; index++) WriteValue(writer, session.ReadDeath(index));
                }
                lines.Add(tick + " " + BitConverter.ToString(hash.ComputeHash(memory.ToArray())).Replace("-", ""));
            }
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../.local/performance-20261025/cpu-repair/trajectory-latest.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllLines(path, lines);
            Assert.That(lines.Count, Is.EqualTo(600));
        }

        /// <summary>测试按字段名序递归写入原始数值，避免JSON浮点舍入和结构体填充字节造成伪一致或伪差异。</summary>
        /// <param name="writer">本Tick的内存写入器。</param><param name="value">完整战斗值类型；诊断Movement计数不参与行为指纹。</param>
        /// <remarks>仅维护测试反射缓存；不包含World实体标识或原生容器头。</remarks>
        /// <exception cref="InvalidOperationException">出现未支持的引用类型时立即失败。</exception>
        private static void WriteValue(BinaryWriter writer, object value)
        {
            Type type = value.GetType();
            if (type.IsEnum) { writer.Write(Convert.ToInt32(value)); return; }
            if (value is bool b) { writer.Write(b); return; }
            if (value is byte by) { writer.Write(by); return; }
            if (value is int i) { writer.Write(i); return; }
            if (value is uint ui) { writer.Write(ui); return; }
            if (value is long l) { writer.Write(l); return; }
            if (value is ulong ul) { writer.Write(ul); return; }
            if (value is float f) { writer.Write(f); return; }
            if (value is double d) { writer.Write(d); return; }
            if (value is FixedList128Bytes<ulong> lifetimes)
            {
                writer.Write(lifetimes.Length);
                for (int index = 0; index < lifetimes.Length; index++) writer.Write(lifetimes[index]);
                return;
            }
            if (!type.IsValueType) throw new InvalidOperationException("Unsupported trajectory type " + type);
            if (!Fields.TryGetValue(type, out FieldInfo[] fields))
            {
                fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.Name).ToArray();
                Fields.Add(type, fields);
            }
            foreach (FieldInfo field in fields)
                if (type != typeof(CombatCounters) || !field.Name.StartsWith("Movement", StringComparison.Ordinal))
                    WriteValue(writer, field.GetValue(value));
        }
    }
}
