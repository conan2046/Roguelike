namespace Roguelike.Infrastructure.Resources
{
    /// <summary>
    /// Centralizes provider-level identifiers that bootstrap YooAsset before Luban tables are available.
    /// </summary>
    public static class YooAssetProjectConstants
    {
        /// <summary>Raw-file package containing Luban binaries and legacy ProjectX content.</summary>
        public const string RawPackageName = "DefaultPackage";

        /// <summary>AssetBundle package containing imported Unity assets under GameContent.</summary>
        public const string GameAssetPackageName = "GameAssetPackage";

        /// <summary>Collector tag used only to bootstrap the Luban table binaries.</summary>
        public const string LubanConfigTag = "luban-config";
    }
}
