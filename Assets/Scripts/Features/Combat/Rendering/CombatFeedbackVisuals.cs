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
    /// <remarks>依赖 TbCharacter/TbMonster 的 damageFloatHeightPixels 字段定位锚点；入局时按战斗规则换算一次世界高度。</remarks>
    public sealed class CombatFeedbackVisuals : IDisposable
    {
        private const int BarFrameResourceId = 960006;
        private const int BarFillResourceId = 960007;
        private const int DigitAtlasResourceId = 960008;

        private const int FeedbackAtlasWidth = 348;
        private const int FeedbackAtlasHeight = 44;
        private const int BarTextureWidth = 60;
        private const int BarTextureHeight = 7;
        private const int DigitTextureHeight = 30;
        private const int DigitCellWidth = 29;

        private const float HpFrameDepth = -1f;
        private const float HpFillDepth = -2f;
        private const float DigitDepth = -3f;
        private const float HiddenWorldCoordinate = 1000000f;

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
        private readonly float healthBarWidthPixels;
        private readonly float healthBarHeightPixels;

        private readonly Material[] materials;
        private readonly Mesh[] meshes;
        private readonly Texture2D[] textures;
        private readonly RenderMeshArray meshArray;
        private readonly RenderMeshDescription description;

        private readonly int barFrameMaterialIndex;
        private readonly int barFillMaterialIndex;
        private readonly int digitMaterialIndex;
        private readonly int barFrameMeshIndex;
        private readonly int barFillMeshIndex;
        private readonly int digitMeshStartIndex;

        private readonly List<HpBarSlot> hpBars = new List<HpBarSlot>();
        private long[] previousHealth = Array.Empty<long>();
        private ulong[] previousLifetimes = Array.Empty<ulong>();
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
            var lookup = BuildHeightLookup(scenario, scenario.CombatRulesId_Ref.WorldUnitsPerPixel);
            var (materials, meshes, textures, meshArray) = await LoadResourcesAsync(scenario.PresentationId_Ref,
                scenario.CombatRulesId_Ref, resources, token);
            return new CombatFeedbackVisuals(world, session, lookup, scenario.CombatRulesId_Ref,
                scenario.PresentationId_Ref, materials, meshes, textures, meshArray);
        }

        /// <summary>从正式关卡定义加载血条/数字资源并绑定到世界。</summary>
        public static async Task<CombatFeedbackVisuals> CreateAsync(World world, CombatSession session,
            CombatRunDefinition definition, IResourceService resources, CancellationToken token)
        {
            if (definition?.Presentation == null || definition.CombatRules == null || resources == null)
                throw new ArgumentNullException(nameof(definition));
            var lookup = BuildHeightLookup(definition, definition.CombatRules.WorldUnitsPerPixel);
            var (materials, meshes, textures, meshArray) = await LoadResourcesAsync(definition.Presentation,
                definition.CombatRules, resources, token);
            return new CombatFeedbackVisuals(world, session, lookup, definition.CombatRules,
                definition.Presentation, materials, meshes, textures, meshArray);
        }

        /// <summary>将测试入口角色与怪物的伤害数字像素锚点统一转换为世界高度。</summary>
        /// <param name="scenario">已解析角色和怪物引用的 TbPerformanceScenario。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel。</param>
        /// <returns>配置 ID 到世界高度的查找表。</returns>
        private static Dictionary<int, float> BuildHeightLookup(PerformanceScenarioConfig scenario, float worldUnitsPerPixel)
        {
            var lookup = new Dictionary<int, float>();
            AddCharacter(scenario.CharacterId_Ref, lookup, worldUnitsPerPixel);
            foreach (var monster in scenario.MonsterIds_Ref)
                AddMonster(monster, lookup, worldUnitsPerPixel);
            return lookup;
        }

        /// <summary>将正式单局角色、普通怪与 Boss 的伤害数字像素锚点统一转换为世界高度。</summary>
        /// <param name="definition">已完成跨表解析的正式单局定义。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel。</param>
        /// <returns>配置 ID 到世界高度的查找表。</returns>
        private static Dictionary<int, float> BuildHeightLookup(CombatRunDefinition definition, float worldUnitsPerPixel)
        {
            var lookup = new Dictionary<int, float>();
            AddCharacter(definition.Character, lookup, worldUnitsPerPixel);
            foreach (var monster in definition.Monsters)
                AddMonster(monster, lookup, worldUnitsPerPixel);
            AddMonster(definition.Boss.MonsterId_Ref, lookup, worldUnitsPerPixel);
            return lookup;
        }

        /// <summary>登记角色伤害数字锚点，并拒绝以代码默认值掩盖缺失配置。</summary>
        /// <param name="config">TbCharacter 行。</param>
        /// <param name="lookup">待写入的查找表。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel。</param>
        /// <exception cref="InvalidOperationException">战斗角色未配置像素锚点。</exception>
        private static void AddCharacter(CharacterConfig config, Dictionary<int, float> lookup, float worldUnitsPerPixel)
        {
            if (config == null) return;
            if (!config.DamageFloatHeightPixels.HasValue)
                throw new InvalidOperationException($"TbCharacter {config.Id}: damageFloatHeightPixels is required.");
            lookup[config.Id] = config.DamageFloatHeightPixels.Value * worldUnitsPerPixel;
        }

        /// <summary>登记怪物伤害数字锚点，并拒绝以代码默认值掩盖缺失配置。</summary>
        /// <param name="config">TbMonster 行。</param>
        /// <param name="lookup">待写入的查找表。</param>
        /// <param name="worldUnitsPerPixel">TbCombatRules.worldUnitsPerPixel。</param>
        /// <exception cref="InvalidOperationException">战斗怪物未配置像素锚点。</exception>
        private static void AddMonster(MonsterConfig config, Dictionary<int, float> lookup, float worldUnitsPerPixel)
        {
            if (config == null) return;
            if (!config.DamageFloatHeightPixels.HasValue)
                throw new InvalidOperationException($"TbMonster {config.Id}: damageFloatHeightPixels is required.");
            lookup[config.Id] = config.DamageFloatHeightPixels.Value * worldUnitsPerPixel;
        }

        private static async Task<(Material[] materials, Mesh[] meshes, Texture2D[] textures, RenderMeshArray meshArray)>
            LoadResourcesAsync(CombatPresentationConfig presentation, CombatRulesConfig rules,
                IResourceService resources, CancellationToken token)
        {
            if (!(presentation.HealthBarWidthPixels > 0f) || !(presentation.HealthBarHeightPixels > 0f))
                throw new InvalidOperationException("TbCombatPresentation health bar pixel size must be positive.");
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

            var textureList = new List<Texture2D>(4);
            var materialList = new List<Material>(1);

            Texture2D barFrame = await LoadTextureAsync(BarFrameResourceId, resources, filter, token);
            Texture2D barFill = await LoadTextureAsync(BarFillResourceId, resources, filter, token);
            Texture2D digitAtlas = await LoadTextureAsync(DigitAtlasResourceId, resources, filter, token);

            textureList.Add(barFrame);
            textureList.Add(barFill);
            textureList.Add(digitAtlas);
            Texture2D feedbackAtlas = CreateFeedbackAtlas(barFrame, barFill, digitAtlas, filter);
            textureList.Add(feedbackAtlas);

            materialList.Add(CreateMaterial(shader, feedbackAtlas, presentation.FeedbackRenderQueue));

            var meshesList = new List<Mesh>();
            meshesList.Add(QuadMeshBuilder.Create(
                0.5f / FeedbackAtlasWidth, (BarTextureWidth - 0.5f) / FeedbackAtlasWidth,
                0.5f / FeedbackAtlasHeight, (BarTextureHeight - 0.5f) / FeedbackAtlasHeight));
            meshesList.Add(QuadMeshBuilder.Create(
                0.5f / FeedbackAtlasWidth, (BarTextureWidth - 0.5f) / FeedbackAtlasWidth,
                (BarTextureHeight + 0.5f) / FeedbackAtlasHeight,
                (BarTextureHeight * 2f - 0.5f) / FeedbackAtlasHeight));

            const int digitCount = 12;
            for (int i = 0; i < digitCount; i++)
            {
                float u0 = (i * DigitCellWidth + 0.5f) / FeedbackAtlasWidth;
                float u1 = (i * DigitCellWidth + DigitCellWidth - 0.5f) / FeedbackAtlasWidth;
                float v0 = (BarTextureHeight * 2f + 0.5f) / FeedbackAtlasHeight;
                float v1 = (BarTextureHeight * 2f + DigitTextureHeight - 0.5f) / FeedbackAtlasHeight;
                meshesList.Add(QuadMeshBuilder.Create(u0, u1, v0, v1));
            }

            var materials = materialList.ToArray();
            var meshes = meshesList.ToArray();
            var textures = textureList.ToArray();
            var meshArray = new RenderMeshArray(materials, meshes);
            return (materials, meshes, textures, meshArray);
        }

        /// <summary>按 TbResource 的 Texture 类型加载正式资源，并创建仅供运行时图集合成的可读副本。</summary>
        /// <param name="resourceId">TbResource.id；其 path 必须指向 YooAsset 收集的 Texture2D。</param>
        /// <param name="resources">统一资源服务。</param>
        /// <param name="filter">TbCombatPresentation.textureFilterMode 指定的采样方式。</param>
        /// <param name="token">战斗启动取消令牌。</param>
        /// <returns>与资源像素尺寸一致、由当前战斗反馈实例拥有的 RGBA32 纹理。</returns>
        /// <remarks>加载句柄在副本完成后立即释放；返回纹理由 CombatFeedbackVisuals.Dispose 销毁。</remarks>
        private static async Task<Texture2D> LoadTextureAsync(int resourceId, IResourceService resources,
            FilterMode filter, CancellationToken token)
        {
            using var handle = await resources.LoadAssetAsync<Texture2D>(resourceId, token);
            token.ThrowIfCancellationRequested();
            return CreateReadableCopy(handle.Asset, filter);
        }

        /// <summary>通过临时 RenderTexture 复制不可读的正式纹理，供 CPU 合并反馈图集。</summary>
        /// <param name="source">YooAsset 加载的正式 Texture2D。</param>
        /// <param name="filter">副本使用的采样方式。</param>
        /// <returns>像素可读且不会依赖资源句柄生命周期的 RGBA32 副本。</returns>
        /// <remarks>临时 RenderTexture 仅在调用期间存在，并恢复调用前的 RenderTexture.active。</remarks>
        private static Texture2D CreateReadableCopy(Texture2D source, FilterMode filter)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
                {
                    filterMode = filter,
                    wrapMode = TextureWrapMode.Clamp
                };
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                copy.Apply(false, false);
                return copy;
            }
            catch
            {
                if (copy != null) Release(new[] { copy });
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        /// <summary>把正式蓝框、红色填充与数字纹理合并为仅驻留内存的单材质图集。</summary>
        /// <param name="barFrame">TbResource 960006 加载的血条底框。</param>
        /// <param name="barFill">TbResource 960007 加载的血量填充。</param>
        /// <param name="digits">TbResource 960008 加载的伤害数字图集。</param>
        /// <param name="filter">TbCombatPresentation.textureFilterMode 指定的采样方式。</param>
        /// <returns>供本局 Entities Graphics 共享材质使用的运行时图集。</returns>
        /// <remarks>图集不保存、不序列化，CombatFeedbackVisuals.Dispose 时与源纹理一并销毁。</remarks>
        private static Texture2D CreateFeedbackAtlas(Texture2D barFrame, Texture2D barFill,
            Texture2D digits, FilterMode filter)
        {
            if (barFrame.width != BarTextureWidth || barFrame.height != BarTextureHeight ||
                barFill.width != BarTextureWidth || barFill.height != BarTextureHeight ||
                digits.width != FeedbackAtlasWidth || digits.height != DigitTextureHeight)
                throw new InvalidOperationException(
                    $"Combat feedback textures do not match their configured atlas layout: " +
                    $"frame={barFrame.width}x{barFrame.height}, fill={barFill.width}x{barFill.height}, " +
                    $"digits={digits.width}x{digits.height}.");

            var atlas = new Texture2D(FeedbackAtlasWidth, FeedbackAtlasHeight, TextureFormat.RGBA32, false)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp
            };
            atlas.SetPixels32(new Color32[FeedbackAtlasWidth * FeedbackAtlasHeight]);
            atlas.SetPixels32(0, 0, BarTextureWidth, BarTextureHeight, barFrame.GetPixels32());
            atlas.SetPixels32(0, BarTextureHeight, BarTextureWidth, BarTextureHeight, barFill.GetPixels32());
            atlas.SetPixels32(0, BarTextureHeight * 2, FeedbackAtlasWidth, DigitTextureHeight,
                digits.GetPixels32());
            atlas.Apply(false, true);
            return atlas;
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
            CombatRulesConfig rules, CombatPresentationConfig presentation, Material[] materials, Mesh[] meshes,
            Texture2D[] textures, RenderMeshArray meshArray)
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
            healthBarWidthPixels = presentation.HealthBarWidthPixels;
            healthBarHeightPixels = presentation.HealthBarHeightPixels;
            if (!(healthBarWidthPixels > 0f) || !(healthBarHeightPixels > 0f))
                throw new InvalidOperationException("TbCombatPresentation health bar pixel size must be positive.");

            description = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            barFrameMaterialIndex = 0;
            barFillMaterialIndex = 0;
            digitMaterialIndex = 0;
            barFrameMeshIndex = 0;
            barFillMeshIndex = 1;
            digitMeshStartIndex = 2;

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
                {
                    previousHealth[i] = 0;
                    previousLifetimes[i] = 0;
                }
            }
            lastTick = tick;

            for (int slot = 0; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                var slotState = hpBars[slot];
                float floatHeight = damageFloatHeights.TryGetValue(unit.ConfigId, out float h) ? h : 0.35f;
                float3 unitPos = new float3(unit.Position, 0f);
                float anchorY = unitPos.y + floatHeight;

                long prev = previousHealth[slot];
                if (previousLifetimes[slot] == unit.Target.Lifetime && prev > 0 && unit.Target.Health < prev)
                {
                    long damage = prev - unit.Target.Health;
                    SpawnDamageNumber(damage, new float3(unitPos.x, anchorY, DigitDepth), tick);
                }

                if (unit.Target.Health > 0)
                {
                    if (!slotState.Bound) BindHpBar(slot);
                    UpdateHpBar(slotState, unit, unitPos.x, anchorY);
                }
                else if (slotState.Bound)
                {
                    SetVisible(slotState.FrameEntity, false);
                    SetVisible(slotState.FillEntity, false);
                }

                previousHealth[slot] = unit.Target.Health;
                previousLifetimes[slot] = unit.Target.Lifetime;
            }

            UpdateDamageNumbers(tick);
        }

        private void EnsureUnitCapacity()
        {
            int count = session.UnitCount;
            if (hpBars.Count == count && previousHealth.Length == count && previousLifetimes.Length == count) return;

            int oldBars = hpBars.Count;
            for (int i = hpBars.Count; i < count; i++)
                hpBars.Add(new HpBarSlot());

            int oldHealth = previousHealth.Length;
            Array.Resize(ref previousHealth, count);
            Array.Resize(ref previousLifetimes, count);
            for (int i = oldHealth; i < count; i++)
            {
                previousHealth[i] = 0;
                previousLifetimes[i] = 0;
            }

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
            state.FrameEntity = CreateFeedbackEntity(barFrameMaterialIndex, barFrameMeshIndex);
            state.FillEntity = CreateFeedbackEntity(barFillMaterialIndex, barFillMeshIndex);
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
            float barWidth = healthBarWidthPixels * worldUnitsPerPixel;
            float barHeight = healthBarHeightPixels * worldUnitsPerPixel;
            float barCenterX = unitX;
            float barCenterY = anchorY - barHeight * 0.5f;
            float barLeftX = barCenterX - barWidth * 0.5f;
            float fillWidth = barWidth * ratio;
            float fillCenterX = barLeftX + fillWidth * 0.5f;

            SetVisible(slot.FrameEntity, true);
            SetVisible(slot.FillEntity, true);

            manager.SetComponentData(slot.FrameEntity,
                LocalTransform.FromPosition(new float3(barCenterX, barCenterY, HpFrameDepth)));
            manager.SetComponentData(slot.FrameEntity,
                new PostTransformMatrix { Value = float4x4.Scale(barWidth, barHeight, 1f) });

            manager.SetComponentData(slot.FillEntity,
                LocalTransform.FromPosition(new float3(fillCenterX, barCenterY, HpFillDepth)));
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
                int meshIndex = digitMeshStartIndex + CharToMeshIndex(text[i]);
                Entity digit = AcquireDigitEntity();
                number.Digits[i] = digit;
                manager.SetComponentData(digit,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(digitMaterialIndex, meshIndex));
                float x = leftX + i * digitWorldWidth * DigitKerning + digitWorldWidth * 0.5f;
                manager.SetComponentData(digit, LocalTransform.FromPosition(new float3(x, baseY, DigitDepth)));
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
            return CreateFeedbackEntity(digitMaterialIndex, digitMeshStartIndex);
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
                    manager.SetComponentData(digit, LocalTransform.FromPosition(new float3(x, baseY, DigitDepth)));
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

        /// <summary>切换反馈实体可见性，隐藏时移出相机视野，显示位置和尺寸由调用方紧接着写入。</summary>
        /// <param name="entity">需要切换的血条或数字实体。</param>
        /// <param name="visible">是否显示；显示分支不修改尺寸。</param>
        /// <remarks>不增删 DisableRendering，也不使用退化零矩阵，避免 Entities Graphics 块索引失效或产生黑色退化面。</remarks>
        private void SetVisible(Entity entity, bool visible)
        {
            if (visible) return;
            manager.SetComponentData(entity,
                LocalTransform.FromPosition(new float3(HiddenWorldCoordinate, HiddenWorldCoordinate, 0f)));
            manager.SetComponentData(entity,
                new PostTransformMatrix { Value = float4x4.Scale(1f, 1f, 1f) });
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
