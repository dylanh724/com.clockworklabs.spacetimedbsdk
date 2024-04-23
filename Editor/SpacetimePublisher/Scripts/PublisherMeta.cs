using System.IO;

namespace SpacetimeDB.Editor
{
    using static SpacetimeMeta;
    
    /// Static metadata for PublisherWindow
    /// Looking for more? See SpacetimeMeta.cs
    public static class PublisherMeta
    {
        public enum FoldoutGroupType
        {
            Server,
            Identity,
            Publish,
            PublishResult,
        }

        public const string TOP_BANNER_CLICK_LINK = "https://spacetimedb.com/docs/modules";
        public const string INSTALL_WASM_OPT_URL = "https://github.com/WebAssembly/binaryen/releases";
        
        public static string PUBLISHER_DIR_PATH => SpacetimeWindow.NormalizePath(
            Path.Join(SPACETIMEDB_EDITOR_DIR_PATH, "SpacetimePublisher"));
        
        public static string PATH_TO_UXML => SpacetimeWindow.NormalizePath(
            Path.Join(PUBLISHER_DIR_PATH, "PublisherWindowComponents.uxml"));
        public static string PATH_TO_USS => SpacetimeWindow.NormalizePath(
            Path.Join(PUBLISHER_DIR_PATH, "PublisherWindowStyles.uss"));

        public const string AUTOGEN_DIR_NAME = "SpacetimeDbAutogen";
        public static string PATH_TO_AUTOGEN_DIR => SpacetimeWindow.NormalizePath(
            Path.Join(UnityEngine.Application.dataPath, AUTOGEN_DIR_NAME));
        
        public static string RELATIVE_PATH_TO_AUTOGEN_DIR => SpacetimeWindow.NormalizePath(
            Path.Join("Assets", AUTOGEN_DIR_NAME));
    }
}