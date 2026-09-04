using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Roguelike.Infrastructure.Editor
{
    /// <summary>
    /// Creates and assigns the reproducible Universal Render Pipeline assets required by Entities Graphics.
    /// </summary>
    public static class ProjectRenderPipelineSetup
    {
        private const string SettingsFolder = "Assets/Settings";
        private const string RendererAssetPath = SettingsFolder + "/RoguelikeUniversalRenderer.asset";
        private const string PipelineAssetPath = SettingsFolder + "/RoguelikeUniversalRenderPipeline.asset";
        private const string PerformanceShaderName = "Universal Render Pipeline/Unlit";

        /// <summary>
        /// Creates missing renderer assets, applies the desktop performance profile, and assigns the project pipeline.
        /// </summary>
        /// <remarks>Writes project assets and GraphicsSettings; repeated calls reuse the same assets and are idempotent.</remarks>
        [MenuItem("Roguelike/Setup/Configure Universal Render Pipeline")]
        public static void Configure()
        {
            EnsureSettingsFolder();
            UniversalRendererData rendererData = LoadOrCreateRendererData();
            UniversalRenderPipelineAsset pipelineAsset = LoadOrCreatePipelineAsset(rendererData);

            pipelineAsset.supportsHDR = false;
            pipelineAsset.supportsCameraDepthTexture = false;
            pipelineAsset.supportsCameraOpaqueTexture = false;
            pipelineAsset.maxAdditionalLightsCount = 0;
            pipelineAsset.msaaSampleCount = 1;
            pipelineAsset.renderScale = 1f;
            pipelineAsset.useSRPBatcher = true;

            GraphicsSettings.renderPipelineAsset = pipelineAsset;
            EnsureAlwaysIncludedShader(PerformanceShaderName);
            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(pipelineAsset);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Roguelike.Setup] URP_CONFIGURED pipeline={PipelineAssetPath}");
        }

        /// <summary>
        /// Adds a dynamically requested shader to GraphicsSettings so Player stripping cannot remove it.
        /// </summary>
        /// <param name="shaderName">Exact Unity shader name consumed by the runtime performance material.</param>
        /// <exception cref="System.InvalidOperationException">Thrown when the installed render pipeline does not expose the required shader.</exception>
        /// <remarks>Updates only the m_AlwaysIncludedShaders project setting and preserves all existing entries.</remarks>
        private static void EnsureAlwaysIncludedShader(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new System.InvalidOperationException($"Required project shader was not found: {shaderName}");
            }

            var serializedSettings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty shaders = serializedSettings.FindProperty("m_AlwaysIncludedShaders");
            for (int index = 0; index < shaders.arraySize; index++)
            {
                if (shaders.GetArrayElementAtIndex(index).objectReferenceValue == shader)
                {
                    return;
                }
            }

            int newIndex = shaders.arraySize;
            shaders.InsertArrayElementAtIndex(newIndex);
            shaders.GetArrayElementAtIndex(newIndex).objectReferenceValue = shader;
            serializedSettings.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Creates the settings folder when the project has not configured rendering before.
        /// </summary>
        /// <remarks>Creates one Unity asset folder and its meta file only on the first setup run.</remarks>
        private static void EnsureSettingsFolder()
        {
            if (!AssetDatabase.IsValidFolder(SettingsFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }
        }

        /// <summary>
        /// Loads the committed Universal Renderer data or creates it through Unity's serialization API.
        /// </summary>
        /// <returns>The renderer data referenced by the project pipeline asset.</returns>
        /// <remarks>Creates and saves RendererAssetPath only when it is missing.</remarks>
        private static UniversalRendererData LoadOrCreateRendererData()
        {
            UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            if (rendererData != null)
            {
                return rendererData;
            }

            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            rendererData.name = "RoguelikeUniversalRenderer";
            AssetDatabase.CreateAsset(rendererData, RendererAssetPath);
            return rendererData;
        }

        /// <summary>
        /// Loads the committed pipeline asset or creates one bound to the supplied renderer data.
        /// </summary>
        /// <param name="rendererData">Universal Renderer data used by the new pipeline.</param>
        /// <returns>The render pipeline asset assigned in GraphicsSettings.</returns>
        /// <remarks>Creates and saves PipelineAssetPath only when it is missing.</remarks>
        private static UniversalRenderPipelineAsset LoadOrCreatePipelineAsset(UniversalRendererData rendererData)
        {
            UniversalRenderPipelineAsset pipelineAsset =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            if (pipelineAsset != null)
            {
                return pipelineAsset;
            }

            pipelineAsset = UniversalRenderPipelineAsset.Create(rendererData);
            pipelineAsset.name = "RoguelikeUniversalRenderPipeline";
            AssetDatabase.CreateAsset(pipelineAsset, PipelineAssetPath);
            return pipelineAsset;
        }
    }
}
