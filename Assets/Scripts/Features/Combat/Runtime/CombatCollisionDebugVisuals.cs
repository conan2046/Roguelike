#if ROGUELIKE_COMBAT_COLLISION_DEBUG
using System;
using System.Collections.Generic;
using cfg;
using Roguelike.Features.Combat.Ecs;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>在运行时直接绘制战斗模拟使用的单位、移动、弹丸和范围技能碰撞圆。</summary>
    /// <remarks>仅在 ROGUELIKE_COMBAT_COLLISION_DEBUG 宏启用时编译；对象只存在于当前 Play 生命周期，不保存任何预览资产。</remarks>
    internal sealed class CombatCollisionDebugVisuals : IDisposable
    {
        private const int CircleSegments = 64;
        private const float AreaTraceSeconds = 0.4f;
        private const float DebugDepth = -4f;
        private const int MovementSortingOrder = 32000;
        private const int BodySortingOrder = 32010;
        private const int AreaSortingOrder = 32020;
        private const int ProjectileSortingOrder = 32030;

        private static readonly Color PlayerBodyColor = new Color(0f, 0.9f, 1f, 1f);
        private static readonly Color MonsterBodyColor = new Color(1f, 0.15f, 0.15f, 1f);
        private static readonly Color MovementColor = new Color(0.2f, 1f, 0.25f, 0.65f);
        private static readonly Color ProjectileColor = new Color(1f, 0.9f, 0f, 1f);
        private static readonly Color AreaColor = new Color(1f, 0f, 1f, 1f);

        private readonly CombatSession session;
        private readonly GameObject root;
        private readonly Material material;
        private readonly float lineWidth;
        private readonly Vector3[] circlePositions = new Vector3[CircleSegments];
        private readonly List<LineRenderer> bodyLines = new List<LineRenderer>();
        private readonly List<LineRenderer> movementLines = new List<LineRenderer>();
        private readonly List<LineRenderer> projectileLines = new List<LineRenderer>();
        private readonly List<LineRenderer> areaLines = new List<LineRenderer>();
        private readonly List<AreaTrace> areaTraces = new List<AreaTrace>();
        private readonly Dictionary<int, float> areaRadii = new Dictionary<int, float>();
        private bool disposed;

        /// <summary>创建本局临时线框根节点并捕获像素线宽，不修改战斗数据。</summary>
        /// <param name="session">提供真实 ECS 碰撞状态的当前战斗会话。</param>
        /// <param name="rules">提供逻辑像素到渲染坐标比例的 TbCombatRules。</param>
        /// <param name="parent">承载本局临时对象的 CombatRuntimeRunner。</param>
        /// <exception cref="ArgumentNullException">会话、规则或父节点为空。</exception>
        /// <exception cref="InvalidOperationException">调试线材质着色器不可用或像素比例非法。</exception>
        /// <remarks>创建的 GameObject、Material 和 LineRenderer 均由本对象在战斗关闭时销毁。</remarks>
        public CombatCollisionDebugVisuals(CombatSession session, CombatRulesConfig rules, Transform parent)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (!(rules.WorldUnitsPerPixel > 0f))
                throw new InvalidOperationException("TbCombatRules.worldUnitsPerPixel must be positive for collision debug drawing.");

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("Sprites/Default shader is required for collision debug drawing.");
            material = new Material(shader)
            {
                name = "Combat Collision Debug (Runtime Only)",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 5000
            };
            lineWidth = rules.WorldUnitsPerPixel * 2f;
            root = new GameObject("战斗碰撞调试 [ROGUELIKE_COMBAT_COLLISION_DEBUG]")
            {
                hideFlags = HideFlags.DontSave
            };
            root.transform.SetParent(parent, false);
        }

        /// <summary>每次战斗模拟推进后，把 ECS 使用的实际圆心、半径和偏移同步到临时线框。</summary>
        /// <remarks>单位死亡和弹丸失活会立即隐藏；目标范围技能保留短暂轨迹以便肉眼确认。</remarks>
        public void Synchronize()
        {
            if (disposed) return;
            RefreshAreaRadii();
            CaptureAreaEvents();
            SynchronizeUnits();
            SynchronizeProjectiles();
            SynchronizeAreas();
        }

        /// <summary>在正式战斗 HUD 上绘制当前宏对应的颜色图例。</summary>
        /// <remarks>只绘制即时 GUI，不创建或保存 UI 资源。</remarks>
        public void DrawLegend()
        {
            if (disposed) return;
            const float width = 255f;
            const float height = 92f;
            var area = new Rect(8f, Screen.height - height - 8f, width, height);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("碰撞调试 [ROGUELIKE_COMBAT_COLLISION_DEBUG]");
            GUILayout.Label("青=主角受击  红=怪物受击  绿=移动");
            GUILayout.Label("黄=弹丸判定  紫=范围技能判定");
            GUILayout.EndArea();
        }

        /// <summary>战斗关闭时回收全部临时线框、材质和根节点。</summary>
        /// <remarks>幂等；不会销毁或修改任何正式资源、Scene 或 Prefab。</remarks>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DestroyOwned(root);
            DestroyOwned(material);
            bodyLines.Clear();
            movementLines.Clear();
            projectileLines.Clear();
            areaLines.Clear();
            areaTraces.Clear();
            areaRadii.Clear();
        }

        /// <summary>读取每把玩家武器的最终范围半径，使范围线框与技能缩放后的判定值一致。</summary>
        private void RefreshAreaRadii()
        {
            for (int index = 0; index < session.PlayerWeaponCount; index++)
            {
                CombatWeaponState weapon = session.ReadPlayerWeapon(index);
                if (weapon.AreaRadius > 0f) areaRadii[weapon.SkillId] = weapon.AreaRadius;
            }
        }

        /// <summary>捕获本帧一次性范围技能事件，并保留有限时间方便观察释放位置。</summary>
        private void CaptureAreaEvents()
        {
            for (int index = 0; index < session.AreaCount; index++)
            {
                CombatAreaEvent area = session.ReadArea(index);
                if (!areaRadii.TryGetValue(area.SkillId, out float radius)) continue;
                bool alreadyCaptured = false;
                for (int traceIndex = 0; traceIndex < areaTraces.Count; traceIndex++)
                {
                    AreaTrace trace = areaTraces[traceIndex];
                    if (trace.AttackSequence == area.AttackSequence && trace.SkillId == area.SkillId)
                    {
                        alreadyCaptured = true;
                        break;
                    }
                }
                if (!alreadyCaptured)
                    areaTraces.Add(new AreaTrace(area.SkillId, area.AttackSequence,
                        new Vector2(area.Position.x, area.Position.y), radius, Time.unscaledTime + AreaTraceSeconds));
            }
        }

        /// <summary>绘制存活单位的真实受击形状；合并配置显示单个纵向胶囊，旧配置保留双圆。</summary>
        private void SynchronizeUnits()
        {
            EnsurePool(bodyLines, session.UnitCount, "受击判定", BodySortingOrder);
            EnsurePool(movementLines, session.UnitCount, "移动阻挡", MovementSortingOrder);
            for (int index = 0; index < session.UnitCount; index++)
            {
                CombatUnit unit = session.ReadUnit(index);
                bool visible = unit.Target.Health > 0;
                if (!visible)
                {
                    bodyLines[index].enabled = false;
                    movementLines[index].enabled = false;
                    continue;
                }

                Vector2 bodyCenter = new Vector2(unit.Position.x + unit.BodyOffset.x,
                    unit.Position.y + unit.BodyOffset.y);
                if (unit.Movement.UsesCapsule2D)
                {
                    movementLines[index].enabled = false;
                    DrawVerticalCapsule(bodyLines[index], bodyCenter, unit.Radius, unit.BodyHalfSegment,
                        unit.Target.IsPlayer ? PlayerBodyColor : MonsterBodyColor);
                }
                else
                {
                    DrawCircle(movementLines[index],
                        new Vector2(unit.Position.x + unit.Movement.Offset.x, unit.Position.y + unit.Movement.Offset.y),
                        unit.Movement.Radius, MovementColor);
                    DrawCircle(bodyLines[index], bodyCenter, unit.Radius,
                        unit.Target.IsPlayer ? PlayerBodyColor : MonsterBodyColor);
                }
            }
        }

        /// <summary>绘制所有活动弹丸包含 CollisionOffset 的真实碰撞圆。</summary>
        private void SynchronizeProjectiles()
        {
            EnsurePool(projectileLines, session.ProjectileCapacity, "弹丸命中", ProjectileSortingOrder);
            for (int index = 0; index < session.ProjectileCapacity; index++)
            {
                CombatProjectile projectile = session.ReadProjectile(index);
                if (!projectile.Active)
                {
                    projectileLines[index].enabled = false;
                    continue;
                }
                DrawCircle(projectileLines[index],
                    new Vector2(projectile.Position.x + projectile.CollisionOffset.x,
                        projectile.Position.y + projectile.CollisionOffset.y),
                    projectile.Radius, ProjectileColor);
            }
        }

        /// <summary>清理过期范围轨迹并绘制仍需观察的目标位置判定圆。</summary>
        private void SynchronizeAreas()
        {
            float now = Time.unscaledTime;
            for (int index = areaTraces.Count - 1; index >= 0; index--)
            {
                if (areaTraces[index].ExpiresAt <= now) areaTraces.RemoveAt(index);
            }
            EnsurePool(areaLines, areaTraces.Count, "范围技能命中", AreaSortingOrder);
            for (int index = 0; index < areaLines.Count; index++)
            {
                if (index >= areaTraces.Count)
                {
                    areaLines[index].enabled = false;
                    continue;
                }
                AreaTrace trace = areaTraces[index];
                DrawCircle(areaLines[index], trace.Position, trace.Radius, AreaColor);
            }
        }

        /// <summary>按需要扩展指定类别的 LineRenderer 池，避免战斗过程中反复销毁对象。</summary>
        /// <param name="pool">要扩展的线框池。</param>
        /// <param name="required">本帧至少需要的数量。</param>
        /// <param name="label">Hierarchy 中用于辨认的类别名。</param>
        /// <param name="sortingOrder">确保调试线绘制在战斗贴图之上的排序值。</param>
        private void EnsurePool(List<LineRenderer> pool, int required, string label, int sortingOrder)
        {
            while (pool.Count < required)
            {
                var item = new GameObject($"{label}范围 {pool.Count}") { hideFlags = HideFlags.DontSave };
                item.transform.SetParent(root.transform, false);
                LineRenderer line = item.AddComponent<LineRenderer>();
                line.sharedMaterial = material;
                line.useWorldSpace = true;
                line.loop = true;
                line.positionCount = CircleSegments;
                line.widthMultiplier = lineWidth;
                line.numCapVertices = 0;
                line.numCornerVertices = 0;
                line.alignment = LineAlignment.View;
                line.textureMode = LineTextureMode.Stretch;
                line.sortingOrder = sortingOrder;
                line.enabled = false;
                pool.Add(line);
            }
        }

        /// <summary>把一个圆心和半径转换为固定分段线框，并设置该判定类别颜色。</summary>
        /// <param name="line">接收顶点的运行时 LineRenderer。</param>
        /// <param name="center">真实碰撞圆心。</param>
        /// <param name="radius">真实碰撞半径。</param>
        /// <param name="color">该判定类别的调试颜色。</param>
        private void DrawCircle(LineRenderer line, Vector2 center, float radius, Color color)
        {
            line.enabled = radius > 0f;
            if (!line.enabled) return;
            for (int index = 0; index < CircleSegments; index++)
            {
                float angle = index * Mathf.PI * 2f / CircleSegments;
                circlePositions[index] = new Vector3(center.x + Mathf.Cos(angle) * radius,
                    center.y + Mathf.Sin(angle) * radius, DebugDepth);
            }
            line.startColor = color;
            line.endColor = color;
            line.SetPositions(circlePositions);
        }

        /// <summary>把 DOTS 纵向胶囊转换为两段半圆和两条竖边组成的固定分段线框。</summary>
        /// <param name="line">接收顶点的运行时 LineRenderer。</param>
        /// <param name="center">胶囊中心。</param>
        /// <param name="radius">胶囊端帽半径。</param>
        /// <param name="halfSegment">中心线在上下方向的半长度。</param>
        /// <param name="color">该判定类别的调试颜色。</param>
        private void DrawVerticalCapsule(LineRenderer line, Vector2 center, float radius, float halfSegment, Color color)
        {
            line.enabled = radius > 0f;
            if (!line.enabled) return;
            int half = CircleSegments / 2;
            for (int index = 0; index < half; index++)
            {
                float angle = index * Mathf.PI / (half - 1);
                circlePositions[index] = new Vector3(center.x + Mathf.Cos(angle) * radius,
                    center.y + halfSegment + Mathf.Sin(angle) * radius, DebugDepth);
            }
            for (int index = 0; index < half; index++)
            {
                float angle = Mathf.PI + index * Mathf.PI / (half - 1);
                circlePositions[half + index] = new Vector3(center.x + Mathf.Cos(angle) * radius,
                    center.y - halfSegment + Mathf.Sin(angle) * radius, DebugDepth);
            }
            line.startColor = color;
            line.endColor = color;
            line.SetPositions(circlePositions);
        }

        /// <summary>根据当前 Unity 生命周期安全销毁运行时独占对象。</summary>
        /// <param name="value">要销毁的临时 Unity 对象。</param>
        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        /// <summary>记录一次目标位置范围技能的短时调试轨迹。</summary>
        private readonly struct AreaTrace
        {
            public readonly int SkillId;
            public readonly ulong AttackSequence;
            public readonly Vector2 Position;
            public readonly float Radius;
            public readonly float ExpiresAt;

            /// <summary>保存范围技能事件的唯一身份、真实圆和过期时刻。</summary>
            /// <param name="skillId">TbSkill.id。</param>
            /// <param name="attackSequence">模拟层稳定攻击序号。</param>
            /// <param name="position">目标位置圆心。</param>
            /// <param name="radius">经技能视觉缩放后的最终判定半径。</param>
            /// <param name="expiresAt">按未缩放时间计算的调试轨迹过期时刻。</param>
            public AreaTrace(int skillId, ulong attackSequence, Vector2 position, float radius, float expiresAt)
            {
                SkillId = skillId;
                AttackSequence = attackSequence;
                Position = position;
                Radius = radius;
                ExpiresAt = expiresAt;
            }
        }
    }
}
#endif
