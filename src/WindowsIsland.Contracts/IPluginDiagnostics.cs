namespace WindowsIsland.Contracts;

/// <summary>定义当前插件的本机诊断写入通道。</summary>
public interface IPluginDiagnostics
{
    /// <summary>提交一条结构化诊断，由宿主管理采集开关、字段校验、合并和保留期限。</summary>
    /// <param name="diagnostic">使用固定区域与操作标识的诊断，不包含消息内容或个人信息。</param>
    void Write(PluginDiagnostic diagnostic);
}
