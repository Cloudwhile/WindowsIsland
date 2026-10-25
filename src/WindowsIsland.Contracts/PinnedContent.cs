namespace WindowsIsland.Contracts;

/// <summary>描述由宿主调度的单个固定内容，不包含窗口或界面对象。</summary>
/// <param name="Id">插件内稳定且非空的内容标识。</param>
/// <param name="PluginId">提交内容的插件标识，必须与上下文一致。</param>
/// <param name="Title">供辅助功能和提示使用的标题。</param>
/// <param name="CompactText">紧凑胶囊显示的当前值。</param>
/// <param name="Body">可选的内容说明。</param>
/// <param name="ActionId">可选的插件点击动作标识，不作为地址或命令执行。</param>
/// <param name="Symbol">可选的图标名称，宿主不识别时使用默认图标。</param>
/// <param name="Controls">可选的交互控件快照，由宿主创建原生控件。</param>
/// <param name="CustomPanelXaml">保留给宿主内置来源的自定义面板；所有外部插件均忽略此字段，使用结构化控件。</param>
public sealed record PinnedContent(string Id, string PluginId, string Title, string CompactText,
    string? Body = null, string? ActionId = null, string? Symbol = null,
    IReadOnlyList<PinnedControl>? Controls = null, string? CustomPanelXaml = null);

/// <summary>提供绑定到当前插件的固定内容提交能力，可从后台线程调用。</summary>
public interface IPinnedContentSource
{
    /// <summary>提交或替换当前插件的固定内容；全局只呈现最近提交的一项。</summary>
    /// <param name="content">来源标识与当前上下文一致的内容。</param>
    void Submit(PinnedContent content);

    /// <summary>更新已提交的同标识内容，不恢复已撤下或被其他内容替换的项目。</summary>
    /// <param name="content">需要更新的完整内容快照。</param>
    void Update(PinnedContent content);

    /// <summary>撤下当前插件指定标识的固定内容。</summary>
    /// <param name="id">插件内的内容标识。</param>
    void Remove(string id);

    /// <summary>注册当前插件的交互处理器；宿主在后台串行回调，撤销上下文后不再调用。</summary>
    /// <param name="handler">接收当前内容交互的异步处理器，空引用表示取消注册。</param>
    void SetInteractionHandler(Func<PinnedControlInteraction, Task>? handler) { }
}
