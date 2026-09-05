using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using ProjectX.Migration;
using Roguelike.Animation;
using Roguelike.Animation.Rendering;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
using UnityEngine;
using UnityEngine.Rendering;

namespace Roguelike.Features.Combat.Rendering
{
    /// <summary>一局共享的 ANI、纹理、材质和预建帧网格；所有实例退出后统一释放。</summary>
    public sealed class CombatVisualResources : IDisposable
    {
        /// <summary>已加载的片段快照；网格索引按 ANI 全局帧及镜像寻址。</summary>
        public sealed class Clip
        {
            public CombatAnimation Animation;
            public CombatDirections Directions;
            public int MaterialIndex;
            public int[] MeshIndices;
        }
        private readonly Dictionary<(int, int), Clip> clips = new Dictionary<(int, int), Clip>();
        private readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        private readonly Dictionary<int, int> materialIndices = new Dictionary<int, int>();
        private readonly List<Material> materials = new List<Material>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private bool disposed;
        public Material[] Materials { get; private set; }
        public Mesh[] Meshes { get; private set; }
        public int TextureCount => textures.Count;
        public int ClipCount => clips.Count;

        /// <summary>入局时读取表现表 Shader/过滤方式，按资源 ID 加载必要片段，不扫描后缀或补缺省资源。</summary>
        /// <param name="scenario">已解析外键的 TbPerformanceScenario。</param>
        /// <param name="resources">已初始化的统一资源服务。</param>
        /// <param name="token">退出或取消入局令牌。</param>
        /// <returns>由调用者在渲染实例移除后释放的资源集合。</returns>
        /// <remarks>在 Unity 主线程调用，保留同步上下文。失败和取消释放部分创建对象与所有临时资源句柄。</remarks>
        /// <exception cref="InvalidOperationException">缺少配置、Shader、纹理或非法 ANI。</exception>
        /// <exception cref="OperationCanceledException">加载被取消。</exception>
        public static async Task<CombatVisualResources> LoadAsync(PerformanceScenarioConfig scenario, IResourceService resources, CancellationToken token)
        {
            if (scenario?.PresentationId_Ref == null || resources == null) throw new InvalidOperationException("Missing visual configuration or resources.");
            var config = scenario.PresentationId_Ref;
            if (string.IsNullOrWhiteSpace(config.ShaderName)) throw new InvalidOperationException("TbCombatPresentation.shaderName is required.");
            Shader shader = Shader.Find(config.ShaderName);
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Unavailable configured shader: " + config.ShaderName);
            FilterMode filter;
            switch (config.TextureFilterMode)
            {
                case "Point": filter = FilterMode.Point; break;
                case "Bilinear": filter = FilterMode.Bilinear; break;
                default: throw new InvalidOperationException("TbCombatPresentation.textureFilterMode must be Point or Bilinear.");
            }
            var result = new CombatVisualResources();
            try
            {
                await result.LoadVisualAsync(scenario.CharacterId_Ref.VisualSetId_Ref, false, scenario.CombatRulesId_Ref,
                    scenario.PresentationId_Ref, resources, shader, filter, token);
                foreach (var monster in scenario.MonsterIds_Ref)
                    await result.LoadVisualAsync(monster.VisualSetId_Ref, true, scenario.CombatRulesId_Ref,
                        scenario.PresentationId_Ref, resources, shader, filter, token);
                await result.LoadSkillAsync(scenario.CharacterId_Ref.DefaultSkillId_Ref, scenario.CombatRulesId_Ref,
                    scenario.PresentationId_Ref, resources, shader, filter, token);
                token.ThrowIfCancellationRequested();
                result.Materials = result.materials.ToArray(); result.Meshes = result.meshes.ToArray();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        /// <summary>正式关卡入局时共享加载全部单位和可解锁技能表现，避免每次生成实体重复读取 ANI。</summary>
        /// <param name="definition">由 TbStage 聚合并验证的正式单局定义。</param>
        /// <param name="resources">已初始化的统一资源服务。</param>
        /// <param name="token">退出或取消入局令牌。</param>
        /// <returns>覆盖角色、普通怪、Boss、Projectile、Impact 和 TargetArea 的共享资源集合。</returns>
        /// <remarks>在 Unity 主线程创建材质、纹理和网格；调用方必须在全部表现实例释放后 Dispose。</remarks>
        /// <exception cref="InvalidOperationException">配置、Shader、纹理或 ANI 不可用。</exception>
        /// <exception cref="OperationCanceledException">加载被取消。</exception>
        public static async Task<CombatVisualResources> LoadAsync(CombatRunDefinition definition, IResourceService resources, CancellationToken token)
        {
            if (definition?.Presentation == null || resources == null)
                throw new InvalidOperationException("Missing formal run visual configuration or resources.");
            var presentation = definition.Presentation;
            if (string.IsNullOrWhiteSpace(presentation.ShaderName))
                throw new InvalidOperationException("TbCombatPresentation.shaderName is required.");
            Shader shader = Shader.Find(presentation.ShaderName);
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("Unavailable configured shader: " + presentation.ShaderName);
            FilterMode filter;
            switch (presentation.TextureFilterMode)
            {
                case "Point": filter = FilterMode.Point; break;
                case "Bilinear": filter = FilterMode.Bilinear; break;
                default: throw new InvalidOperationException("TbCombatPresentation.textureFilterMode must be Point or Bilinear.");
            }

            var result = new CombatVisualResources();
            try
            {
                await result.LoadVisualAsync(definition.Character.VisualSetId_Ref, false, definition.CombatRules,
                    presentation, resources, shader, filter, token);
                foreach (var monster in definition.Monsters.Concat(new[] { definition.Boss.MonsterId_Ref }).GroupBy(item => item.Id).Select(group => group.First()))
                    await result.LoadVisualAsync(monster.VisualSetId_Ref, true, definition.CombatRules,
                        presentation, resources, shader, filter, token);
                foreach (var skill in definition.AvailableSkills)
                    await result.LoadSkillAsync(skill, definition.CombatRules, presentation, resources, shader, filter, token);
                token.ThrowIfCancellationRequested();
                result.Materials = result.materials.ToArray();
                result.Meshes = result.meshes.ToArray();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        /// <summary>为角色集合加载必需待机/移动，怪物另加载显式攻击；复用同集合已准备的帧。</summary>
        /// <param name="visual">TbVisualSet 显式片段引用及缩放。</param>
        /// <param name="monster">是否必须加载攻击。</param>
        /// <param name="rules">提供世界单位换算的 TbCombatRules。</param>
        /// <param name="presentation">提供方向及时钟的 TbCombatPresentation。</param>
        /// <param name="resources">统一资源入口。</param>
        /// <param name="shader">已验证的配置 Shader。</param>
        /// <param name="filter">配置解析的过滤方式。</param>
        /// <param name="token">取消令牌。</param>
        /// <returns>全部必需片段准备任务。</returns>
        /// <remarks>创建共享 Unity 对象，所有权归本资源集合。</remarks>
        /// <exception cref="InvalidOperationException">缺少显式片段或缩放非法。</exception>
        private async Task LoadVisualAsync(VisualSetConfig visual, bool monster, CombatRulesConfig rules, CombatPresentationConfig presentation,
            IResourceService resources, Shader shader, FilterMode filter, CancellationToken token)
        {
            if (visual?.StandClipId_Ref == null || visual.MoveClipId_Ref == null || (monster && visual.AttackClipId_Ref == null))
                throw new InvalidOperationException("Visual requires explicit idle/move and monster attack clips.");
            float scale = rules.WorldUnitsPerPixel * visual.ScalePermille / 1000f;
            CombatMath.Positive(scale);
            await LoadClipAsync(visual.Id, visual.StandClipId_Ref, scale, presentation, resources, shader, filter, token);
            await LoadClipAsync(visual.Id, visual.MoveClipId_Ref, scale, presentation, resources, shader, filter, token);
            if (monster) await LoadClipAsync(visual.Id, visual.AttackClipId_Ref, scale, presentation, resources, shader, filter, token);
        }

        /// <summary>按 TbSkill 的投递类型加载已确认的飞行、命中或目标位置片段。</summary>
        /// <param name="skill">正式初始或可解锁技能。</param>
        /// <param name="rules">提供世界像素比例的 TbCombatRules。</param>
        /// <param name="presentation">提供 ANI 时间单位的 TbCombatPresentation。</param>
        /// <param name="resources">统一资源入口。</param>
        /// <param name="shader">已验证的配置 Shader。</param>
        /// <param name="filter">配置解析的纹理过滤方式。</param>
        /// <param name="token">取消令牌。</param>
        /// <returns>该技能全部显式片段完成加载的任务。</returns>
        /// <exception cref="InvalidOperationException">技能投递类型或片段角色不符合协议。</exception>
        private async Task LoadSkillAsync(SkillConfig skill, CombatRulesConfig rules, CombatPresentationConfig presentation,
            IResourceService resources, Shader shader, FilterMode filter, CancellationToken token)
        {
            if (skill?.CombatProfileId_Ref == null) throw new InvalidOperationException("TbSkill: missing combat profile.");
            float scale = rules.WorldUnitsPerPixel;
            CombatMath.Positive(scale);
            if (skill.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Projectile)
            {
                if (skill.ProjectileClipId_Ref?.Action != EAnimationAction.Projectile ||
                    (skill.ImpactClipId_Ref != null && skill.ImpactClipId_Ref.Action != EAnimationAction.Impact))
                    throw new InvalidOperationException($"TbSkill {skill.Id}: invalid projectile visual roles.");
                await LoadClipAsync(skill.Id, skill.ProjectileClipId_Ref, scale, presentation, resources, shader, filter, token);
                if (skill.ImpactClipId_Ref != null)
                    await LoadClipAsync(skill.Id, skill.ImpactClipId_Ref, scale, presentation, resources, shader, filter, token);
                return;
            }
            if (skill.CombatProfileId_Ref.DeliveryType != ESkillDeliveryType.TargetArea || skill.AreaClipIds_Ref == null ||
                skill.AreaClipIds_Ref.Count == 0 || skill.AreaClipIds_Ref.Any(item => item?.Action != EAnimationAction.Area))
                throw new InvalidOperationException($"TbSkill {skill.Id}: invalid target-area visual roles.");
            foreach (var clip in skill.AreaClipIds_Ref)
                await LoadClipAsync(skill.Id, clip, scale, presentation, resources, shader, filter, token);
        }

        /// <summary>按 TbAnimationClip 的 ANI/PNG ID 加载，验证图集边界并预建普通与镜像网格。</summary>
        /// <param name="visualId">缩放所属表现集合。</param>
        /// <param name="config">显式片段配置。</param>
        /// <param name="scale">表配置换算的像素比例。</param>
        /// <param name="presentation">方向及时钟配置。</param>
        /// <param name="resources">统一资源入口。</param>
        /// <param name="shader">配置 Shader。</param>
        /// <param name="filter">配置过滤方式。</param>
        /// <param name="token">取消令牌。</param>
        /// <returns>片段准备任务。</returns>
        /// <remarks>临时原始句柄离开作用域即释放；对象创建后立即登记，确保异常可回收。</remarks>
        private async Task LoadClipAsync(int visualId, AnimationClipConfig config, float scale, CombatPresentationConfig presentation,
            IResourceService resources, Shader shader, FilterMode filter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var key = (visualId, config.Id);
            if (clips.ContainsKey(key)) return;
            using var raw = await resources.LoadRawFileAsync(config.AniResourceId, token);
            token.ThrowIfCancellationRequested();
            var data = CocosAniData.Parse(raw.Data);
            if (!textures.TryGetValue(config.TextureResourceId, out var texture))
            {
                using var png = await resources.LoadRawFileAsync(config.TextureResourceId, token);
                token.ThrowIfCancellationRequested();
                // 初始尺寸是 LoadImage 解码前的 API 占位，实际尺寸只能来自 PNG。
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
                textures.Add(config.TextureResourceId, texture);
                if (!ImageConversion.LoadImage(texture, png.Data, true)) throw new InvalidOperationException("Cannot decode TbResource " + config.TextureResourceId);
                var material = new Material(shader) { enableInstancing = true, renderQueue = (int)RenderQueue.Transparent };
                materialIndices.Add(config.TextureResourceId, materials.Count); materials.Add(material);
                if (!material.HasProperty("_BaseMap") || !material.HasProperty("_Surface")) throw new InvalidOperationException("Configured shader lacks URP Unlit material interface.");
                material.SetTexture("_BaseMap", texture); material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Surface", 1); material.SetFloat("_Blend", 0);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0); material.SetFloat("_Cull", (float)CullMode.Off);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            AniFrameLayout.ValidateAtlas(data, texture.width, texture.height);
            var animation = new CombatAnimation(data, presentation, config.Loop);
            var clip = new Clip { Animation = animation, MaterialIndex = materialIndices[config.TextureResourceId], MeshIndices = new int[data.frames.Length * 2] };
            for (int i = 0; i < clip.MeshIndices.Length; i++) clip.MeshIndices[i] = -1;
            if (config.Action == EAnimationAction.Stand || config.Action == EAnimationAction.Move)
                clip.Directions = new CombatDirections(presentation, animation);
            for (int action = 0; action < data.actions.Length; action++)
                for (int frame = 0; frame < data.actions[action].frames.Length; frame++)
                {
                    int global = data.actions[action].frames[frame];
                    if (clip.MeshIndices[global * 2] >= 0) continue;
                    var quads = AniFrameLayout.Build(data, action, frame, texture.width, texture.height);
                    clip.MeshIndices[global * 2] = meshes.Count; meshes.Add(AniMeshFactory.CreateFrame(quads, scale));
                    for (int i = 0; i < quads.Length; i++)
                    { quads[i].CenterX = -quads[i].CenterX; quads[i].U += quads[i].UWidth; quads[i].UWidth = -quads[i].UWidth; }
                    clip.MeshIndices[global * 2 + 1] = meshes.Count; meshes.Add(AniMeshFactory.CreateFrame(quads, scale));
                }
            clips.Add(key, clip);
        }

        /// <summary>播放更新时读取预加载片段，不加载资源或创建对象。</summary>
        /// <param name="visualId">集合 ID。</param>
        /// <param name="clipId">片段 ID。</param>
        /// <returns>共享片段。</returns>
        /// <exception cref="ObjectDisposedException">资源已释放。</exception>
        public Clip Read(int visualId, int clipId)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatVisualResources));
            return clips[(visualId, clipId)];
        }

        /// <summary>退出或加载失败时销毁共享网格、材质、纹理，允许重复释放。</summary>
        /// <remarks>调用前须移除所有渲染使用者；播放模式延迟销毁，编辑模式立即销毁，仅处理本集合创建的对象。</remarks>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var mesh in meshes) Release(mesh);
            foreach (var material in materials) Release(material);
            foreach (var texture in textures.Values) Release(texture);
            meshes.Clear(); materials.Clear(); textures.Clear(); clips.Clear(); materialIndices.Clear();
        }

        /// <summary>按 Unity 生命周期销毁本模块拥有的运行时对象。</summary>
        /// <param name="value">运行时创建对象，不得传入导入资产。</param>
        /// <remarks>播放模式延迟销毁，非播放模式立即释放。</remarks>
        private static void Release(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
