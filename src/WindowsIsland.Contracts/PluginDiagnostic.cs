namespace WindowsIsland.Contracts;

/// <summary>描述一次插件诊断，由宿主补充时间、应用版本和错误编号。</summary>
/// <param name="Area">固定的模块标识。</param>
/// <param name="Operation">固定的操作标识。</param>
/// <param name="IsFatal">是否为致命错误。</param>
/// <param name="AdditionalInformation">可选的结构化错误元数据。</param>
public sealed record PluginDiagnostic(string Area, string Operation, bool IsFatal = false,
    PluginDiagnosticInfo? AdditionalInformation = null);

/// <summary>描述诊断允许携带的附加信息，不包含异常消息、原始堆栈或自由文本。</summary>
/// <param name="ExceptionType">异常类型的完整名称。</param>
/// <param name="HResult">异常的数值错误代码。</param>
/// <param name="ClientVersion">第三方客户端文件版本，仅包含数字和点。</param>
public sealed record PluginDiagnosticInfo(string? ExceptionType = null, int? HResult = null, string? ClientVersion = null);
