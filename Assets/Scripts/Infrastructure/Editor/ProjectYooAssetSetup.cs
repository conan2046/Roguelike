using System;
using System.IO;
using Roguelike.Infrastructure.Resources;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

namespace Roguelike.Infrastructure.Editor
{
    /// <summary>
    /// Maps collected files to the exact project-relative locations stored by Luban TbResource.path.
    /// </summary>
    [DisplayName("定位地址: 项目资源路径")]
    public sealed class ProjectResourceAddressRule : IAddressRule
    {
        private const string StreamingAssetsPrefix = "Assets/StreamingAssets/";
        private const string GameContentPrefix = "Assets/GameContent/";
        private const string AssetsPrefix = "Assets/";

        /// <summary>
        /// Removes the physical Unity root while retaining the provider-relative path stored by TbResource.
        /// </summary>
        /// <param name="data">YooAsset collector context containing the source asset path.</param>
        /// <returns>A stable address matching TbResource.path for StreamingAssets and GameContent collectors.</returns>
        public string GetAssetAddress(AddressRuleData data)
        {
            string normalized = data.AssetPath.Replace('\\', '/');
            if (normalized.StartsWith(StreamingAssetsPrefix, StringComparison.Ordinal))
            {
                return normalized.Substring(StreamingAssetsPrefix.Length);
            }

            return normalized.StartsWith(GameContentPrefix, StringComparison.Ordinal)
                ? normalized.Substring(AssetsPrefix.Length)
                : normalized;
        }
    }

    /// <summary>
    /// Creates the reproducible YooAsset collector settings used by editor simulation and offline package builds.
    /// </summary>
    public static class ProjectYooAssetSetup
    {
        private const string ConfigPath = "Assets/StreamingAssets/Config/Luban";
        private const string LegacyContentPath = "Assets/StreamingAssets/ProjectX";
        private const string GameContentPath = "Assets/GameContent";

        /// <summary>
        /// Rebuilds separate Raw and imported-asset packages with no CDN, download, or hot-update configuration.
        /// </summary>
        /// <remarks>Overwrites only YooAsset's generated collector settings asset and never modifies scenes or prefabs.</remarks>
        [MenuItem("Roguelike/Resources/Rebuild YooAsset Settings")]
        public static void Configure()
        {
            EnsureAssetFolder(GameContentPath);
            AssetBundleCollectorSettingData.ClearAll();

            AssetBundleCollectorPackage rawPackage = CreatePackage(
                YooAssetProjectConstants.RawPackageName,
                nameof(RawFileIgnoreRule),
                false);
            AssetBundleCollectorPackage gameAssetPackage = CreatePackage(
                YooAssetProjectConstants.GameAssetPackageName,
                nameof(NormalIgnoreRule),
                true);

            AddCollector(
                rawPackage,
                "LubanConfig",
                ConfigPath,
                nameof(PackRawFile),
                YooAssetProjectConstants.LubanConfigTag);
            AddCollector(rawPackage, "LegacyRawContent", LegacyContentPath, nameof(PackRawFile), string.Empty);
            AddCollector(gameAssetPackage, "GameContent", GameContentPath, nameof(PackDirectory), string.Empty);

            AssetBundleCollectorSettingData.FixFile();
            AssetBundleCollectorSettingData.SaveFile();
            AssetDatabase.SaveAssets();
            Debug.Log("[Roguelike] YooAsset Raw and GameAsset collector settings rebuilt.");
        }

        /// <summary>
        /// Creates one addressable collector package with an explicit source-file policy.
        /// </summary>
        /// <param name="packageName">Stable package name shared by build and runtime initialization.</param>
        /// <param name="ignoreRuleName">YooAsset ignore rule compatible with the package build pipeline.</param>
        /// <param name="autoCollectShaders">Whether imported Unity shader dependencies are collected automatically.</param>
        /// <returns>The package to which source collectors can be attached.</returns>
        /// <remarks>Mutates only the in-memory collector settings rebuilt by <see cref="Configure"/>.</remarks>
        private static AssetBundleCollectorPackage CreatePackage(
            string packageName,
            string ignoreRuleName,
            bool autoCollectShaders)
        {
            AssetBundleCollectorPackage package = AssetBundleCollectorSettingData.CreatePackage(packageName);
            package.EnableAddressable = true;
            package.SupportExtensionless = false;
            package.LocationToLower = false;
            package.IncludeAssetGUID = false;
            package.AutoCollectShaders = autoCollectShaders;
            package.IgnoreRuleName = ignoreRuleName;
            return package;
        }

        /// <summary>
        /// Adds one directory collector with the common project address rule and explicit packing policy.
        /// </summary>
        /// <param name="package">Target local YooAsset package.</param>
        /// <param name="groupName">Diagnostic collector group name.</param>
        /// <param name="assetPath">Unity project directory to collect.</param>
        /// <param name="packRuleName">YooAsset packing rule type name.</param>
        /// <param name="tags">Optional semicolon-separated resource tags.</param>
        /// <exception cref="DirectoryNotFoundException">Thrown when a required source directory is absent.</exception>
        /// <remarks>Mutates the in-memory YooAsset collector settings that <see cref="Configure"/> persists.</remarks>
        private static void AddCollector(
            AssetBundleCollectorPackage package,
            string groupName,
            string assetPath,
            string packRuleName,
            string tags)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new DirectoryNotFoundException($"YooAsset collector directory is missing: {assetPath}");
            }

            AssetBundleCollectorGroup group = AssetBundleCollectorSettingData.CreateGroup(package, groupName);
            var collector = new AssetBundleCollector
            {
                CollectPath = string.Empty,
                CollectorGUID = guid,
                CollectorType = ECollectorType.MainAssetCollector,
                AddressRuleName = nameof(ProjectResourceAddressRule),
                PackRuleName = packRuleName,
                FilterRuleName = nameof(CollectAll),
                AssetTags = tags
            };
            AssetBundleCollectorSettingData.CreateCollector(group, collector);
        }

        /// <summary>
        /// Creates a missing nested Unity asset folder one segment at a time so its metadata is reproducible.
        /// </summary>
        /// <param name="assetPath">Assets-relative directory path.</param>
        /// <exception cref="ArgumentException">Thrown when the path is outside the Assets root.</exception>
        /// <remarks>Creates Unity asset folders and their metadata when a segment is missing.</remarks>
        private static void EnsureAssetFolder(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Asset folder must be under Assets.", nameof(assetPath));
            }

            string current = "Assets";
            string[] segments = normalized.Substring("Assets/".Length).Split('/');
            foreach (string segment in segments)
            {
                string next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segment);
                }

                current = next;
            }
        }
    }
}
