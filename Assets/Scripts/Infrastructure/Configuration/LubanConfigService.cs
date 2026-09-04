using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Luban;
using Roguelike.Core.Configuration;
using Roguelike.Core.Startup;
using Roguelike.Infrastructure.Resources;
using YooAsset;

namespace Roguelike.Infrastructure.Configuration
{
    /// <summary>
    /// Loads every Luban binary collected under the YooAsset bootstrap tag and exposes one resolved table container.
    /// </summary>
    public sealed class LubanConfigService : IConfigService
    {
        private readonly YooAssetPackageInitializer packageInitializer;
        private Tables tables;

        /// <summary>
        /// Creates the configuration service bound to the already ordered YooAsset package stage.
        /// </summary>
        /// <param name="packageInitializer">Owner of the package that contains generated Luban bytes.</param>
        /// <exception cref="ArgumentNullException">Thrown when the package initializer is missing.</exception>
        public LubanConfigService(YooAssetPackageInitializer packageInitializer)
        {
            this.packageInitializer = packageInitializer ?? throw new ArgumentNullException(nameof(packageInitializer));
        }

        /// <summary>Gets whether all Luban tables have been loaded and resolved.</summary>
        public bool IsInitialized => tables != null;

        /// <summary>
        /// Gets the generated table container after initialization.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown before <see cref="InitializeAsync"/> succeeds.</exception>
        public Tables Tables => tables ?? throw new InvalidOperationException("Luban configuration is not initialized.");

        /// <summary>
        /// Loads tagged raw bytes from YooAsset, indexes them by Luban table name, and constructs generated Tables.
        /// </summary>
        /// <param name="cancellationToken">Token observed before and after each YooAsset operation.</param>
        /// <returns>A task that completes when cross-table references have been resolved.</returns>
        /// <remarks>Data comes from all generated files under the YooAsset tag defined by <see cref="YooAssetProjectConstants.LubanConfigTag"/>; each temporary provider handle is released after its bytes are copied.</remarks>
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourcePackage package = packageInitializer.Package;
            AssetInfo[] assets = package.GetAssetInfos(YooAssetProjectConstants.LubanConfigTag);
            if (assets == null || assets.Length == 0)
            {
                throw new InvalidOperationException("No Luban configuration assets were collected by YooAsset.");
            }

            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetInfo assetInfo in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (RawFileHandle handle = package.LoadRawFileAsync(assetInfo))
                {
                    await handle.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handle.Status != EOperationStatus.Succeed)
                    {
                        throw new InvalidOperationException($"Failed to load Luban table '{assetInfo.AssetPath}': {handle.LastError}");
                    }

                    string tableName = Path.GetFileNameWithoutExtension(assetInfo.AssetPath);
                    data.Add(tableName, handle.GetRawFileData());
                }
            }

            tables = new Tables(tableName => CreateBuffer(data, tableName));
        }

        /// <summary>
        /// Wraps one generated table payload and reports a precise bootstrap error when a table is missing.
        /// </summary>
        /// <param name="data">Loaded table bytes indexed by generated table name.</param>
        /// <param name="tableName">Name requested by the generated Tables constructor.</param>
        /// <returns>A Luban byte buffer positioned at the start of the table payload.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the YooAsset collector omitted a generated table.</exception>
        private static ByteBuf CreateBuffer(IReadOnlyDictionary<string, byte[]> data, string tableName)
        {
            if (!data.TryGetValue(tableName, out byte[] bytes))
            {
                throw new KeyNotFoundException($"Missing Luban binary table '{tableName}'.");
            }

            return new ByteBuf(bytes);
        }
    }

    /// <summary>
    /// Adapts configuration initialization into the ordered application startup pipeline.
    /// </summary>
    public sealed class ConfigurationInitializationStep : IStartupStep
    {
        private readonly IConfigService configService;

        /// <summary>
        /// Creates a configuration stage that depends only on the Core service contract.
        /// </summary>
        /// <param name="configService">Unified Luban configuration entry point.</param>
        /// <exception cref="ArgumentNullException">Thrown when the service is missing.</exception>
        public ConfigurationInitializationStep(IConfigService configService)
        {
            this.configService = configService ?? throw new ArgumentNullException(nameof(configService));
        }

        /// <summary>Gets the diagnostic startup stage name.</summary>
        public string Name => "Configuration";

        /// <summary>
        /// Loads generated Luban data after the resource package stage has completed.
        /// </summary>
        /// <param name="cancellationToken">Token forwarded to configuration loading.</param>
        /// <returns>A task that completes when all tables are available.</returns>
        /// <remarks>Updates the application-owned configuration service and must run after the resource stage.</remarks>
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            return configService.InitializeAsync(cancellationToken);
        }
    }
}
