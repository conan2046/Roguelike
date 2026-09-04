using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Roguelike.Infrastructure.Resources;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace Roguelike.Infrastructure.Editor
{
    /// <summary>
    /// Builds the complete YooAsset package into StreamingAssets before producing a Windows offline player.
    /// </summary>
    public static class OfflineWindowsBuild
    {
        private const string PackageVersionArgument = "-packageVersion";
        private const string PlayerOutputArgument = "-playerOutput";
        private const string BuildinCatalogFileName = "BuildinCatalog.bytes";

        /// <summary>
        /// Builds a Windows offline player from the Unity menu using the PlayerSettings bundle version.
        /// </summary>
        /// <exception cref="BuildFailedException">Thrown when validation, resource building, or player building fails.</exception>
        /// <remarks>Writes ignored build artifacts under Bundles, Assets/StreamingAssets/yoo, and Builds/Windows.</remarks>
        [MenuItem("Roguelike/Build/Windows Offline Player")]
        public static void BuildFromMenu()
        {
            string packageVersion = PlayerSettings.bundleVersion;
            string outputPath = Path.Combine(
                "Builds",
                "Windows",
                packageVersion,
                Application.productName + ".exe");
            Build(packageVersion, outputPath);
        }

        /// <summary>
        /// Builds a Windows offline player from arguments supplied to Unity by the repository build script.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when a required command-line argument is missing.</exception>
        /// <exception cref="BuildFailedException">Thrown when validation, resource building, or player building fails.</exception>
        /// <remarks>Intended for Unity batch mode; its exception controls the Unity process exit code.</remarks>
        public static void BuildFromCommandLine()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string packageVersion = ReadRequiredArgument(arguments, PackageVersionArgument);
            string outputPath = ReadRequiredArgument(arguments, PlayerOutputArgument);
            Build(packageVersion, outputPath);
        }

        /// <summary>
        /// Validates the build environment, rebuilds all local package files, and then builds the Windows player.
        /// </summary>
        /// <param name="packageVersion">Release identifier stored in the YooAsset local manifest.</param>
        /// <param name="outputPath">Absolute or project-relative path of the Windows executable.</param>
        /// <exception cref="BuildFailedException">Thrown when any required input or build operation fails.</exception>
        /// <remarks>Replaces only the generated DefaultPackage directory below Assets/StreamingAssets/yoo.</remarks>
        private static void Build(string packageVersion, string outputPath)
        {
            ValidatePackageVersion(packageVersion);
            ValidateBuildTarget();
            string[] scenes = GetEnabledScenes();
            string absoluteOutputPath = GetAbsoluteProjectPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(absoluteOutputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                throw new BuildFailedException($"Player output directory is invalid: {absoluteOutputPath}");
            }

            Directory.CreateDirectory(outputDirectory);
            ProjectRenderPipelineSetup.Configure();
            ProjectYooAssetSetup.Configure();
            ValidateRawOnlyPackageContent();
            BuildResourcePackage(packageVersion);

            string editorPackageDirectory = Path.Combine(
                AssetBundleBuilderHelper.GetStreamingAssetsRoot(),
                YooAssetProjectConstants.PackageName);
            ValidatePackageFiles(editorPackageDirectory, packageVersion);
            BuildPlayer(scenes, absoluteOutputPath);
            ValidatePlayerPackage(absoluteOutputPath, packageVersion);

            Debug.Log($"[Roguelike.Build] WINDOWS_PLAYER_BUILD_SUCCEEDED path={absoluteOutputPath}");
        }

        /// <summary>
        /// Builds every collected resource and copies the complete package into the default built-in file system.
        /// </summary>
        /// <param name="packageVersion">Release identifier written to the YooAsset manifest files.</param>
        /// <exception cref="BuildFailedException">Thrown when YooAsset reports a failed build result.</exception>
        /// <remarks>Clears the previous built-in DefaultPackage before copying the new manifest, catalog, and bundles.</remarks>
        private static void BuildResourcePackage(string packageVersion)
        {
            var parameters = new RawFileBuildParameters
            {
                BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot(),
                BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot(),
                BuildPipeline = EBuildPipeline.RawFileBuildPipeline.ToString(),
                BuildBundleType = (int)EBuildBundleType.RawBundle,
                BuildTarget = BuildTarget.StandaloneWindows64,
                PackageName = YooAssetProjectConstants.PackageName,
                PackageVersion = packageVersion,
                PackageNote = $"Windows offline player {packageVersion}",
                ClearBuildCacheFiles = true,
                UseAssetDependencyDB = true,
                EnableSharePackRule = true,
                VerifyBuildingResult = true,
                FileNameStyle = EFileNameStyle.HashName,
                BuildinFileCopyOption = EBuildinFileCopyOption.ClearAndCopyAll,
                BuildinFileCopyParams = string.Empty,
                IncludePathInHash = true
            };

            var pipeline = new RawFileBuildPipeline();
            YooAsset.Editor.BuildResult result = pipeline.Run(parameters, true);
            if (!result.Success)
            {
                throw new BuildFailedException($"YooAsset offline package build failed: {result.ErrorInfo}");
            }

            Debug.Log(
                $"[Roguelike.Build] OFFLINE_RESOURCE_BUILD_SUCCEEDED version={packageVersion} " +
                $"directory={result.OutputPackageDirectory}");
        }

        /// <summary>
        /// Ensures the current raw-file package does not silently collect imported Unity assets it cannot deserialize.
        /// </summary>
        /// <exception cref="BuildFailedException">Thrown when GameContent contains a non-meta file requiring an AssetBundle package.</exception>
        private static void ValidateRawOnlyPackageContent()
        {
            string gameContentDirectory = Path.GetFullPath("Assets/GameContent");
            if (!Directory.Exists(gameContentDirectory))
            {
                return;
            }

            bool containsAsset = Directory.EnumerateFiles(
                    gameContentDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Any(path => !string.Equals(
                    Path.GetExtension(path),
                    ".meta",
                    StringComparison.OrdinalIgnoreCase));
            if (containsAsset)
            {
                throw new BuildFailedException(
                    "Assets/GameContent contains imported Unity assets. " +
                    "Add a dedicated AssetBundle package before building the raw-only offline package.");
            }
        }

        /// <summary>
        /// Builds the enabled project scenes into a Windows x64 executable.
        /// </summary>
        /// <param name="scenes">Enabled scene asset paths from EditorBuildSettings.</param>
        /// <param name="absoluteOutputPath">Absolute destination path of the executable.</param>
        /// <exception cref="BuildFailedException">Thrown when Unity does not report a successful player build.</exception>
        /// <remarks>Writes the executable and its sibling data directory but does not change scene or PlayerSettings assets.</remarks>
        private static void BuildPlayer(string[] scenes, string absoluteOutputPath)
        {
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = absoluteOutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            UnityEditor.Build.Reporting.BuildReport report = UnityEditor.BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    $"Windows player build failed: result={report.summary.result}, " +
                    $"errors={report.summary.totalErrors}, warnings={report.summary.totalWarnings}");
            }
        }

        /// <summary>
        /// Returns all enabled and existing scenes in their configured player order.
        /// </summary>
        /// <returns>Validated project-relative scene asset paths.</returns>
        /// <exception cref="BuildFailedException">Thrown when no enabled build scene exists or a configured scene is missing.</exception>
        private static string[] GetEnabledScenes()
        {
            var scenes = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes.Where(scene => scene.enabled))
            {
                if (!File.Exists(scene.path))
                {
                    throw new BuildFailedException($"Enabled build scene is missing: {scene.path}");
                }

                scenes.Add(scene.path);
            }

            if (scenes.Count == 0)
            {
                throw new BuildFailedException("No enabled scenes are configured in EditorBuildSettings.");
            }

            return scenes.ToArray();
        }

        /// <summary>
        /// Verifies that the installed editor can produce the required Windows x64 target without changing platforms.
        /// </summary>
        /// <exception cref="BuildFailedException">Thrown when the Windows module is unavailable or another target is active.</exception>
        private static void ValidateBuildTarget()
        {
            if (!UnityEditor.BuildPipeline.IsBuildTargetSupported(
                    BuildTargetGroup.Standalone,
                    BuildTarget.StandaloneWindows64))
            {
                throw new BuildFailedException("The Unity Windows Build Support module is not installed.");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new BuildFailedException(
                    "Active build target must be StandaloneWindows64. Start batch builds with '-buildTarget Win64'.");
            }
        }

        /// <summary>
        /// Rejects empty or path-like package versions before they reach generated output directories.
        /// </summary>
        /// <param name="packageVersion">Candidate YooAsset package version.</param>
        /// <exception cref="BuildFailedException">Thrown when the value is unsafe as a directory or manifest identifier.</exception>
        private static void ValidatePackageVersion(string packageVersion)
        {
            if (string.IsNullOrWhiteSpace(packageVersion) ||
                packageVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                packageVersion.Contains("/") ||
                packageVersion.Contains("\\"))
            {
                throw new BuildFailedException($"Package version is invalid: '{packageVersion}'.");
            }
        }

        /// <summary>
        /// Confirms that the package directory contains the local version, hash, manifest, and built-in catalog.
        /// </summary>
        /// <param name="packageDirectory">DefaultPackage directory to inspect.</param>
        /// <param name="packageVersion">Expected manifest version.</param>
        /// <exception cref="BuildFailedException">Thrown when any mandatory built-in file is absent.</exception>
        private static void ValidatePackageFiles(string packageDirectory, string packageVersion)
        {
            string packageName = YooAssetProjectConstants.PackageName;
            string[] requiredFiles =
            {
                YooAssetSettingsData.GetPackageVersionFileName(packageName),
                YooAssetSettingsData.GetPackageHashFileName(packageName, packageVersion),
                YooAssetSettingsData.GetManifestBinaryFileName(packageName, packageVersion),
                BuildinCatalogFileName
            };

            foreach (string fileName in requiredFiles)
            {
                string path = Path.Combine(packageDirectory, fileName);
                if (!File.Exists(path))
                {
                    throw new BuildFailedException($"Required built-in package file is missing: {path}");
                }
            }
        }

        /// <summary>
        /// Confirms that Unity copied the complete validated package into the finished player data directory.
        /// </summary>
        /// <param name="absoluteOutputPath">Absolute executable path returned to the build caller.</param>
        /// <param name="packageVersion">Expected YooAsset package version.</param>
        /// <exception cref="BuildFailedException">Thrown when the executable or its built-in package is missing.</exception>
        private static void ValidatePlayerPackage(string absoluteOutputPath, string packageVersion)
        {
            if (!File.Exists(absoluteOutputPath))
            {
                throw new BuildFailedException($"Windows executable was not created: {absoluteOutputPath}");
            }

            string playerDataDirectory = Path.Combine(
                Path.GetDirectoryName(absoluteOutputPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(absoluteOutputPath) + "_Data");
            string packageDirectory = Path.Combine(
                playerDataDirectory,
                "StreamingAssets",
                YooAssetSettingsData.GetDefaultYooFolderName(),
                YooAssetProjectConstants.PackageName);
            ValidatePackageFiles(packageDirectory, packageVersion);
        }

        /// <summary>
        /// Resolves a required named Unity command-line argument without interpreting its value.
        /// </summary>
        /// <param name="arguments">Complete process argument list.</param>
        /// <param name="name">Exact argument name whose following value is required.</param>
        /// <returns>The non-empty value immediately following the named argument.</returns>
        /// <exception cref="ArgumentException">Thrown when the argument or its value is missing.</exception>
        private static string ReadRequiredArgument(IReadOnlyList<string> arguments, string name)
        {
            for (int index = 0; index < arguments.Count - 1; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                {
                    string value = arguments[index + 1];
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            throw new ArgumentException($"Required Unity command-line argument is missing: {name}");
        }

        /// <summary>
        /// Converts a project-relative output path to an absolute normalized path while preserving absolute inputs.
        /// </summary>
        /// <param name="path">Player output path supplied by menu or command line.</param>
        /// <returns>Absolute normalized filesystem path.</returns>
        /// <exception cref="BuildFailedException">Thrown when the output does not name a Windows executable.</exception>
        private static string GetAbsoluteProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException($"Player output must be an .exe path: '{path}'.");
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            string combined = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
            return Path.GetFullPath(combined);
        }
    }
}
