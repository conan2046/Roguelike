using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Roguelike.Core;
using Roguelike.Core.Features;
using Roguelike.Core.Resources;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;

namespace Roguelike.Features.Performance
{
    /// <summary>
    /// Activates a data-driven standalone performance scenario only when command-line launch arguments request one.
    /// </summary>
    public sealed class PerformanceFeature : IGameFeature
    {
        /// <summary>Gets the diagnostic feature name.</summary>
        public string Name => "Performance";

        /// <summary>
        /// Reads the optional performance launch request after Luban is ready and creates its persistent runner.
        /// </summary>
        /// <param name="services">Initialized application services used to read configuration and load visual resources.</param>
        /// <param name="cancellationToken">Startup token checked before creating Unity objects.</param>
        /// <returns>A completed task after the runner is configured, or immediately when no performance scenario was requested.</returns>
        /// <exception cref="ArgumentNullException">Thrown when application services are missing.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a requested scenario is absent from Luban TbPerformanceScenario.</exception>
        /// <remarks>Creates a persistent GameObject only in explicit performance-test launches; normal game launches remain unchanged.</remarks>
        public Task InitializeAsync(ApplicationServices services, CancellationToken cancellationToken)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!PerformanceLaunchOptions.TryCreate(Environment.GetCommandLineArgs(), out PerformanceLaunchOptions options))
            {
                return Task.CompletedTask;
            }

            PerformanceScenarioConfig scenario = services.Config.Tables.TbPerformanceScenario.GetOrDefault(options.ScenarioId);
            if (scenario == null)
            {
                throw new InvalidOperationException(
                    $"Luban TbPerformanceScenario does not contain requested ID {options.ScenarioId}.");
            }

            var root = new GameObject("[Roguelike.Performance]");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.AddComponent<PerformanceScenarioRunner>().Configure(services, scenario, options);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Contains the command-line values that select a scenario and its machine-readable evidence files.
    /// </summary>
    internal sealed class PerformanceLaunchOptions
    {
        private const string ScenarioArgument = "-performanceScenario";
        private const string OutputArgument = "-performanceOutput";
        private const string ScreenshotArgument = "-performanceScreenshot";

        /// <summary>Gets the Luban TbPerformanceScenario identifier requested by the caller.</summary>
        public int ScenarioId { get; private set; }

        /// <summary>Gets the absolute JSON result path owned by this launch.</summary>
        public string OutputPath { get; private set; }

        /// <summary>Gets the absolute screenshot path owned by this launch.</summary>
        public string ScreenshotPath { get; private set; }

        /// <summary>
        /// Detects an explicit performance request and validates all required paired arguments.
        /// </summary>
        /// <param name="arguments">Complete process argument list supplied by Unity.</param>
        /// <param name="options">Validated launch options when a scenario argument is present.</param>
        /// <returns>True when performance mode was requested; otherwise false.</returns>
        /// <exception cref="ArgumentException">Thrown when performance mode is requested with missing or invalid values.</exception>
        public static bool TryCreate(IReadOnlyList<string> arguments, out PerformanceLaunchOptions options)
        {
            string scenarioValue = ReadOptionalValue(arguments, ScenarioArgument);
            if (scenarioValue == null)
            {
                options = null;
                return false;
            }

            if (!int.TryParse(scenarioValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int scenarioId) ||
                scenarioId <= 0)
            {
                throw new ArgumentException($"Invalid {ScenarioArgument} value: '{scenarioValue}'.");
            }

            string outputPath = ReadRequiredValue(arguments, OutputArgument);
            string screenshotPath = ReadRequiredValue(arguments, ScreenshotArgument);
            options = new PerformanceLaunchOptions
            {
                ScenarioId = scenarioId,
                OutputPath = Path.GetFullPath(outputPath),
                ScreenshotPath = Path.GetFullPath(screenshotPath)
            };
            return true;
        }

        /// <summary>
        /// Reads a named argument value when the argument exists.
        /// </summary>
        /// <param name="arguments">Complete process argument list.</param>
        /// <param name="name">Exact command-line argument name.</param>
        /// <returns>The following non-empty value, or null when the argument is absent.</returns>
        /// <exception cref="ArgumentException">Thrown when the argument exists without a usable value.</exception>
        private static string ReadOptionalValue(IReadOnlyList<string> arguments, string name)
        {
            for (int index = 0; index < arguments.Count; index++)
            {
                if (!string.Equals(arguments[index], name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    throw new ArgumentException($"Command-line argument requires a value: {name}");
                }

                return arguments[index + 1];
            }

            return null;
        }

        /// <summary>
        /// Reads a mandatory argument value after performance mode has been selected.
        /// </summary>
        /// <param name="arguments">Complete process argument list.</param>
        /// <param name="name">Exact command-line argument name.</param>
        /// <returns>The following non-empty argument value.</returns>
        /// <exception cref="ArgumentException">Thrown when the named argument is absent.</exception>
        private static string ReadRequiredValue(IReadOnlyList<string> arguments, string name)
        {
            return ReadOptionalValue(arguments, name) ??
                   throw new ArgumentException($"Required performance argument is missing: {name}");
        }
    }

    /// <summary>
    /// Loads configured visuals, creates rendered ECS entities, samples standalone frame performance, and writes evidence.
    /// </summary>
    internal sealed class PerformanceScenarioRunner : MonoBehaviour
    {
        private const string ShaderName = "Universal Render Pipeline/Unlit";
        private const string BaseMapProperty = "_BaseMap";
        private const string BaseColorProperty = "_BaseColor";
        private const string GcAllocatedMarker = "GC Allocated In Frame";

        private readonly List<Material> materials = new List<Material>();
        private readonly List<Texture2D> textures = new List<Texture2D>();
        private ApplicationServices services;
        private PerformanceScenarioConfig scenario;
        private PerformanceLaunchOptions options;
        private World entityWorld;
        private EntityManager entityManager;
        private EntityQuery entityQuery;
        private Mesh entityMesh;
        private Camera performanceCamera;
        private bool ownsEntities;

        /// <summary>
        /// Supplies initialized services and starts the requested performance scenario.
        /// </summary>
        /// <param name="services">Application services whose configuration and resource providers are already ready.</param>
        /// <param name="scenario">Scenario row loaded from Luban TbPerformanceScenario.</param>
        /// <param name="options">Validated command-line output locations and scenario identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required dependency is missing.</exception>
        /// <remarks>Begins asynchronous resource loading and later creates ECS entities, a camera, and result files owned by this component.</remarks>
        public void Configure(
            ApplicationServices services,
            PerformanceScenarioConfig scenario,
            PerformanceLaunchOptions options)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            this.scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            RunAsync();
        }

        /// <summary>
        /// Loads all configured monster textures before creating the render world and sampling coroutine.
        /// </summary>
        /// <remarks>Creates Unity textures, materials, mesh, camera, and ECS entities; failures produce a JSON result and terminate the Player.</remarks>
        private async void RunAsync()
        {
            try
            {
                ValidateScenario();
                await LoadVisualMaterialsAsync();
                ConfigureFrameEnvironment();
                entityMesh = CreateQuadMesh();
                performanceCamera = CreatePerformanceCamera();
                SpawnEntities();
                StartCoroutine(SamplePerformance());
            }
            catch (Exception exception)
            {
                WriteFailureAndQuit(exception);
            }
        }

        /// <summary>
        /// Rejects invalid performance-table values before any Player state is changed.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when Luban TbPerformanceScenario contains unsafe or contradictory values.</exception>
        private void ValidateScenario()
        {
            if (scenario.EntityCount <= 0 ||
                scenario.WarmupSeconds < 0f ||
                scenario.SampleSeconds <= 0f ||
                scenario.TargetAverageFps <= 0f ||
                scenario.MaxP95FrameTimeMs <= 0f ||
                scenario.ScreenWidth <= 0 ||
                scenario.ScreenHeight <= 0 ||
                scenario.SpawnColumns <= 0 ||
                scenario.HorizontalSpacing <= 0f ||
                scenario.VerticalSpacing <= 0f ||
                scenario.EntityScale <= 0f ||
                scenario.MoveAmplitude < 0f ||
                scenario.MoveSpeedMin < 0f ||
                scenario.MoveSpeedMax < scenario.MoveSpeedMin ||
                scenario.MaxGcAllocBytesPerFrame < 0 ||
                scenario.CameraPadding < 0f ||
                scenario.VSyncCount < 0)
            {
                throw new InvalidOperationException(
                    $"Luban TbPerformanceScenario row {scenario.Id} contains invalid performance settings.");
            }

            if (scenario.MonsterIds_Ref == null || scenario.MonsterIds_Ref.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Luban TbPerformanceScenario row {scenario.Id} must reference at least one TbMonster row.");
            }
        }

        /// <summary>
        /// Resolves each configured monster through TbMonster, TbVisualSet, TbAnimationClip, and TbResource, then builds one instanced material.
        /// </summary>
        /// <returns>A task that completes after all referenced PNG bytes have been copied into runtime textures.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a configured visual chain, texture, or required URP shader is unavailable.</exception>
        /// <remarks>Loads raw resources through IResourceService and releases provider handles immediately after Texture2D copies their bytes.</remarks>
        private async Task LoadVisualMaterialsAsync()
        {
            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException($"Required performance shader was not found: {ShaderName}");
            }

            foreach (MonsterConfig monster in scenario.MonsterIds_Ref)
            {
                AnimationClipConfig clip = ResolveVisualClip(monster);
                using (IRawResourceHandle animationHandle = await services.Resources.LoadRawFileAsync(
                           clip.AniResourceId,
                           CancellationToken.None))
                using (IRawResourceHandle textureHandle = await services.Resources.LoadRawFileAsync(
                           clip.TextureResourceId,
                           CancellationToken.None))
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        name = $"PerformanceTexture_{monster.Id}",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    if (!ImageConversion.LoadImage(texture, textureHandle.Data, true))
                    {
                        Destroy(texture);
                        throw new InvalidOperationException(
                            $"TbResource {clip.TextureResourceId} could not be decoded as a performance texture.");
                    }

                    var material = CreateTransparentMaterial(shader, texture, monster.Id);
                    ConfigureFirstAnimationFrame(material, animationHandle.Data, texture);
                    textures.Add(texture);
                    materials.Add(material);
                }
            }
        }

        /// <summary>
        /// Chooses the first confirmed stand or move clip, falling back to the first declared clip when either semantic slot is empty.
        /// </summary>
        /// <param name="monster">Resolved TbMonster row whose visual chain is required.</param>
        /// <returns>A resolved TbAnimationClip row containing the texture resource ID.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the monster or its visual set does not resolve to a clip.</exception>
        private static AnimationClipConfig ResolveVisualClip(MonsterConfig monster)
        {
            if (monster == null || monster.VisualSetId_Ref == null)
            {
                throw new InvalidOperationException("A performance monster has no resolved TbVisualSet reference.");
            }

            VisualSetConfig visualSet = monster.VisualSetId_Ref;
            AnimationClipConfig clip = visualSet.StandClipId_Ref ?? visualSet.MoveClipId_Ref;
            if (clip == null && visualSet.ClipIds_Ref != null && visualSet.ClipIds_Ref.Count > 0)
            {
                clip = visualSet.ClipIds_Ref[0];
            }

            return clip ?? throw new InvalidOperationException(
                $"TbVisualSet {visualSet.Id} has no resolved animation clip for performance rendering.");
        }

        /// <summary>
        /// Creates an instancing-enabled transparent URP material for one configured monster texture.
        /// </summary>
        /// <param name="shader">Verified URP unlit shader included by the active render pipeline.</param>
        /// <param name="texture">Decoded texture selected through the Luban visual references.</param>
        /// <param name="monsterId">TbMonster identifier used only for diagnostics.</param>
        /// <returns>A runtime material compatible with Entities Graphics instancing.</returns>
        /// <remarks>Creates a Unity Material owned and destroyed by this runner.</remarks>
        private static Material CreateTransparentMaterial(Shader shader, Texture texture, int monsterId)
        {
            var material = new Material(shader)
            {
                name = $"PerformanceMaterial_{monsterId}",
                enableInstancing = true,
                renderQueue = (int)RenderQueue.Transparent
            };
            material.SetTexture(BaseMapProperty, texture);
            material.SetColor(BaseColorProperty, Color.white);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return material;
        }

        /// <summary>
        /// Reads the first atlas frame bounds from the paired ANI resource and applies them as material UV scale and offset.
        /// </summary>
        /// <param name="material">Runtime material whose BaseMap transform selects the displayed frame.</param>
        /// <param name="animationData">Raw bytes loaded through TbAnimationClip.aniResourceId and TbResource.path.</param>
        /// <param name="texture">Decoded paired atlas loaded through TbAnimationClip.textureResourceId.</param>
        /// <exception cref="InvalidOperationException">Thrown when the ANI header or first-frame bounds are invalid.</exception>
        /// <remarks>Changes only the runner-owned material and does not modify the source texture or files.</remarks>
        private static void ConfigureFirstAnimationFrame(
            Material material,
            IReadOnlyList<byte> animationData,
            Texture2D texture)
        {
            if (animationData == null || animationData.Count < 10)
            {
                throw new InvalidOperationException("ANI resource is too short to contain a first-frame rectangle.");
            }

            int frameCount =
                animationData[0] |
                (animationData[1] << 8) |
                (animationData[2] << 16) |
                (animationData[3] << 24);
            int top = ReadBigEndianUInt16(animationData, 4);
            int width = ReadBigEndianUInt16(animationData, 6);
            int height = ReadBigEndianUInt16(animationData, 8);
            if (frameCount <= 0 ||
                width <= 0 ||
                height <= 0 ||
                width > texture.width ||
                top + height > texture.height)
            {
                throw new InvalidOperationException(
                    $"ANI first-frame bounds are invalid: frames={frameCount}, top={top}, width={width}, " +
                    $"height={height}, texture={texture.width}x{texture.height}.");
            }

            var scale = new Vector2(width / (float)texture.width, height / (float)texture.height);
            var offset = new Vector2(0f, 1f - ((top + height) / (float)texture.height));
            material.SetTextureScale(BaseMapProperty, scale);
            material.SetTextureOffset(BaseMapProperty, offset);
        }

        /// <summary>
        /// Reads one unsigned 16-bit big-endian value used by legacy ANI frame metadata.
        /// </summary>
        /// <param name="data">ANI byte sequence containing the value.</param>
        /// <param name="offset">Zero-based byte offset of the high byte.</param>
        /// <returns>The decoded unsigned value represented as an integer.</returns>
        private static int ReadBigEndianUInt16(IReadOnlyList<byte> data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Applies the frame pacing and window settings sourced from Luban TbPerformanceScenario.
        /// </summary>
        /// <remarks>Changes process-wide screen size, VSync, target frame rate, and background execution for the test Player lifetime.</remarks>
        private void ConfigureFrameEnvironment()
        {
            Screen.SetResolution(scenario.ScreenWidth, scenario.ScreenHeight, FullScreenMode.Windowed);
            QualitySettings.vSyncCount = scenario.VSyncCount;
            Application.targetFrameRate = scenario.TargetFrameRate;
            Application.runInBackground = scenario.RunInBackground;
        }

        /// <summary>
        /// Creates a unit quad whose dimensions are later controlled by each LocalTransform scale.
        /// </summary>
        /// <returns>A runtime XY-plane mesh with normalized UV coordinates.</returns>
        /// <remarks>Creates a Unity Mesh owned and destroyed by this runner; its geometry is a technical rendering fixture.</remarks>
        private static Mesh CreateQuadMesh()
        {
            var mesh = new Mesh { name = "PerformanceQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>
        /// Disables scene cameras and creates an orthographic camera that contains the configured entity grid.
        /// </summary>
        /// <returns>The camera used for screenshot and standalone rendering evidence.</returns>
        /// <remarks>Temporarily disables existing cameras and creates a new persistent Camera destroyed with this runner.</remarks>
        private Camera CreatePerformanceCamera()
        {
            foreach (Camera existingCamera in FindObjectsOfType<Camera>())
            {
                existingCamera.enabled = false;
            }

            var cameraObject = new GameObject("[Roguelike.Performance.Camera]");
            cameraObject.transform.SetParent(transform, false);
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.035f, 0.055f, 1f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;

            int rows = Mathf.CeilToInt(scenario.EntityCount / (float)scenario.SpawnColumns);
            int visibleColumns = Mathf.Min(scenario.EntityCount, scenario.SpawnColumns);
            float gridWidth = ((visibleColumns - 1) * scenario.HorizontalSpacing) + scenario.EntityScale;
            float gridHeight = ((rows - 1) * scenario.VerticalSpacing) + scenario.EntityScale;
            float aspect = scenario.ScreenWidth / (float)scenario.ScreenHeight;
            camera.orthographicSize = Mathf.Max(
                (gridHeight * 0.5f) + scenario.CameraPadding,
                ((gridWidth * 0.5f) + scenario.CameraPadding) / aspect);
            return camera;
        }

        /// <summary>
        /// Creates one render prototype, clones it in bulk, and applies grid, motion, and material indices from the scenario.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the default ECS world is unavailable or the final entity count differs.</exception>
        /// <remarks>Creates and owns PerformanceEntityTag entities in the default ECS World; they are destroyed by OnDestroy.</remarks>
        private void SpawnEntities()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                throw new InvalidOperationException("Default ECS World is not available for the performance scenario.");
            }

            entityWorld = world;
            entityManager = world.EntityManager;
            Entity prototype = entityManager.CreateEntity();
            entityManager.AddComponentData(prototype, LocalTransform.Identity);
            entityManager.AddComponentData(prototype, new PerformanceEntityTag());
            entityManager.AddComponentData(prototype, new PerformanceMotion());

            var description = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            var renderMeshArray = new RenderMeshArray(materials.ToArray(), new[] { entityMesh });
            RenderMeshUtility.AddComponents(
                prototype,
                entityManager,
                description,
                renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            var random = new Random((uint)scenario.RandomSeed);
            int rows = Mathf.CeilToInt(scenario.EntityCount / (float)scenario.SpawnColumns);
            using (NativeArray<Entity> instances = entityManager.Instantiate(
                       prototype,
                       scenario.EntityCount,
                       Allocator.Temp))
            {
                for (int index = 0; index < instances.Length; index++)
                {
                    int column = index % scenario.SpawnColumns;
                    int row = index / scenario.SpawnColumns;
                    var origin = new float3(
                        (column - ((scenario.SpawnColumns - 1) * 0.5f)) * scenario.HorizontalSpacing,
                        (row - ((rows - 1) * 0.5f)) * scenario.VerticalSpacing,
                        0f);
                    entityManager.SetComponentData(
                        instances[index],
                        LocalTransform.FromPositionRotationScale(origin, quaternion.identity, scenario.EntityScale));
                    entityManager.SetComponentData(
                        instances[index],
                        new PerformanceMotion
                        {
                            Origin = origin,
                            Amplitude = scenario.MoveAmplitude,
                            AngularSpeed = random.NextFloat(scenario.MoveSpeedMin, scenario.MoveSpeedMax),
                            Phase = random.NextFloat(0f, math.PI * 2f)
                        });
                    entityManager.SetComponentData(
                        instances[index],
                        MaterialMeshInfo.FromRenderMeshArrayIndices(index % materials.Count, 0));
                }
            }

            entityManager.DestroyEntity(prototype);
            entityQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PerformanceEntityTag>());
            ownsEntities = true;
            int actualCount = entityQuery.CalculateEntityCount();
            if (actualCount != scenario.EntityCount)
            {
                throw new InvalidOperationException(
                    $"Performance entity count mismatch: expected={scenario.EntityCount}, actual={actualCount}.");
            }

            Debug.Log(
                $"[Roguelike.Performance] SPAWN_COMPLETED scenario={scenario.Id} entities={actualCount} " +
                $"materials={materials.Count}",
                this);
        }

        /// <summary>
        /// Waits for the configured warmup, captures visual evidence, then measures every rendered frame for the sample duration.
        /// </summary>
        /// <returns>A Unity coroutine that ends after evidence is written and the Player receives its pass/fail exit code.</returns>
        /// <remarks>Writes a screenshot and JSON report, logs the final result marker, and terminates the standalone Player.</remarks>
        private IEnumerator SamplePerformance()
        {
            double warmupStart = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - warmupStart < scenario.WarmupSeconds)
            {
                yield return null;
            }

            EnsureParentDirectory(options.ScreenshotPath);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(options.ScreenshotPath);
            yield return new WaitForEndOfFrame();

            int capacity = Mathf.Max(1, Mathf.CeilToInt(scenario.SampleSeconds * 1000f));
            var frameTimes = new List<float>(capacity);
            long maximumGcAllocBytes = 0;
            long totalGcAllocBytes = 0;
            using (ProfilerRecorder gcRecorder = ProfilerRecorder.StartNew(
                       ProfilerCategory.Memory,
                       GcAllocatedMarker))
            {
                double sampleStart = Time.realtimeSinceStartupAsDouble;
                while (Time.realtimeSinceStartupAsDouble - sampleStart < scenario.SampleSeconds)
                {
                    yield return null;
                    frameTimes.Add(Time.unscaledDeltaTime * 1000f);
                    if (gcRecorder.Valid)
                    {
                        long allocated = gcRecorder.LastValue;
                        maximumGcAllocBytes = Math.Max(maximumGcAllocBytes, allocated);
                        totalGcAllocBytes += allocated;
                    }
                }

                double durationSeconds = Time.realtimeSinceStartupAsDouble - sampleStart;
                PerformanceResult result = BuildResult(
                    frameTimes,
                    durationSeconds,
                    gcRecorder.Valid,
                    maximumGcAllocBytes,
                    totalGcAllocBytes);
                WriteResult(result);
                Debug.Log(
                    $"[Roguelike.Performance] RESULT scenario={result.scenarioId} pass={result.pass} " +
                    $"entities={result.entityCount} frames={result.frameCount} averageFps={result.averageFps:F2} " +
                    $"p95FrameTimeMs={result.p95FrameTimeMs:F3} onePercentLowFps={result.onePercentLowFps:F2} " +
                    $"maxGcAllocBytesPerFrame={result.maxGcAllocBytesPerFrame}",
                    this);
                yield return null;
                Application.Quit(result.pass ? 0 : 3);
            }
        }

        /// <summary>
        /// Sorts completed samples and evaluates every threshold stored in Luban TbPerformanceScenario.
        /// </summary>
        /// <param name="frameTimes">Collected unscaled frame durations in milliseconds.</param>
        /// <param name="durationSeconds">Measured wall-clock sample duration.</param>
        /// <param name="gcRecorderValid">Whether Unity exposed the configured GC allocation marker.</param>
        /// <param name="maximumGcAllocBytes">Largest per-frame allocation observed during sampling.</param>
        /// <param name="totalGcAllocBytes">Sum of per-frame allocations observed during sampling.</param>
        /// <returns>A complete serializable performance result with pass/fail decision.</returns>
        private PerformanceResult BuildResult(
            List<float> frameTimes,
            double durationSeconds,
            bool gcRecorderValid,
            long maximumGcAllocBytes,
            long totalGcAllocBytes)
        {
            float[] sortedFrameTimes = frameTimes.ToArray();
            Array.Sort(sortedFrameTimes);
            float p95FrameTimeMs = ReadPercentile(sortedFrameTimes, 0.95f);
            float p99FrameTimeMs = ReadPercentile(sortedFrameTimes, 0.99f);
            float averageFps = durationSeconds > 0d ? (float)(frameTimes.Count / durationSeconds) : 0f;
            float onePercentLowFps = p99FrameTimeMs > 0f ? 1000f / p99FrameTimeMs : 0f;
            int entityCount = ownsEntities && entityWorld != null && entityWorld.IsCreated
                ? entityQuery.CalculateEntityCount()
                : 0;
            bool gcThresholdPassed =
                !gcRecorderValid ||
                maximumGcAllocBytes <= scenario.MaxGcAllocBytesPerFrame;
            bool pass =
                entityCount == scenario.EntityCount &&
                averageFps >= scenario.TargetAverageFps &&
                p95FrameTimeMs <= scenario.MaxP95FrameTimeMs &&
                gcThresholdPassed;

            return new PerformanceResult
            {
                scenarioId = scenario.Id,
                scenarioName = scenario.Name,
                pass = pass,
                entityCount = entityCount,
                frameCount = frameTimes.Count,
                sampleDurationSeconds = (float)durationSeconds,
                averageFps = averageFps,
                p95FrameTimeMs = p95FrameTimeMs,
                onePercentLowFps = onePercentLowFps,
                gcRecorderValid = gcRecorderValid,
                maxGcAllocBytesPerFrame = maximumGcAllocBytes,
                totalGcAllocBytes = totalGcAllocBytes,
                targetAverageFps = scenario.TargetAverageFps,
                maxAllowedP95FrameTimeMs = scenario.MaxP95FrameTimeMs,
                maxAllowedGcAllocBytesPerFrame = scenario.MaxGcAllocBytesPerFrame,
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                unityVersion = Application.unityVersion,
                timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                screenshotPath = options.ScreenshotPath,
                failure = string.Empty
            };
        }

        /// <summary>
        /// Returns a nearest-rank percentile from an ascending frame-time array.
        /// </summary>
        /// <param name="sortedValues">Ascending frame times produced after sampling.</param>
        /// <param name="percentile">Normalized percentile in the inclusive range zero to one.</param>
        /// <returns>The selected frame time, or zero when no frame was sampled.</returns>
        private static float ReadPercentile(float[] sortedValues, float percentile)
        {
            if (sortedValues.Length == 0)
            {
                return 0f;
            }

            int index = Mathf.Clamp(
                Mathf.CeilToInt(sortedValues.Length * percentile) - 1,
                0,
                sortedValues.Length - 1);
            return sortedValues[index];
        }

        /// <summary>
        /// Serializes a successful or threshold-failing result to the caller-owned JSON path.
        /// </summary>
        /// <param name="result">Completed performance result.</param>
        /// <remarks>Creates the parent directory and overwrites only the explicit output file for this Player launch.</remarks>
        private void WriteResult(PerformanceResult result)
        {
            EnsureParentDirectory(options.OutputPath);
            File.WriteAllText(
                options.OutputPath,
                JsonUtility.ToJson(result, true),
                new UTF8Encoding(false));
        }

        /// <summary>
        /// Produces machine-readable failure evidence when setup or resource loading prevents sampling.
        /// </summary>
        /// <param name="exception">Original setup exception recorded in the result and Player log.</param>
        /// <remarks>Writes the requested JSON path when possible, logs the exception, and exits the standalone Player with code two.</remarks>
        private void WriteFailureAndQuit(Exception exception)
        {
            try
            {
                WriteResult(new PerformanceResult
                {
                    scenarioId = scenario != null ? scenario.Id : options.ScenarioId,
                    scenarioName = scenario != null ? scenario.Name : string.Empty,
                    pass = false,
                    entityCount = 0,
                    unityVersion = Application.unityVersion,
                    timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    screenshotPath = options.ScreenshotPath,
                    failure = exception.ToString()
                });
            }
            catch (Exception writeException)
            {
                Debug.LogException(writeException, this);
            }

            Debug.LogException(exception, this);
            Application.Quit(2);
        }

        /// <summary>
        /// Creates the direct parent directory for one explicit evidence file path.
        /// </summary>
        /// <param name="path">Absolute result or screenshot path supplied by the test launcher.</param>
        /// <exception cref="InvalidOperationException">Thrown when the path has no parent directory.</exception>
        private static void EnsureParentDirectory(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException($"Evidence path has no parent directory: {path}");
            }

            Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// Called by Unity when the runner is destroyed to remove its ECS entities and release all runtime render objects.
        /// </summary>
        /// <remarks>Destroys only PerformanceEntityTag entities and Unity objects created by this component.</remarks>
        private void OnDestroy()
        {
            if (ownsEntities && entityWorld != null && entityWorld.IsCreated)
            {
                entityManager.DestroyEntity(entityQuery);
                entityQuery.Dispose();
                ownsEntities = false;
            }

            foreach (Material material in materials)
            {
                Destroy(material);
            }

            foreach (Texture2D texture in textures)
            {
                Destroy(texture);
            }

            if (entityMesh != null)
            {
                Destroy(entityMesh);
            }

            if (performanceCamera != null)
            {
                Destroy(performanceCamera.gameObject);
            }
        }
    }

    /// <summary>
    /// Defines the JSON evidence written by a completed or failed standalone performance scenario.
    /// </summary>
    [Serializable]
    internal sealed class PerformanceResult
    {
        public int scenarioId;
        public string scenarioName;
        public bool pass;
        public int entityCount;
        public int frameCount;
        public float sampleDurationSeconds;
        public float averageFps;
        public float p95FrameTimeMs;
        public float onePercentLowFps;
        public bool gcRecorderValid;
        public long maxGcAllocBytesPerFrame;
        public long totalGcAllocBytes;
        public float targetAverageFps;
        public float maxAllowedP95FrameTimeMs;
        public long maxAllowedGcAllocBytesPerFrame;
        public int screenWidth;
        public int screenHeight;
        public string unityVersion;
        public string timestampUtc;
        public string screenshotPath;
        public string failure;
    }
}
