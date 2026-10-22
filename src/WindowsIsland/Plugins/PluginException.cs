namespace WindowsIsland.Plugins;

// 错误身份独立于 Message；新增失败点使用新码，已有数值保持稳定。
internal enum PluginErrorCode
{
    Zip = 1,
    Manifest = 2,
    Id = 3,
    Contract = 4,
    Entry = 5,
    Downgrade = 6,
    Version = 7,
    Path = 8,
    SourceConflict = 9,
    Start = 10,
    SourceAddress = 11,
    Hash = 12,
    IndexMismatch = 13,
    NotListed = 14,
    ManagedAssembly = 15,
    HostDependency = 16,
    MissingDependency = 17,
    SourceList = 18,
    IndexSize = 19,
    IndexDuplicate = 20,
    SourceMetadata = 21,
    IndexMetadata = 22,
    SourceEmpty = 23,
    DownloadRedirect = 24,
    SourceSize = 25,
    CacheSize = 26,
    ExpandedSize = 27,
    ArchiveLength = 28,
    ArchiveIncomplete = 29,
    IdConflict = 30,
    ManifestSize = 31,
    ManifestEmpty = 32,
    DirectoryId = 33,
    DescriptionSize = 34,
    EntryName = 35,
    DeveloperId = 36,
    Repository = 37,
    Permissions = 38,
    PathId = 39,
    NotReady = 40,
    RuntimeEntry = 41,
    DuplicateId = 42,
    RuntimeHostDependency = 43,
}

internal sealed class PluginException(PluginErrorCode code, string message) : IOException(message)
{
    public PluginErrorCode Code { get; } = code;
}
