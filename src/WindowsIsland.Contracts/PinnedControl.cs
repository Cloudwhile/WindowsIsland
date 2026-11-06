namespace WindowsIsland.Contracts;

/// <summary>标识宿主支持的固定内容交互控件。</summary>
public enum PinnedControlKind
{
    /// <summary>触发一次操作的按钮。</summary>
    Button,
    /// <summary>切换布尔值的开关。</summary>
    Toggle,
    /// <summary>按步长调整数值的滑块。</summary>
    Slider
}

/// <summary>描述一个由宿主渲染的交互控件，未使用的字段保留默认值。</summary>
/// <param name="Id">当前内容内唯一的控件标识。</param>
/// <param name="Label">文案及辅助功能名称。</param>
/// <param name="Kind">控件类型。</param>
/// <param name="IsEnabled">是否允许交互。</param>
/// <param name="IsOn">开关当前值。</param>
/// <param name="Minimum">滑块最小值。</param>
/// <param name="Maximum">滑块最大值。</param>
/// <param name="Value">滑块当前值。</param>
/// <param name="Step">滑块步长，必须为正数。</param>
/// <param name="Symbol">可选按钮图标名称，未提供时使用文案。</param>
public sealed record PinnedControl(string Id, string Label, PinnedControlKind Kind, bool IsEnabled = true,
    bool IsOn = false, double Minimum = 0, double Maximum = 1, double Value = 0, double Step = 1, string? Symbol = null);

/// <summary>描述经宿主校验并交还给插件的一次交互。</summary>
/// <param name="ContentId">当前固定内容标识。</param>
/// <param name="ControlId">控件标识。</param>
/// <param name="IsOn">开关提交的值，其他控件为空引用。</param>
/// <param name="Value">滑块提交的值，其他控件为空引用。</param>
public sealed record PinnedControlInteraction(string ContentId, string ControlId, bool? IsOn = null, double? Value = null);
