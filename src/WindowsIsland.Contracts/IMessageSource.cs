namespace WindowsIsland.Contracts;

/// <summary>定义主动向宿主推送通知的消息来源，允许插件维护有状态的会话监听。</summary>
public interface IMessageSource : IIslandPlugin
{
    /// <summary>在收到新通知或补全通知元数据时触发，事件可以来自后台线程。</summary>
    /// <remarks>
    /// 宿主负责调度、队列和渲染。一次通知选择本事件或
    /// <see cref="IPluginContext.PublishNotification"/> 其中一个通道提交，避免重复投递。
    /// </remarks>
    event Action<PluginNotification>? NotificationReceived;
}
