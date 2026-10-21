namespace WindowsIsland.Contracts;

/// <summary>提供插件合约的大版本及兼容性判断。</summary>
/// <remarks>仅相同大版本兼容。同一大版本禁止新增必填接口方法，新能力通过 IPluginContext.GetService 返回可选服务。
/// 发布新大版本必须同时提供迁移说明。</remarks>
public static class PluginContract
{
    /// <summary>当前合约的大版本。</summary>
    public const int Version = 1;

    /// <summary>判断插件要求的合约大版本是否与宿主支持的大版本相同，非正整数版本无效。</summary>
    /// <param name="pluginContractVersion">插件清单声明的合约大版本。</param>
    /// <param name="hostContractVersion">宿主支持的合约大版本，默认使用当前合约版本。</param>
    /// <returns>两个版本均有效且相等时为真，否则为假。</returns>
    public static bool IsCompatible(int pluginContractVersion, int hostContractVersion = Version) =>
        pluginContractVersion > 0 && hostContractVersion > 0 && pluginContractVersion == hostContractVersion;
}
