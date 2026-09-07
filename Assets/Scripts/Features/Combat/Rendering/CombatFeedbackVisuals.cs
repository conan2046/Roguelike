using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>在单位上方绘制 HP 血条与跳字伤害数字；复用同一 DOTS World，不创建 GameObject。</summary>
    /// <remarks>依赖 TbCharacter/TbMonster 的 damageFloatHeight 字段定位锚点；白闪效果因需要自定义 shader 暂留扩展接口。</remarks>
    public sealed class CombatFeedbackVisuals : IDisposable
    {
        private const int BarFrameResourceId = 960006;
        private const int BarFillResourceId = 960007;
        private const int DigitAtlasResourceId = 960008;

        private const int RenderQueueHpFrame = (int)RenderQueue.Transparent + 1;
        private const int RenderQueueHpFill = (int)RenderQueue.Transparent + 2;
        private const int RenderQueueDigits = (int)RenderQueue.Transparent + 3;

        private const float NumberRiseDuration = 0.6f;
        private const float NumberRiseHeight = 0.35f;
        private const float NumberScaleStart = 1.0f;
        private const float NumberScaleEnd = 0.35f;
        private const float DigitKerning = 0.85f;

        private readonly EntityManager manager;
        private readonly World world;
        private readonly CombatSession session;
        private readonly IReadOnlyDictionary<int, float> damageFloatHeights;
        private readonly float worldUnitsPerPixel;

        private readonly Material[] materials;
        private readonly Mesh[] meshes;
        private readonly Texture2D[] textures;
        private readonly RenderMeshArray meshArray;
        private readonly RenderMeshDescription description;

        private readonly int barFrameMaterialIndex;
        private readonly int barFillMaterialIndex;
        private readonly int digitMaterialIndex;
        private readonly int unitQuadMeshIndex;

        private readonly List<HpBarSlot> hpBars = new List<HpBarSlot>();
        private readonly long[] previousHealth = Array.Empty<long>();
        private readonly List<DamageNumber> damageNumbers = new List<DamageNumber>();
        private readonly List<Entity> digitPool = new List<Entity>();

        private bool disposed;
        private ulong lastTick;

        private sealed class HpBarSlot
        {
            public Entity FrameEntity;
            public Entity FillEntity;
            public bool Bound;
        }

        private sealed class DamageNumber
        {
            public bool Active;
            public Entity[] Digits;
            public ulong StartTick;
            public float3 StartPosition;
        }

        /// <summary>从测试场景配置加载血条/数字资源并绑定到世界。</summary>
        public static async Task<CombatFeedbackVisuals> CreateAsync(World world, CombatSession session,
            PerformanceScenarioConfig scenario, IResourceService resources, CancellationToken token)
        {
            if (scenario?.PresentationId_Ref == null || scenario.CombatRulesId_Ref == null || resources == null)
                throw new ArgumentNullException(nameof(scenario));
            var lookup = BuildHeightLookup(scenario);
            var (materials, meshes, textures, meshArray) = await LoadResourcesAsync(scenario.PresentationId_Ref,
                scenario.CombatRulesId_Ref, resources, token);
            return new CombatFeedbackVisuals(world, session, lookup, scenario.CombatRulesId_Ref,
                materials, meshes, textures, meshArray);
        }

        /// <summary>从正式关卡定义加载血条/数字资源并绑定到世界。</summary>
        public static async Task<CombatFeedbackVisuals> CreateAsync(World world, CombatSession session,
            CombatRunDefinition definition, IResourceService resources, CancellationToken token)
        {
            if (definition?.Presentation == null || definition.CombatRules == null || resources == null)
                throw new ArgumentNullException(nameof(definition));
            var lookup = BuildHeightLookup(definition);
            var (materials, meshes, textures, meshArray) = await LoadResourcesAsync(definition.Presentation,
                definition.CombatRules, resources, token);
            return new CombatFeedbackVisuals(world, session, lookup, definition.CombatRules,
                materials, meshes, textures, meshArray);
        }

        private static Dictionary<int, float> BuildHeightLookup(PerformanceScenarioConfig scenario)
        {
            var lookup = new Dictionary<int, float>();
            AddCharacter(scenario.CharacterId_Ref, lookup);
            foreach (var monster in scenario.MonsterIds_Ref)
                AddMonster(monster, lookup);
            return lookup;
        }

        private static Dictionary<int, float> BuildHeightLookup(CombatRunDefinition definition)
        {
            var lookup = new Dictionary<int, float>();
            AddCharacter(definition.Character, lookup);
            foreach (var monster in definition.Monsters)
                AddMonster(monster, lookup);
            AddMonster(definition.Boss.MonsterId_Ref, lookup);
            return lookup;
        }

        private static void AddCharacter(CharacterConfig config, Dictionary<int, float> lookup)
        {
            if (config == null) return;
            lookup[config.Id] = config.DamageFloatHeight ?? 0.35f;
        }

        private static void AddMonster(MonsterConfig config, Dictionary<int, float> lookup)
        {
            if (config == null) return;
            lookup[config.Id] = config.DamageFloatHeight ?? 0.35f;
        }

        private static async Task<(Material[] materials, Mesh[] meshes, Texture2D[] textures, RenderMeshArray meshArray)>
            LoadResourcesAsync(CombatPresentationConfig presentation, CombatRulesConfig rules,
                IResourceService resources, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(presentation.ShaderName))
                throw new InvalidOperationException("TbCombatPresentation.shaderName is required for combat feedback.");
            Shader shader = Shader.Find(presentation.ShaderName);
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("Unavailable configured shader: " + presentation.ShaderName);

            FilterMode filter = presentation.TextureFilterMode switch
            {
                "Point" => FilterMode.Point,
                "Bilinear" => FilterMode.Bilinear,
                _ => throw new InvalidOperationException("TbCombatPresentation.textureFilterMode must be Point or Bilinear.")
            };

            var textureList = new List<Texture2D>(3);
            var materialList = new List<Material>(3);

            Texture2D barFrame = await LoadTextureAsync(BarFrameResourceId, resources, filter, token);
            Texture2D barFill = await LoadTextureAsync(BarFillResourceId, resources, filter, token);
            Texture2D digitAtlas = await LoadTextureAsync(DigitAtlasResourceId, resources, filter, token);

            textureList.Add(barFrame);
            textureList.Add(barFill);
            textureList.Add(digitAtlas);

            materialList.Add(CreateMaterial(shader, barFrame, RenderQueueHpFrame));
            materialList.Add(CreateMaterial(shader, barFill, RenderQueueHpFill));
            materialList.Add(CreateMaterial(shader, digitAtlas, RenderQueueDigits));

            var meshesList = new List<Mesh>();
            meshesList.Add(QuadMeshBuilder.Create(0f, 1f, 0f, 1f));

            const int digitCount = 12;
            const float cellWidth = 29f / 348f;
            const float cellHeight = 1f;
            for (int i = 0; i < digitCount; i++)
            {
                float u0 = i * cellWidth;
                float u1 = u0 + cellWidth;
                meshesList.Add(QuadMeshBuilder.Create(u0, u1, 0f, cellHeight));
            }

            var materials = materialList.ToArray();
            var meshes = meshesList.ToArray();
            var textures = textureList.ToArray();
            var meshArray = new RenderMeshArray(materials, meshes);
            return (materials, meshes, textures, meshArray);
        }

        private static async Task<Texture2D> LoadTextureAsync(int resourceId, IResourceService resources,
            FilterMode filter, CancellationToken token)
        {
            using var handle = await resources.LoadRawFileAsync(resourceId, token);
            token.ThrowIfCancellationRequested();
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!ImageConversion.LoadImage(texture, handle.Data, true))
                throw new InvalidOperationException("Cannot decode TbResource " + resourceId);
            return texture;
        }

        private static Material CreateMaterial(Shader shader, Texture2D texture, int renderQueue)
        {
            var material = new Material(shader)
            {
                enableInstancing = true,
                renderQueue = renderQueue
            };
            if (!material.HasProperty("_BaseMap") || !material.HasProperty("_Surface"))
                throw new InvalidOperationException("Configured shader lacks URP Unlit material interface for feedback.");
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 1);
            material.SetFloat("_Blend", 0);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return material;
        }

        private CombatFeedbackVisuals(World world, CombatSession session, Dictionary<int, float> damageFloatHeights,
            CombatRulesConfig rules, Material[] materials, Mesh[] meshes, Texture2D[] textures,
            RenderMeshArray meshArray)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            manager = world.EntityManager;
            this.damageFloatHeights = damageFloatHeights;
            this.materials = materials;
            this.meshes = meshes;
            this.textures = textures;
            this.meshArray = meshArray;
            worldUnitsPerPixel = rules.WorldUnitsPerPixel;

            description = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            barFrameMaterialIndex = 0;
            barFillMaterialIndex = 1;
            digitMaterialIndex = 2;
            unitQuadMeshIndex = 0;

            try
            {
                Synchronize();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>每帧根据当前单位生命与位置同步血条、检测扣血生成跳字。</summary>
        public void Synchronize()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatFeedbackVisuals));
            EnsureUnitCapacity();
            ulong tick = session.Statistics.Tick;
            if (tick < lastTick)
            {
                // tick 回退：清空跳字，重置生命记录避免生成虚假跳字。
                for (int i = damageNumbers.Count - 1; i >= 0; i--)
                    HideDamageNumber(damageNumbers[i]);
                damageNumbers.Clear();
                for (int i = 0; i < previousHealth.Length; i++)
                    previousHealth[i] = 0;
            }
            lastTick = tick;

            for (int slot = 0; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                var slotState = hpBars[slot];
                float floatHeight = damageFloatHeights.TryGetValue(unit.ConfigId, out float h) ? h : 0.35f;
                float3 unitPos = new float3(unit.Position, 0f);
                float anchorY = unitPos.y + floatHeight;

                if (unit.Target.Health > 0)
                {
                    if (!slotState.Bound) BindHpBar(slot);
                    UpdateHpBar(slotState, unit, unitPos.x, anchorY);

                    long prev = previousHealth[slot];
                    if (prev > 0 && unit.Target.Health < prev)
                    {
                        long damage = prev - unit.Target.Health;
                        SpawnDamageNumber(damage, new float3(unitPos.x, anchorY, 0f), tick);
                    }
                }
                else if (slotState.Bound)
                {
                    SetVisible(slotState.FrameEntity, false);
                    SetVisible(slotState.FillEntity, false);
                }

                previousHealth[slot] = unit.Target.Health;
            }

            UpdateDamageNumbers(tick);
        }

        private void EnsureUnitCapacity()
        {
            int count = session.UnitCount;
            if (hpBars.Count == count && previousHealth.Length == count) return;

            int oldBars = hpBars.Count;
            for (int i = hpBars.Count; i < count; i++)
                hpBars.Add(new HpBarSlot());

            int oldHealth = previousHealth.Length;
            Array.Resize(ref previousHealth, count);
            for (int i = oldHealth; i < count; i++)
                previousHealth[i] = 0;

            // 容量缩小时：销毁不再需要的血条实体（正常不会缩小）。
            if (count < oldBars)
            {
                for (int i = count; i < oldBars; i++)
                {
                    var slot = hpBars[i];
                    if (slot.Bound && world.IsCreated)
                    {
                        if (manager.Exists(slot.FrameEntity)) manager.DestroyEntity(slot.FrameEntity);
                        if (manager.Exists(slot.FillEntity)) manager.DestroyEntity(slot.FillEntity);
                    }
                }
                hpBars.RemoveRange(count, oldBars - count);
            }
        }

        private void BindHpBar(int slot)
        {
            var state = hpBars[slot];
            state.FrameEntity = CreateFeedbackEntity(barFrameMaterialIndex, unitQuadMeshIndex);
            state.FillEntity = CreateFeedbackEntity(barFillMaterialIndex, unitQuadMeshIndex);
            state.Bound = true;
        }

        private Entity CreateFeedbackEntity(int materialIndex, int meshIndex)
        {
            var entity = manager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld), typeof(PostTransformMatrix));
            RenderMeshUtility.AddComponents(entity, manager, description, meshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(materialIndex, meshIndex));
            var bounds = meshes[meshIndex].bounds;
            manager.SetComponentData(entity, new RenderBounds
            {
                Value = new AABB { Center = bounds.center, Extents = bounds.extents }
            });
            SetVisible(entity, false);
            return entity;
        }

        private void UpdateHpBar(HpBarSlot slot, in CombatUnit unit, float unitX, float anchorY)
        {
            float ratio = unit.Target.MaxHealth > 0
                ? math.clamp((float)unit.Target.Health / unit.Target.MaxHealth, 0f, 1f)
                : 0f;
            float barWidth = math.max(unit.Radius * 2.2f, worldUnitsPerPixel * 48f);
            float barHeight = barWidth * (7f / 60f);
            float barCenterX = unitX;
            float barCenterY = anchorY - barHeight * 0.5f;
            float barLeftX = barCenterX - barWidth * 0.5f;
            float fillWidth = barWidth * ratio;
            float fillCenterX = barLeftX + fillWidth * 0.5f;

            SetVisible(slot.FrameEntity, true);
            SetVisible(slot.FillEntity, true);

            manager.SetComponentData(slot.FrameEntity,
                LocalTransform.FromPosition(new float3(barCenterX, barCenterY, 0f)));
            manager.SetComponentData(slot.FrameEntity,
                new PostTransformMatrix { Value = float4x4.Scale(barWidth, barHeight, 1f) });

            manager.SetComponentData(slot.FillEntity,
                LocalTransform.FromPosition(new float3(fillCenterX, barCenterY, 0f)));
            manager.SetComponentData(slot.FillEntity,
                new PostTransformMatrix { Value = float4x4.Scale(fillWidth, barHeight, 1f) });
        }

        private void SpawnDamageNumber(long value, float3 startPosition, ulong tick)
        {
            if (value <= 0) return;
            string text = value.ToString(CultureInfo.InvariantCulture);
            if (text.Length == 0) return;

            var number = new DamageNumber
            {
                Active = true,
                Digits = new Entity[text.Length],
                StartTick = tick,
                StartPosition = startPosition
            };
            damageNumbers.Add(number);

            float digitWorldWidth = worldUnitsPerPixel * 29f * 1.25f;
            float digitWorldHeight = worldUnitsPerPixel * 30f * 1.25f;
            float totalWidth = text.Length * digitWorldWidth * DigitKerning;
            float leftX = startPosition.x - totalWidth * 0.5f;
            float baseY = startPosition.y + digitWorldHeight * 0.5f;

            for (int i = 0; i < text.Length; i++)
            {
                int meshIndex = CharToMeshIndex(text[i]);
                Entity digit = AcquireDigitEntity();
                number.Digits[i] = digit;
                manager.SetComponentData(digit, MaterialMeshInfo.FromRenderMeshArrayIndices(digitMaterialIndex, meshIndex));
                float x = leftX + i * digitWorldWidth * DigitKerning + digitWorldWidth * 0.5f;
                manager.SetComponentData(digit, LocalTransform.FromPosition(new float3(x, baseY, 0f)));
                manager.SetComponentData(digit, new PostTransformMatrix
                {
                    Value = float4x4.Scale(digitWorldWidth, digitWorldHeight, 1f)
                });
                SetVisible(digit, true);
            }
        }

        private static int CharToMeshIndex(char c)
        {
            return c switch
            {
                '.' => 0,
                '/' => 1,
                '0' => 2,
                '1' => 3,
                '2' => 4,
                '3' => 5,
                '4' => 6,
                '5' => 7,
                '6' => 8,
                '7' => 9,
                '8' => 10,
                '9' => 11,
                _ => 2
            };
        }

        private Entity AcquireDigitEntity()
        {
            // 复用池中任意隐藏实体；返回后由调用方重新设置 MaterialMeshInfo/PostTransformMatrix/Position/Scale。
            while (digitPool.Count > 0)
            {
                var candidate = digitPool[digitPool.Count - 1];
                digitPool.RemoveAt(digitPool.Count - 1);
                if (manager.Exists(candidate)) return candidate;
            }
            return CreateFeedbackEntity(digitMaterialIndex, 0);
        }

        private void UpdateDamageNumbers(ulong tick)
        {
            double now = tick * session.StepSeconds;
            for (int i = damageNumbers.Count - 1; i >= 0; i--)
            {
                var number = damageNumbers[i];
                if (!number.Active) continue;
                double age = (now - (double)number.StartTick * session.StepSeconds);
                if (age >= NumberRiseDuration || age < 0)
                {
                    HideDamageNumber(number);
                    damageNumbers.RemoveAt(i);
                    continue;
                }

                float t = (float)(age / NumberRiseDuration);
                float scale = math.lerp(NumberScaleStart, NumberScaleEnd, t);
                float yOffset = math.lerp(0f, NumberRiseHeight, t);

                float digitWorldWidth = worldUnitsPerPixel * 29f * 1.25f * scale;
                float digitWorldHeight = worldUnitsPerPixel * 30f * 1.25f * scale;
                float totalWidth = number.Digits.Length * digitWorldWidth * DigitKerning;
                float leftX = number.StartPosition.x - totalWidth * 0.5f;
                float baseY = number.StartPosition.y + digitWorldHeight * 0.5f + yOffset;

                for (int d = 0; d < number.Digits.Length; d++)
                {
                    var digit = number.Digits[d];
                    if (!manager.Exists(digit)) continue;
                    float x = leftX + d * digitWorldWidth * DigitKerning + digitWorldWidth * 0.5f;
                    manager.SetComponentData(digit, LocalTransform.FromPosition(new float3(x, baseY, 0f)));
                    manager.SetComponentData(digit, new PostTransformMatrix
                    {
                        Value = float4x4.Scale(digitWorldWidth, digitWorldHeight, 1f)
                    });
                }
            }
        }

        private void HideDamageNumber(DamageNumber number)
        {
            number.Active = false;
            foreach (var digit in number.Digits)
            {
                if (manager.Exists(digit))
                {
                    SetVisible(digit, false);
                    digitPool.Add(digit);
                }
            }
        }

        private void SetVisible(Entity entity, bool visible)
        {
            bool hidden = manager.HasComponent<DisableRendering>(entity);
            if (visible && hidden) manager.RemoveComponent<DisableRendering>(entity);
            else if (!visible && !hidden) manager.AddComponent<DisableRendering>(entity);
        }

        /// <summary>释放运行时创建的实体、材质、网格和纹理。</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (world != null && world.IsCreated)
            {
                foreach (var slot in hpBars)
                {
                    if (slot.Bound)
                    {
                        if (manager.Exists(slot.FrameEntity)) manager.DestroyEntity(slot.FrameEntity);
                        if (manager.Exists(slot.FillEntity)) manager.DestroyEntity(slot.FillEntity);
                    }
                }
                foreach (var number in damageNumbers)
                {
                    foreach (var digit in number.Digits)
                    {
                        if (manager.Exists(digit)) manager.DestroyEntity(digit);
                    }
                }
                foreach (var digit in digitPool)
                {
                    if (manager.Exists(digit)) manager.DestroyEntity(digit);
                }
            }
            hpBars.Clear();
            damageNumbers.Clear();
            digitPool.Clear();
            Release(materials);
            Release(meshes);
            Release(textures);
        }

        private static void Release(IEnumerable<UnityEngine.Object> objects)
        {
            if (objects == null) return;
            foreach (var obj in objects)
            {
                if (obj == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
                else UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        private static class QuadMeshBuilder
        {
            public static Mesh Create(float u0, float u1, float v0, float v1)
            {
                var mesh = new Mesh
                {
                    vertices = new[]
                    {
                        new Vector3(-0.5f, -0.5f, 0f),
                        new Vector3(0.5f, -0.5f, 0f),
                        new Vector3(-0.5f, 0.5f, 0f),
                        new Vector3(0.5f, 0.5f, 0f)
                    },
                    uv = new[]
                    {
                        new Vector2(u0, v0),
                        new Vector2(u1, v0),
                        new Vector2(u0, v1),
                        new Vector2(u1, v1)
                    },
                    triangles = new[] { 0, 2, 1, 2, 3, 1 }
                };
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
