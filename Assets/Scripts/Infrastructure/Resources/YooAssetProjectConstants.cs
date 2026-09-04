namespace Roguelike.Infrastructure.Resources
{
    /// <summary>
    /// Centralizes provider-level identifiers that bootstrap YooAsset before Luban tables are available.
    /// </summary>
    public static class YooAssetProjectConstants
    {
        /// <summary>Default package name used by the single-player client.</summary>
        public const string PackageName = "DefaultPackage";

        /// <summary>Collector tag used only to bootstrap the Luban table binaries.</summary>
        public const string LubanConfigTag = "luban-config";
    }
}
