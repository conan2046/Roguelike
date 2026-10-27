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
using Unity.Collections;
using Unity.Profiling;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Roguelike.Architecture.PlayModeTests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Roguelike.Architecture.Tests")]

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>在单位上方用稳定槽序双层批次绘制HP血条，伤害数字继续复用同一DOTS World。</summary>
    /// <remarks>依赖 TbCharacter/TbMonster 的 damageFloatHeightPixels 字段定位锚点；入局时按战斗规则换算一次世界高度。</remarks>
    public sealed class CombatFeedbackVisuals : IDisposable
    {
        private const float HpFrameDepth = -1f;
        private const float HpFillDepth = -2f;
        private const float DigitDepth = -3f;
        private const float HiddenWorldCoordinate = 1000000f;

        private static readonly ProfilerMarker HealthBarMarker = new ProfilerMarker("Roguelike.Presentation.HealthBars");
        private static readonly ProfilerMarker DamageNumberMarker = new ProfilerMarker("Roguelike.Presentation.DamageNumbers");
        private readonly EntityManager manager;
        private readonly World world;
        private readonly CombatSession session;
        private readonly IReadOnlyDictionary<int, float> damageFloatHeights;
        private readonly float worldUnitsPerPixel;
        private readonly float healthBarWidthPixels;
        private readonly float healthBarHeightPixels;
        private readonly string damageNumberGlyphs;
        private readonly float damageNumberRiseDuration;
        private readonly float damageNumberRiseHeight;
        private readonly float damageNumberScaleStart;
        private readonly float damageNumberScaleEnd;
        private readonly float damageNumberKerning;
        private readonly float damageNumberGlyphScale;
        private readonly float digitWidthPixels;
        private readonly float digitHeightPixels;

        private readonly Material[] materials;
        private readonly Mesh[] meshes;
        private readonly Texture2D[] textures;
        private readonly RenderMeshArray meshArray;
        private readonly RenderMeshDescription description;

        internal OrderedHealthBarBatch HealthBarBatch { get; private set; }

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
            public bool Visible, Initialized;
            public Matrix4x4 FrameMatrix, FillMatrix;
            public float3 FramePosition, FillPosition;
            public float Ratio;
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
                resources, token);
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
                resources, token);
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

        /// <summary>按 TbCombatPresentation 加载正式反馈资源，并依据真实纹理尺寸构造内存图集与 UV 网格。</summary>
        /// <param name="presentation">TbCombatPresentation 行，提供资源引用、字形顺序与渲染参数。</param>
        /// <param name="resources">统一资源服务。</param>
        /// <param name="token">战斗启动取消令牌。</param>
        /// <returns>由本次战斗反馈实例独占并负责释放的材质、网格、纹理与索引数组。</returns>
        /// <exception cref="InvalidOperationException">配置引用、字形协议、纹理布局或 Shader 不合法。</exception>
        private static async Task<(Material[] materials, Mesh[] meshes, Texture2D[] textures, RenderMeshArray meshArray)>
            LoadResourcesAsync(CombatPresentationConfig presentation, IResourceService resources,
                CancellationToken token)
        {
            ValidateFeedbackConfiguration(presentation);
            if (!(presentation.HealthBarWidthPixels > 0f) || !float.IsFinite(presentation.HealthBarWidthPixels) ||
                !(presentation.HealthBarHeightPixels > 0f) || !float.IsFinite(presentation.HealthBarHeightPixels))
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

            Texture2D barFrame = await LoadTextureAsync(presentation.HealthBarFrameResourceId, resources, filter, token);
            Texture2D barFill = await LoadTextureAsync(presentation.HealthBarFillResourceId, resources, filter, token);
            Texture2D digitAtlas = await LoadTextureAsync(presentation.DamageNumberAtlasResourceId, resources, filter, token);
            if (digitAtlas.width % presentation.DamageNumberGlyphs.Length != 0)
            {
                Release(new[] { barFrame, barFill, digitAtlas });
                throw new InvalidOperationException(
                    $"Damage-number atlas width {digitAtlas.width} is not divisible by configured glyph count " +
                    $"{presentation.DamageNumberGlyphs.Length}.");
            }

            textureList.Add(barFrame);
            textureList.Add(barFill);
            textureList.Add(digitAtlas);
            Texture2D feedbackAtlas = CreateFeedbackAtlas(barFrame, barFill, digitAtlas, filter);
            textureList.Add(feedbackAtlas);

            materialList.Add(CreateMaterial(shader, feedbackAtlas, presentation.FeedbackRenderQueue));

            int atlasWidth = feedbackAtlas.width;
            int atlasHeight = feedbackAtlas.height;
            int barWidth = barFrame.width;
            int barHeight = barFrame.height;
            int digitOffsetY = barFrame.height + barFill.height;
            int digitCellWidth = digitAtlas.width / presentation.DamageNumberGlyphs.Length;
            var meshesList = new List<Mesh>();
            meshesList.Add(QuadMeshBuilder.Create(
                0.5f / atlasWidth, (barWidth - 0.5f) / atlasWidth,
                0.5f / atlasHeight, (barHeight - 0.5f) / atlasHeight));
            meshesList.Add(QuadMeshBuilder.Create(
                0.5f / atlasWidth, (barWidth - 0.5f) / atlasWidth,
                (barFrame.height + 0.5f) / atlasHeight,
                (digitOffsetY - 0.5f) / atlasHeight));

            for (int i = 0; i < presentation.DamageNumberGlyphs.Length; i++)
            {
                float u0 = (i * digitCellWidth + 0.5f) / atlasWidth;
                float u1 = (i * digitCellWidth + digitCellWidth - 0.5f) / atlasWidth;
                float v0 = (digitOffsetY + 0.5f) / atlasHeight;
                float v1 = (digitOffsetY + digitAtlas.height - 0.5f) / atlasHeight;
                meshesList.Add(QuadMeshBuilder.Create(u0, u1, v0, v1));
            }

            var materials = materialList.ToArray();
            var meshes = meshesList.ToArray();
            var textures = textureList.ToArray();
            var meshArray = new RenderMeshArray(materials, meshes);
            return (materials, meshes, textures, meshArray);
        }

        /// <summary>校验 TbCombatPresentation 的战斗反馈资源引用与跳字布局协议。</summary>
        /// <param name="presentation">待用于当前战斗的表现配置。</param>
        /// <exception cref="InvalidOperationException">资源不是纹理、字形不唯一/缺少数字或动画参数越界。</exception>
        private static void ValidateFeedbackConfiguration(CombatPresentationConfig presentation)
        {
            if (presentation == null)
                throw new InvalidOperationException("TbCombatPresentation is required for combat feedback.");
            if (presentation.HealthBarFrameResourceId_Ref?.Type != EResourceType.Texture ||
                string.IsNullOrWhiteSpace(presentation.HealthBarFrameResourceId_Ref.Path) ||
                presentation.HealthBarFillResourceId_Ref?.Type != EResourceType.Texture ||
                string.IsNullOrWhiteSpace(presentation.HealthBarFillResourceId_Ref.Path) ||
                presentation.DamageNumberAtlasResourceId_Ref?.Type != EResourceType.Texture ||
                string.IsNullOrWhiteSpace(presentation.DamageNumberAtlasResourceId_Ref.Path))
                throw new InvalidOperationException(
                    "TbCombatPresentation combat feedback resources must resolve to non-empty Texture paths.");

            string glyphs = presentation.DamageNumberGlyphs;
            var uniqueGlyphs = new HashSet<char>();
            if (string.IsNullOrEmpty(glyphs))
                throw new InvalidOperationException("TbCombatPresentation.damageNumberGlyphs is required.");
            foreach (char glyph in glyphs)
            {
                if (!uniqueGlyphs.Add(glyph))
                    throw new InvalidOperationException(
                        $"TbCombatPresentation.damageNumberGlyphs contains duplicate glyph '{glyph}'.");
            }
            for (char digit = '0'; digit <= '9'; digit++)
            {
                if (!uniqueGlyphs.Contains(digit))
                    throw new InvalidOperationException(
                        $"TbCombatPresentation.damageNumberGlyphs is missing digit '{digit}'.");
            }

            if (!(presentation.DamageNumberRiseDurationSeconds > 0f) ||
                !float.IsFinite(presentation.DamageNumberRiseDurationSeconds) ||
                !(presentation.DamageNumberRiseHeightPixels >= 0f) ||
                !float.IsFinite(presentation.DamageNumberRiseHeightPixels) ||
                !(presentation.DamageNumberScaleStart > 0f) ||
                !float.IsFinite(presentation.DamageNumberScaleStart) ||
                !(presentation.DamageNumberScaleEnd > 0f) ||
                !float.IsFinite(presentation.DamageNumberScaleEnd) ||
                !(presentation.DamageNumberKerning > 0f) ||
                !float.IsFinite(presentation.DamageNumberKerning) ||
                !(presentation.DamageNumberGlyphScale > 0f) ||
                !float.IsFinite(presentation.DamageNumberGlyphScale))
                throw new InvalidOperationException("TbCombatPresentation damage-number animation values are invalid.");
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
        /// <param name="barFrame">TbCombatPresentation.healthBarFrameResourceId 引用的血条底框。</param>
        /// <param name="barFill">TbCombatPresentation.healthBarFillResourceId 引用的血量填充。</param>
        /// <param name="digits">TbCombatPresentation.damageNumberAtlasResourceId 引用的伤害数字图集。</param>
        /// <param name="filter">TbCombatPresentation.textureFilterMode 指定的采样方式。</param>
        /// <returns>供本局 Entities Graphics 共享材质使用的运行时图集。</returns>
        /// <remarks>图集不保存、不序列化，CombatFeedbackVisuals.Dispose 时与源纹理一并销毁。</remarks>
        private static Texture2D CreateFeedbackAtlas(Texture2D barFrame, Texture2D barFill,
            Texture2D digits, FilterMode filter)
        {
            if (barFrame == null || barFill == null || digits == null)
                throw new ArgumentNullException(nameof(barFrame), "Combat feedback textures must all be loaded.");
            if (barFrame.width <= 0 || barFrame.height <= 0 ||
                barFill.width != barFrame.width || barFill.height != barFrame.height ||
                digits.width <= 0 || digits.height <= 0)
                throw new InvalidOperationException(
                    $"Combat feedback textures do not form a valid atlas layout: " +
                    $"frame={barFrame.width}x{barFrame.height}, fill={barFill.width}x{barFill.height}, " +
                    $"digits={digits.width}x{digits.height}.");

            int atlasWidth = Math.Max(barFrame.width, digits.width);
            int atlasHeight = checked(barFrame.height + barFill.height + digits.height);
            var atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp
            };
            atlas.SetPixels32(new Color32[checked(atlasWidth * atlasHeight)]);
            atlas.SetPixels32(0, 0, barFrame.width, barFrame.height, barFrame.GetPixels32());
            atlas.SetPixels32(0, barFrame.height, barFill.width, barFill.height, barFill.GetPixels32());
            atlas.SetPixels32(0, barFrame.height + barFill.height, digits.width, digits.height,
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

        /// <summary>缓存本局配置换算结果并创建反馈实体状态。</summary>
        /// <param name="world">当前 CombatSession 所属 DOTS World。</param>
        /// <param name="session">当前战斗模拟会话。</param>
        /// <param name="damageFloatHeights">角色/怪物配置 ID 到世界高度的映射。</param>
        /// <param name="rules">TbCombatRules 行。</param>
        /// <param name="presentation">TbCombatPresentation 行。</param>
        /// <param name="materials">运行时反馈材质。</param>
        /// <param name="meshes">血条与逐字形 UV 网格。</param>
        /// <param name="textures">三个正式资源副本与合并后的内存图集。</param>
        /// <param name="meshArray">Entities Graphics 使用的材质网格索引。</param>
        /// <remarks>构造完成后立即同步一次；失败时释放全部运行时实体与 Unity 对象。</remarks>
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
            damageNumberGlyphs = presentation.DamageNumberGlyphs;
            damageNumberRiseDuration = presentation.DamageNumberRiseDurationSeconds;
            damageNumberRiseHeight = presentation.DamageNumberRiseHeightPixels * worldUnitsPerPixel;
            damageNumberScaleStart = presentation.DamageNumberScaleStart;
            damageNumberScaleEnd = presentation.DamageNumberScaleEnd;
            damageNumberKerning = presentation.DamageNumberKerning;
            damageNumberGlyphScale = presentation.DamageNumberGlyphScale;
            digitWidthPixels = textures[2].width / (float)damageNumberGlyphs.Length;
            digitHeightPixels = textures[2].height;
            if (!(healthBarWidthPixels > 0f) || !float.IsFinite(healthBarWidthPixels) ||
                !(healthBarHeightPixels > 0f) || !float.IsFinite(healthBarHeightPixels))
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
                HealthBarBatch = new OrderedHealthBarBatch(meshes[barFrameMeshIndex], meshes[barFillMeshIndex], materials[barFrameMaterialIndex]);
                Synchronize();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>独立调用时重新采集权威单位，随后同步血条和扣血跳字。</summary>
        /// <remarks>不保存快照；同帧主体与反馈应使用共享只读借用重载。</remarks>
        /// <exception cref="ObjectDisposedException">反馈已释放。</exception>
        public void Synchronize()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatFeedbackVisuals));
            Synchronize(session.CapturePresentationUnits());
        }

        /// <summary>读取当前同步帧单位，按生命及位置变化提交血条并检测真实扣血。</summary>
        /// <param name="units">刚从本会话采集的只读借用，不得跨帧保存。</param>
        /// <remarks>血条尺寸来自TbCombatPresentation，锚点来自TbCharacter/TbMonster；同Tick属性变化仍立即反映。</remarks>
        /// <exception cref="ObjectDisposedException">反馈已释放。</exception>
        public void Synchronize(NativeArray<CombatUnit>.ReadOnly units)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatFeedbackVisuals));
            bool barsChanged = EnsureUnitCapacity();
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

            using (HealthBarMarker.Auto())
            {
                for (int slot = 0; slot < session.UnitCount; slot++)
                {
                    var unit = units[slot];
                    var slotState = hpBars[slot];
                    if (!damageFloatHeights.TryGetValue(unit.ConfigId, out float floatHeight))
                        throw new InvalidOperationException(
                            $"Combat unit config {unit.ConfigId} has no configured damage-float height.");
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
                        barsChanged |= UpdateHpBar(slotState, unit, unitPos.x, anchorY);
                    }
                    else if (slotState.Visible)
                    {
                        slotState.Visible = false; barsChanged = true;
                    }

                    previousHealth[slot] = unit.Target.Health;
                    previousLifetimes[slot] = unit.Target.Lifetime;
                }
                if (barsChanged)
                {
                    HealthBarBatch.Begin(hpBars.Count);
                    for (int slot = 0; slot < hpBars.Count; slot++)
                    {
                        var bar = hpBars[slot];
                        if (bar.Visible) HealthBarBatch.Add(bar.FrameMatrix, bar.FillMatrix);
                    }
                    HealthBarBatch.Commit();
                }
            }
            using (DamageNumberMarker.Auto()) UpdateDamageNumbers(tick);
        }

        /// <summary>按实际槽数调整血条状态与生命记录；减少时丢弃尾部状态，不创建血条实体。</summary>
        /// <returns>槽容量是否变化，调用方据此刷新批次可见索引。</returns>
        /// <remarks>只改变本局内存；保留已有槽的生命周期记录，避免重开或复用产生虚假跳字。</remarks>
        private bool EnsureUnitCapacity()
        {
            int count = session.UnitCount;
            if (hpBars.Count == count && previousHealth.Length == count && previousLifetimes.Length == count) return false;
            for (int i = hpBars.Count; i < count; i++) hpBars.Add(new HpBarSlot());
            if (count < hpBars.Count) hpBars.RemoveRange(count, hpBars.Count - count);
            Array.Resize(ref previousHealth, count); Array.Resize(ref previousLifetimes, count);
            return true;
        }

        /// <summary>测试与诊断按槽读取已提交血条状态；不借出可修改的内部对象。</summary>
        /// <param name="slot">当前会话槽。</param><returns>原深度世界矩阵及可见性。</returns>
        /// <exception cref="ArgumentOutOfRangeException">槽不在当前会话范围内。</exception>
        internal (Matrix4x4 Frame, Matrix4x4 Fill, bool Visible) ReadHealthBar(int slot)
        {
            if ((uint)slot >= (uint)hpBars.Count) throw new ArgumentOutOfRangeException(nameof(slot));
            var state = hpBars[slot]; return (state.FrameMatrix, state.FillMatrix, state.Visible);
        }

        /// <summary>数字池首次扩容时绑定正式材质与字形网格，初始位置隐藏。</summary>
        /// <param name="materialIndex">反馈共享材质索引。</param><param name="meshIndex">正式字形网格索引。</param>
        /// <returns>由反馈绑定拥有的数字实体。</returns><remarks>Dispose统一销毁；血条改用内存批次，不走本入口。</remarks>
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

        /// <summary>按配置尺寸、锚点和权威生命更新血条矩阵；隐藏后复用始终恢复配置尺寸。</summary>
        /// <param name="slot">本绑定拥有的血条槽。</param><param name="unit">当前权威单位。</param>
        /// <param name="unitX">单位世界横坐标。</param><param name="anchorY">源表damageFloatHeightPixels换算的血条锚点。</param>
        /// <returns>矩阵或可见性是否变化，决定是否上传本帧批次。</returns>
        /// <remarks>尺寸来自TbCombatPresentation.healthBarWidthPixels/healthBarHeightPixels；不改变前后层深度或透明材质。</remarks>
        private bool UpdateHpBar(HpBarSlot slot, in CombatUnit unit, float unitX, float anchorY)
        {
            float ratio = unit.Target.MaxHealth > 0
                ? math.clamp((float)unit.Target.Health / unit.Target.MaxHealth, 0f, 1f) : 0f;
            float barWidth = healthBarWidthPixels * worldUnitsPerPixel;
            float barHeight = healthBarHeightPixels * worldUnitsPerPixel;
            float barCenterY = anchorY - barHeight * 0.5f;
            float fillWidth = barWidth * ratio;
            float fillCenterX = unitX - barWidth * 0.5f + fillWidth * 0.5f;
            float3 framePosition = new float3(unitX, barCenterY, HpFrameDepth);
            float3 fillPosition = new float3(fillCenterX, barCenterY, HpFillDepth);
            if (slot.Initialized && slot.Visible && slot.Ratio == ratio &&
                math.all(slot.FramePosition == framePosition) && math.all(slot.FillPosition == fillPosition)) return false;
            slot.FrameMatrix = Matrix4x4.TRS(framePosition, Quaternion.identity, new Vector3(barWidth, barHeight, 1f));
            slot.FillMatrix = Matrix4x4.TRS(fillPosition, Quaternion.identity, new Vector3(fillWidth, barHeight, 1f));
            slot.FramePosition = framePosition; slot.FillPosition = fillPosition;
            slot.Ratio = ratio; slot.Initialized = true; slot.Visible = true;
            return true;
        }

        /// <summary>按 TbCombatPresentation 字形、缩放和字距创建一次正伤害跳字。</summary>
        /// <param name="value">实际扣除的正伤害值。</param>
        /// <param name="startPosition">单位配置锚点换算后的世界坐标。</param>
        /// <param name="tick">伤害发生的模拟 Tick。</param>
        /// <remarks>从实体池取出数字实体并写入渲染索引与变换；生命周期由 UpdateDamageNumbers 回收。</remarks>
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

            float digitWorldWidth = worldUnitsPerPixel * digitWidthPixels * damageNumberGlyphScale;
            float digitWorldHeight = worldUnitsPerPixel * digitHeightPixels * damageNumberGlyphScale;
            float totalWidth = text.Length * digitWorldWidth * damageNumberKerning;
            float leftX = startPosition.x - totalWidth * 0.5f;
            float baseY = startPosition.y + digitWorldHeight * 0.5f;

            for (int i = 0; i < text.Length; i++)
            {
                int meshIndex = digitMeshStartIndex + GlyphIndex(text[i]);
                Entity digit = AcquireDigitEntity();
                number.Digits[i] = digit;
                manager.SetComponentData(digit,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(digitMaterialIndex, meshIndex));
                float x = leftX + i * digitWorldWidth * damageNumberKerning + digitWorldWidth * 0.5f;
                manager.SetComponentData(digit, LocalTransform.FromPosition(new float3(x, baseY, DigitDepth)));
                manager.SetComponentData(digit, new PostTransformMatrix
                {
                    Value = float4x4.Scale(digitWorldWidth, digitWorldHeight, 1f)
                });
                SetVisible(digit, true);
            }
        }

        /// <summary>把伤害文本字符映射到 TbCombatPresentation.damageNumberGlyphs 的网格索引。</summary>
        /// <param name="glyph">待显示字符。</param>
        /// <returns>该字符在配置字形序列中的零基位置。</returns>
        /// <exception cref="InvalidOperationException">字符未出现在配置字形序列中。</exception>
        private int GlyphIndex(char glyph)
        {
            int index = damageNumberGlyphs.IndexOf(glyph);
            if (index < 0)
                throw new InvalidOperationException(
                    $"Damage-number glyph '{glyph}' is missing from TbCombatPresentation.damageNumberGlyphs.");
            return index;
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

        /// <summary>按 TbCombatPresentation 的时长、位移与缩放曲线推进全部活动跳字。</summary>
        /// <param name="tick">当前模拟 Tick。</param>
        /// <remarks>到期跳字被隐藏并归还实体池，不销毁 Entities Graphics 实体。</remarks>
        private void UpdateDamageNumbers(ulong tick)
        {
            double now = tick * session.StepSeconds;
            for (int i = damageNumbers.Count - 1; i >= 0; i--)
            {
                var number = damageNumbers[i];
                if (!number.Active) continue;
                double age = (now - (double)number.StartTick * session.StepSeconds);
                if (age >= damageNumberRiseDuration || age < 0)
                {
                    HideDamageNumber(number);
                    damageNumbers.RemoveAt(i);
                    continue;
                }

                float t = (float)(age / damageNumberRiseDuration);
                float scale = math.lerp(damageNumberScaleStart, damageNumberScaleEnd, t);
                float yOffset = math.lerp(0f, damageNumberRiseHeight, t);

                float digitWorldWidth = worldUnitsPerPixel * digitWidthPixels * damageNumberGlyphScale * scale;
                float digitWorldHeight = worldUnitsPerPixel * digitHeightPixels * damageNumberGlyphScale * scale;
                float totalWidth = number.Digits.Length * digitWorldWidth * damageNumberKerning;
                float leftX = number.StartPosition.x - totalWidth * 0.5f;
                float baseY = number.StartPosition.y + digitWorldHeight * 0.5f + yOffset;

                for (int d = 0; d < number.Digits.Length; d++)
                {
                    var digit = number.Digits[d];
                    if (!manager.Exists(digit)) continue;
                    float x = leftX + d * digitWorldWidth * damageNumberKerning + digitWorldWidth * 0.5f;
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
            HealthBarBatch?.Dispose();
            if (world != null && world.IsCreated)
            {
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
    /// <summary>按槽位递增写入三角形的双层血条批次；仅拥有本局内存网格，正式图集、材质和UV由反馈绑定借出。</summary>
    internal sealed class OrderedHealthBarBatch : IDisposable
    {
        private readonly Material material;
        private readonly Layer frames, fills;
        private bool disposed;
        internal bool Enabled = true;
        internal int Count { get; private set; }
        internal ulong UploadVersion { get; private set; }

        /// <summary>反馈资源就绪后创建两层动态网格并订阅相机提交；不生成或保存资产。</summary>
        /// <param name="frame">正式底框网格。</param><param name="fill">正式填充网格。</param><param name="material">借用的正式反馈材质。</param>
        /// <remarks>调用方必须在共享材质释放前Dispose；每个相机沿用其原有透明深度排序。</remarks>
        internal OrderedHealthBarBatch(Mesh frame, Mesh fill, Material material)
        {
            this.material = material;
            frames = new Layer(frame); fills = new Layer(fill);
            RenderPipelineManager.beginCameraRendering += Draw;
        }

        /// <summary>每次权威快照同步前重置写入位置，并按实际槽数准备缓冲。</summary>
        /// <param name="slots">当前会话实际槽数。</param><remarks>仅扩容时分配持久Native缓冲；不保存快照或修改配置。</remarks>
        internal void Begin(int slots)
        {
            Count = 0; frames.EnsureCapacity(slots); fills.EnsureCapacity(slots);
        }

        /// <summary>按稳定槽位顺序追加存活血条的原始两层矩阵，不改变尺寸、UV或深度。</summary>
        /// <param name="frame">底框世界矩阵。</param><param name="fill">填充世界矩阵。</param>
        /// <remarks>只修改本批次内存顶点；调用方必须按槽位递增调用。</remarks>
        internal void Add(Matrix4x4 frame, Matrix4x4 fill)
        {
            frames.Write(Count, frame); fills.Write(Count, fill); Count++;
        }

        /// <summary>同步完成后上传当前可见三角形，死亡条不占索引，不产生退化隐藏图形。</summary>
        /// <remarks>网格索引按槽位顺序固定；两层仍保留原始世界深度并各提交一次。</remarks>
        internal void Commit() { frames.Upload(Count); fills.Upload(Count); UploadVersion++; }

        /// <summary>SRP在每个相机剔除前提交本批次；透明队列继续与主体、技能和数字按原深度排序。</summary>
        /// <param name="context">当前SRP上下文。</param><param name="camera">本次相机。</param>
        /// <remarks>不修改相机或共享材质；无存活条或已经释放时不发出命令。</remarks>
        private void Draw(ScriptableRenderContext context, Camera camera)
        {
            if (disposed || !Enabled || Count == 0) return;
            Graphics.DrawMesh(frames.Mesh, Matrix4x4.identity, material, 0, camera, 0, null, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
            Graphics.DrawMesh(fills.Mesh, Matrix4x4.identity, material, 0, camera, 0, null, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
        }

        /// <summary>退出、重开或异常时取消相机回调，销毁两层内存网格并释放持久缓冲；重复调用安全。</summary>
        /// <remarks>共享材质、图集和源网格仍归反馈绑定；不在World已销毁时访问EntityManager。</remarks>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; RenderPipelineManager.beginCameraRendering -= Draw;
            frames.Dispose(); fills.Dispose(); Count = 0;
        }

        /// <summary>单层交错位置/UV顶点；布局与SetVertexBufferParams声明一致。</summary>
        private struct Vertex { public Vector3 Position; public Vector2 Uv; }

        /// <summary>拥有单层动态网格与缓冲；顶点来自正式Quad，索引顺序保留每条内部三角形顺序。</summary>
        private sealed class Layer : IDisposable
        {
            internal readonly Mesh Mesh;
            private readonly Vector3[] sourceVertices;
            private readonly Vector2[] sourceUv;
            private readonly int[] sourceIndices;
            private NativeArray<Vertex> vertices;
            private int capacity;
            private Vector3 minimum, maximum;

            /// <summary>从正式网格缓存顶点、UV和索引，创建不序列化的动态网格。</summary>
            /// <param name="source">正式四边形资源。</param><remarks>不修改源网格；Dispose销毁新建内存对象。</remarks>
            internal Layer(Mesh source)
            {
                sourceVertices = source.vertices; sourceUv = source.uv; sourceIndices = source.triangles;
                Mesh = new Mesh { name = "Combat health bars (runtime)", hideFlags = HideFlags.HideAndDontSave };
                Mesh.MarkDynamic();
            }

            /// <summary>实际槽数增长时重建缓冲及只写一次的稳定索引；不会缩容或产生每帧分配。</summary>
            /// <param name="slots">会话实际槽数。</param><remarks>旧Native缓冲先释放，网格索引使用32位以覆盖实际槽容量。</remarks>
            internal void EnsureCapacity(int slots)
            {
                if (slots <= capacity) return;
                if (vertices.IsCreated) vertices.Dispose();
                capacity = slots;
                vertices = new NativeArray<Vertex>(slots * sourceVertices.Length, Allocator.Persistent);
                Mesh.SetVertexBufferParams(vertices.Length, new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
                var indices = new NativeArray<uint>(slots * sourceIndices.Length, Allocator.Temp);
                try
                {
                for (int slot = 0; slot < slots; slot++)
                for (int i = 0; i < sourceIndices.Length; i++)
                    indices[slot * sourceIndices.Length + i] = (uint)(slot * sourceVertices.Length + sourceIndices[i]);
                Mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
                Mesh.SetIndexBufferData(indices, 0, 0, indices.Length);
                Mesh.subMeshCount = 1;
                }
                finally { indices.Dispose(); }
            }

            /// <summary>按原矩阵变换正式顶点并累积包围盒，保留原始UV。</summary>
            /// <param name="index">本次可见条的稳定压缩索引。</param><param name="matrix">原血条世界矩阵。</param>
            /// <remarks>只写本层持久缓冲；第一个可见条重置包围盒。</remarks>
            internal void Write(int index, Matrix4x4 matrix)
            {
                for (int i = 0; i < sourceVertices.Length; i++)
                {
                    Vector3 position = matrix.MultiplyPoint3x4(sourceVertices[i]);
                    vertices[index * sourceVertices.Length + i] = new Vertex { Position = position, Uv = sourceUv[i] };
                    if (index == 0 && i == 0) minimum = maximum = position;
                    else { minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position); }
                }
            }

            /// <summary>提交仅存活条的顶点范围与索引范围，更新世界包围盒。</summary>
            /// <param name="count">本次实际可见条数。</param><remarks>零可见时不绘制；不使用退化三角形隐藏死亡槽。</remarks>
            internal void Upload(int count)
            {
                if (count == 0) return;
                Mesh.SetVertexBufferData(vertices, 0, 0, count * sourceVertices.Length, 0, MeshUpdateFlags.DontRecalculateBounds);
                var bounds = new Bounds((minimum + maximum) * .5f, maximum - minimum);
                Mesh.SetSubMesh(0, new SubMeshDescriptor(0, count * sourceIndices.Length) { bounds = bounds, vertexCount = count * sourceVertices.Length }, MeshUpdateFlags.DontRecalculateBounds);
                Mesh.bounds = bounds;
            }

            /// <summary>本局结束后释放持久顶点与动态网格；共享源资源不受影响。</summary>
            /// <remarks>由外层幂等Dispose保证只执行一次；编辑器非Play模式立即销毁对象。</remarks>
            public void Dispose()
            {
                if (vertices.IsCreated) vertices.Dispose();
                if (Application.isPlaying) UnityEngine.Object.Destroy(Mesh); else UnityEngine.Object.DestroyImmediate(Mesh);
            }
        }
    }

}
