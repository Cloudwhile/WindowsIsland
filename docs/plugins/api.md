# API 参考

本页由随 SDK 分发的 Contracts XML 注释生成，覆盖公开类型、成员、输入字段和返回值。合约大版本为 1；程序集 ABI 为 1.0.0.0。记录类型的字段在对应类型行列出。

生命周期方法由宿主调用，返回的 Task 应在本阶段完成时结束。设置、通知和固定内容入口可从后台线程调用；服务可能为 null。停用后旧上下文失效。参数或声明无效会被拒绝，网络还可能因授权、限额、取消和传输失败而失败；IPC 不保证保留远端异常的具体 .NET 类型。参见[错误码](./errors.md)、[设置](./settings.md)、[通知](./notifications.md)、[联网](./permissions.md)和[固定内容示例](./pinned-content.md)。

| 成员 | 含义 | 输入、输出与约束 |
| --- | --- | --- |
| `IIslandPlugin` | 定义由宿主管理的插件生命周期。 | 宿主依次初始化、启动和停止插件，最后释放插件；同一实例可以在停止后重新启动。 |
| `IIslandPlugin.Id` | 获取全局唯一且与清单一致的插件标识，格式为 wini_plugin_名称。 | — |
| `IIslandPlugin.DisplayName` | 获取插件的显示名称。 | — |
| `IIslandPlugin.InitializeAsync(IPluginContext)` | 接收当前插件的宿主服务并准备内部状态，不开始监听或推送历史消息。 | context：绑定到当前插件标识和运行代次的宿主上下文，宿主开始停止插件前撤销投递与设置写入能力。<br>返回：初始化完成时结束的任务。 |
| `IIslandPlugin.StartAsync` | 启动插件拥有的后台监听并建立必要的会话基线，启动完成后返回。 | 返回：监听启动完成时结束的任务，而非整个监听循环。<br>持续监听任务由插件保存，重复启动不得创建重复订阅。 |
| `IIslandPlugin.StopAsync` | 停止监听和通知推送，等待后台任务退出并释放本轮订阅；重复停止应安全。 | 返回：本轮监听完全停止时结束的任务。 |
| `IMessageSource` | 定义主动向宿主推送通知的消息来源，允许插件维护有状态的会话监听。 | — |
| `IMessageSource.NotificationReceived` | 在收到新通知或补全通知元数据时触发，事件可以来自后台线程。 | 宿主负责调度、队列和渲染。一次通知选择本事件或 其中一个通道提交，避免重复投递。 |
| `IMessageSourceBackend` | 提供宿主拥有的既有来源服务适配能力，供内置插件经上下文取得。 | 实现及平台资源留在宿主；此接口不构成外部来源发现或加载机制。 |
| `IMessageSourceBackend.NotificationReceived` | 在既有服务收到通知或补全元数据时触发。 | — |
| `IMessageSourceBackend.StartAsync` | 启动此轮服务和监听，建立静默基线。 | 返回：启动完成时结束的任务。 |
| `IPluginPolling` | 提供由宿主现有计时器驱动的可选轮询能力。 | — |
| `IPluginPolling.PollAsync` | 执行一次来源刷新，不创建独立的宿主计时器。 | 返回：本次轮询完成时结束的任务。 |
| `IPluginNotificationFilter` | 提供跨来源的会话通知过滤能力。 | — |
| `IPluginNotificationFilter.Allows(PluginNotification)` | 判断匹配此插件的通知是否满足会话策略，包括其他来源的同一会话。 | notification：待过滤的通知。<br>返回：允许继续路由时为真。 |
| `IPluginRetry` | 提供一次手动连接重试能力。 | — |
| `IPluginRetry.CanRetry` | 获取当前是否可以重试。 | — |
| `IPluginRetry.Retry` | 请求一次立即重试，不重放缺失期间的历史消息。 | — |
| `IPluginContext` | 提供与当前插件绑定的宿主服务，不暴露界面对象。 | — |
| `IPluginContext.HostVersion` | 获取宿主的完整版本号。 | — |
| `IPluginContext.PreviousVersion` | 被本次安装替换的版本；首次安装或没有更新交接信息时为 null。 | — |
| `IPluginContext.PluginId` | 获取此上下文绑定的插件标识，与插件及清单中的标识一致。 | — |
| `IPluginContext.Settings` | 获取已按当前插件标识隔离的设置存储，可从后台线程调用。 | — |
| `IPluginContext.Diagnostics` | 获取当前插件的本机诊断入口，可从后台线程调用。 | — |
| `IPluginContext.GetService``1` | 获取宿主为当前插件提供的可选服务能力，不返回界面对象。 | T：服务的合约接口类型。<br>返回：绑定到当前插件的服务，未提供该能力时为空引用。<br>同一合约大版本的新能力通过可选服务提供，调用方必须容忍 null；不得新增必填接口方法。 |
| `IPluginContext.PinnedContent` | 获取固定内容提交能力；未提供时为空引用。 | — |
| `IPluginContext.PublishNotification(PluginNotification)` | 从任意线程提交通知，由宿主入队并调度到界面线程。 | notification：来源标识与当前插件标识一致的通知数据。<br>此调用不等待渲染；宿主校对来源标识，停止或释放后的延迟通知由宿主丢弃。 |
| `IPluginDesktop` | 通过宿主访问清单声明且经来源验证的桌面客户端；不向插件传递原生句柄。 | — |
| `IPluginDesktop.FindClientsAsync(Threading.CancellationToken)` | 枚举允许的客户端及其模块、窗口。 | — |
| `IPluginDesktop.ReadTreeAsync(Int32,Int64,Threading.CancellationToken)` | 读取指定客户端窗口的有界可访问性快照。 | — |
| `IPluginDesktop.ReadModuleAsync(DesktopRead,Threading.CancellationToken)` | 读取已声明模块文件的一段内容。 | — |
| `IPluginDesktop.ReadMemoryAsync(DesktopRead,Threading.CancellationToken)` | 读取已声明模块在客户端内存中的一段内容。 | — |
| `IPluginDesktop.SetFlagAsync(DesktopFlag,Threading.CancellationToken)` | 核对加载模块锚点后设置单字节布尔标记；宿主在撤销时恢复原值。 | — |
| `IPluginDesktop.ReadImageAsync(DesktopImage,Threading.CancellationToken)` | 读取快照元素提供的本地图像，或捕获该元素内的指定区域。 | — |
| `PluginDesktopScope` | 声明允许访问的进程名、模块名及用途；此声明不自行授予能力。 | Processes：不带扩展名的客户端进程名。<br>Modules：允许读取的模块文件名。<br>Purpose：供使用者核对的访问用途。 |
| `PluginDesktopScope.#ctor(String[],String[],String)` | 声明允许访问的进程名、模块名及用途；此声明不自行授予能力。 | Processes：不带扩展名的客户端进程名。<br>Modules：允许读取的模块文件名。<br>Purpose：供使用者核对的访问用途。 |
| `PluginDesktopScope.Processes` | 不带扩展名的客户端进程名。 | — |
| `PluginDesktopScope.Modules` | 允许读取的模块文件名。 | — |
| `PluginDesktopScope.Purpose` | 供使用者核对的访问用途。 | — |
| `DesktopClient` | 描述宿主核验的客户端。 | ProcessId：客户端进程标识。<br>StartedAt：进程创建时间。<br>Executable：可执行文件路径。<br>Version：客户端文件版本。<br>Modules：允许访问的模块。<br>Windows：属于客户端的窗口。<br>Icon：可用的客户端图标 PNG。 |
| `DesktopClient.#ctor(Int32,Int64,String,String,DesktopModule[],DesktopWindow[],Byte[])` | 描述宿主核验的客户端。 | ProcessId：客户端进程标识。<br>StartedAt：进程创建时间。<br>Executable：可执行文件路径。<br>Version：客户端文件版本。<br>Modules：允许访问的模块。<br>Windows：属于客户端的窗口。<br>Icon：可用的客户端图标 PNG。 |
| `DesktopClient.ProcessId` | 客户端进程标识。 | — |
| `DesktopClient.StartedAt` | 进程创建时间。 | — |
| `DesktopClient.Executable` | 可执行文件路径。 | — |
| `DesktopClient.Version` | 客户端文件版本。 | — |
| `DesktopClient.Modules` | 允许访问的模块。 | — |
| `DesktopClient.Windows` | 属于客户端的窗口。 | — |
| `DesktopClient.Icon` | 可用的客户端图标 PNG。 | — |
| `DesktopModule` | 描述允许访问的模块。 | Name：模块名称。<br>ImageBase：加载地址，仅供验证模块元数据。<br>ImageSize：加载映像大小。<br>FileLength：模块文件大小。 |
| `DesktopModule.#ctor(String,Int64,Int32,Int64)` | 描述允许访问的模块。 | Name：模块名称。<br>ImageBase：加载地址，仅供验证模块元数据。<br>ImageSize：加载映像大小。<br>FileLength：模块文件大小。 |
| `DesktopModule.Name` | 模块名称。 | — |
| `DesktopModule.ImageBase` | 加载地址，仅供验证模块元数据。 | — |
| `DesktopModule.ImageSize` | 加载映像大小。 | — |
| `DesktopModule.FileLength` | 模块文件大小。 | — |
| `DesktopWindow` | 描述客户端窗口，不授予可继承句柄。 | Handle：窗口标识，每次使用时重新核对归属。<br>ClassName：窗口类名。<br>Bounds：屏幕坐标。<br>Style：窗口样式。<br>ExtendedStyle：扩展样式。<br>Owned：是否有所有者窗口。<br>Visible：是否可见。<br>Minimized：是否最小化。<br>Foreground：是否前台窗口。<br>Scale：坐标缩放。 |
| `DesktopWindow.#ctor(Int64,String,DesktopRect,Int64,Int64,Boolean,Boolean,Boolean,Boolean,Double)` | 描述客户端窗口，不授予可继承句柄。 | Handle：窗口标识，每次使用时重新核对归属。<br>ClassName：窗口类名。<br>Bounds：屏幕坐标。<br>Style：窗口样式。<br>ExtendedStyle：扩展样式。<br>Owned：是否有所有者窗口。<br>Visible：是否可见。<br>Minimized：是否最小化。<br>Foreground：是否前台窗口。<br>Scale：坐标缩放。 |
| `DesktopWindow.Handle` | 窗口标识，每次使用时重新核对归属。 | — |
| `DesktopWindow.ClassName` | 窗口类名。 | — |
| `DesktopWindow.Bounds` | 屏幕坐标。 | — |
| `DesktopWindow.Style` | 窗口样式。 | — |
| `DesktopWindow.ExtendedStyle` | 扩展样式。 | — |
| `DesktopWindow.Owned` | 是否有所有者窗口。 | — |
| `DesktopWindow.Visible` | 是否可见。 | — |
| `DesktopWindow.Minimized` | 是否最小化。 | — |
| `DesktopWindow.Foreground` | 是否前台窗口。 | — |
| `DesktopWindow.Scale` | 坐标缩放。 | — |
| `DesktopRect` | 表示屏幕坐标矩形。 | X：横坐标。<br>Y：纵坐标。<br>Width：宽度。<br>Height：高度。 |
| `DesktopRect.#ctor(Double,Double,Double,Double)` | 表示屏幕坐标矩形。 | X：横坐标。<br>Y：纵坐标。<br>Width：宽度。<br>Height：高度。 |
| `DesktopRect.X` | 横坐标。 | — |
| `DesktopRect.Y` | 纵坐标。 | — |
| `DesktopRect.Width` | 宽度。 | — |
| `DesktopRect.Height` | 高度。 | — |
| `DesktopRect.IsEmpty` | 是否为空矩形。 | — |
| `DesktopRect.Right` | 右边界。 | — |
| `DesktopRect.Bottom` | 下边界。 | — |
| `DesktopRect.Contains(DesktopRect)` | 是否完整包含指定矩形。 | — |
| `DesktopNode` | 描述可访问性树节点，Parent 为父节点下标，根节点使用 -1。 | Parent：父节点下标。<br>Name：元素名称。<br>AutomationId：自动化标识。<br>ClassName：元素类名。<br>ControlType：UI Automation 控件类型标识。<br>ItemStatus：项目状态。<br>ItemType：项目类型。<br>RuntimeId：运行时身份。<br>Bounds：屏幕坐标。<br>Offscreen：是否不在可见区域。 |
| `DesktopNode.#ctor(Int32,String,String,String,Int32,String,String,String,DesktopRect,Boolean)` | 描述可访问性树节点，Parent 为父节点下标，根节点使用 -1。 | Parent：父节点下标。<br>Name：元素名称。<br>AutomationId：自动化标识。<br>ClassName：元素类名。<br>ControlType：UI Automation 控件类型标识。<br>ItemStatus：项目状态。<br>ItemType：项目类型。<br>RuntimeId：运行时身份。<br>Bounds：屏幕坐标。<br>Offscreen：是否不在可见区域。 |
| `DesktopNode.Parent` | 父节点下标。 | — |
| `DesktopNode.Name` | 元素名称。 | — |
| `DesktopNode.AutomationId` | 自动化标识。 | — |
| `DesktopNode.ClassName` | 元素类名。 | — |
| `DesktopNode.ControlType` | UI Automation 控件类型标识。 | — |
| `DesktopNode.ItemStatus` | 项目状态。 | — |
| `DesktopNode.ItemType` | 项目类型。 | — |
| `DesktopNode.RuntimeId` | 运行时身份。 | — |
| `DesktopNode.Bounds` | 屏幕坐标。 | — |
| `DesktopNode.Offscreen` | 是否不在可见区域。 | — |
| `DesktopTree` | 描述有时限的可访问性快照。 | Token：仅当前服务实例可使用的快照标识。<br>Nodes：按父节点先于子节点排序的元素。 |
| `DesktopTree.#ctor(String,DesktopNode[])` | 描述有时限的可访问性快照。 | Token：仅当前服务实例可使用的快照标识。<br>Nodes：按父节点先于子节点排序的元素。 |
| `DesktopTree.Token` | 仅当前服务实例可使用的快照标识。 | — |
| `DesktopTree.Nodes` | 按父节点先于子节点排序的元素。 | — |
| `DesktopRead` | 请求读取客户端模块内的有界区间。 | ProcessId：客户端进程标识。<br>Module：清单声明的模块名。<br>Offset：文件偏移或模块内存相对偏移。<br>Count：读取字节数。 |
| `DesktopRead.#ctor(Int32,String,Int64,Int32)` | 请求读取客户端模块内的有界区间。 | ProcessId：客户端进程标识。<br>Module：清单声明的模块名。<br>Offset：文件偏移或模块内存相对偏移。<br>Count：读取字节数。 |
| `DesktopRead.ProcessId` | 客户端进程标识。 | — |
| `DesktopRead.Module` | 清单声明的模块名。 | — |
| `DesktopRead.Offset` | 文件偏移或模块内存相对偏移。 | — |
| `DesktopRead.Count` | 读取字节数。 | — |
| `DesktopAnchor` | 描述设置布尔标记前必须匹配的内存片段。 | Offset：模块内相对偏移。<br>Bytes：必须逐字匹配的内容。 |
| `DesktopAnchor.#ctor(Int64,Byte[])` | 描述设置布尔标记前必须匹配的内存片段。 | Offset：模块内相对偏移。<br>Bytes：必须逐字匹配的内容。 |
| `DesktopAnchor.Offset` | 模块内相对偏移。 | — |
| `DesktopAnchor.Bytes` | 必须逐字匹配的内容。 | — |
| `DesktopFlag` | 请求设置已核验客户端模块中的单字节布尔标记。 | ProcessId：客户端进程标识。<br>Module：模块名。<br>Offset：模块内相对偏移。<br>Value：布尔值 0 或 1。<br>Anchors：写入前核对的模块片段。 |
| `DesktopFlag.#ctor(Int32,String,Int64,Byte,DesktopAnchor[])` | 请求设置已核验客户端模块中的单字节布尔标记。 | ProcessId：客户端进程标识。<br>Module：模块名。<br>Offset：模块内相对偏移。<br>Value：布尔值 0 或 1。<br>Anchors：写入前核对的模块片段。 |
| `DesktopFlag.ProcessId` | 客户端进程标识。 | — |
| `DesktopFlag.Module` | 模块名。 | — |
| `DesktopFlag.Offset` | 模块内相对偏移。 | — |
| `DesktopFlag.Value` | 布尔值 0 或 1。 | — |
| `DesktopFlag.Anchors` | 写入前核对的模块片段。 | — |
| `DesktopImage` | 请求读取快照元素的图像。 | Token：仍有效的快照标识。<br>Node：元素下标。<br>Bounds：可选的元素内部捕获区域；省略时只读取元素提供的本地图像。 |
| `DesktopImage.#ctor(String,Int32,Nullable{DesktopRect})` | 请求读取快照元素的图像。 | Token：仍有效的快照标识。<br>Node：元素下标。<br>Bounds：可选的元素内部捕获区域；省略时只读取元素提供的本地图像。 |
| `DesktopImage.Token` | 仍有效的快照标识。 | — |
| `DesktopImage.Node` | 元素下标。 | — |
| `DesktopImage.Bounds` | 可选的元素内部捕获区域；省略时只读取元素提供的本地图像。 | — |
| `IPluginConversationPolicy` | 提供有界的会话免打扰快照，不接收其他来源的消息正文。 | — |
| `IPluginConversationPolicy.ConversationRules` | 获取当前可确认的会话状态；宿主不会无限期使用此快照。 | — |
| `PluginConversationRule` | 描述客户端会话的免打扰状态。 | AppId：客户端身份。<br>ProcessId：可选的客户端进程标识。<br>Conversation：会话名称。<br>Muted：是否免打扰。 |
| `PluginConversationRule.#ctor(String,Nullable{Int32},String,Boolean)` | 描述客户端会话的免打扰状态。 | AppId：客户端身份。<br>ProcessId：可选的客户端进程标识。<br>Conversation：会话名称。<br>Muted：是否免打扰。 |
| `PluginConversationRule.AppId` | 客户端身份。 | — |
| `PluginConversationRule.ProcessId` | 可选的客户端进程标识。 | — |
| `PluginConversationRule.Conversation` | 会话名称。 | — |
| `PluginConversationRule.Muted` | 是否免打扰。 | — |
| `IPluginDiagnostics` | 定义当前插件的本机诊断写入通道。 | — |
| `IPluginDiagnostics.Write(PluginDiagnostic)` | 提交一条结构化诊断，由宿主管理采集开关、字段校验、合并和保留期限。 | diagnostic：使用固定区域与操作标识的诊断，不包含消息内容或个人信息。 |
| `IPluginLifecycleAware` | 插件可选实现的停止及卸载钩子；钩子异常仅记录诊断，宿主继续清理。 | — |
| `IPluginLifecycleAware.OnStoppingAsync(PluginStopReason)` | 在 StopAsync 前通知停止原因。 | reason：宿主发起此次停止的原因。 |
| `IPluginLifecycleAware.OnUninstallAsync` | 删除代码目录前调用；停用的插件也会收到此钩子，不会因此启动监听。 | — |
| `PluginStopReason` | 宿主停止插件的原因。 | — |
| `PluginStopReason.Disabled` | 用户停用插件。 | — |
| `PluginStopReason.Removed` | 用户卸载插件。 | — |
| `PluginStopReason.Updating` | 以另一个版本替换插件。 | — |
| `PluginStopReason.Shutdown` | 宿主退出。 | — |
| `PluginStopReason.Failed` | 插件异常，宿主隔离并停止该实例。 | — |
| `IPluginNetwork` | 经宿主授权的网络服务；启用时确认开发者或默认插件源提供的联网须知。 | — |
| `IPluginNetwork.SendAsync(PluginNetworkRequest,Threading.CancellationToken)` | 在已确认的联网须知范围内执行 HTTPS 请求。 | request：本次请求的数据和明确用途。<br>cancellationToken：取消本次请求。<br>返回：有大小限制的 HTTP 响应。 |
| `PluginNetworkRequest` | 交由宿主按已授权范围发送的网络请求。 | Url：已披露 origin 下的完整 HTTPS 地址。<br>Method：HTTP 方法。<br>Purpose：本次发送的具体用途。<br>Headers：有限数量的显式请求头；宿主拒绝路由控制字段，不附加宿主凭据。<br>Body：插件自有内容，最大 1 MiB。 |
| `PluginNetworkRequest.#ctor(String,String,String,Collections.Generic.IReadOnlyDictionary{String,String},Byte[])` | 交由宿主按已授权范围发送的网络请求。 | Url：已披露 origin 下的完整 HTTPS 地址。<br>Method：HTTP 方法。<br>Purpose：本次发送的具体用途。<br>Headers：有限数量的显式请求头；宿主拒绝路由控制字段，不附加宿主凭据。<br>Body：插件自有内容，最大 1 MiB。 |
| `PluginNetworkRequest.Url` | 已披露 origin 下的完整 HTTPS 地址。 | — |
| `PluginNetworkRequest.Method` | HTTP 方法。 | — |
| `PluginNetworkRequest.Purpose` | 本次发送的具体用途。 | — |
| `PluginNetworkRequest.Headers` | 有限数量的显式请求头；宿主拒绝路由控制字段，不附加宿主凭据。 | — |
| `PluginNetworkRequest.Body` | 插件自有内容，最大 1 MiB。 | — |
| `PluginNetworkResponse` | 宿主网络服务返回的有界响应。 | StatusCode：HTTP 状态码。<br>Headers：有限数量的响应头。<br>Body：响应正文，最大 2 MiB。 |
| `PluginNetworkResponse.#ctor(Int32,Collections.Generic.IReadOnlyDictionary{String,String},Byte[])` | 宿主网络服务返回的有界响应。 | StatusCode：HTTP 状态码。<br>Headers：有限数量的响应头。<br>Body：响应正文，最大 2 MiB。 |
| `PluginNetworkResponse.StatusCode` | HTTP 状态码。 | — |
| `PluginNetworkResponse.Headers` | 有限数量的响应头。 | — |
| `PluginNetworkResponse.Body` | 响应正文，最大 2 MiB。 | — |
| `PluginNetworkLimits` | 网络服务协议的固定上限。 | — |
| `PluginNetworkLimits.MaximumRequestBytes` | 请求正文的最大字节数。 | — |
| `PluginNetworkLimits.MaximumResponseBytes` | 响应正文的最大字节数。 | — |
| `PluginNetworkLimits.MaximumHeaders` | 请求或响应头的最大数量。 | — |
| `PluginNetworkLimits.MaximumHeaderNameLength` | 单个头名称的最大字符数。 | — |
| `PluginNetworkLimits.MaximumHeaderValueLength` | 单个头值的最大字符数。 | — |
| `PluginNetworkLimits.MaximumUrlLength` | 请求地址的最大字符数。 | — |
| `PluginNetworkLimits.MaximumPurposeLength` | 用途的最大字符数。 | — |
| `PluginNotificationAccess` | 描述系统通知授权结果。 | — |
| `PluginNotificationAccess.NeedsRegistration` | 应用尚未完成系统通知初始化。 | — |
| `PluginNotificationAccess.Waiting` | 尚未授权。 | — |
| `PluginNotificationAccess.Allowed` | 已允许访问。 | — |
| `PluginNotificationAccess.Denied` | 已拒绝访问。 | — |
| `PluginNotificationAccess.Unavailable` | 授权能力不可用。 | — |
| `IPluginNotificationAccess` | 提供可选的系统通知授权能力。 | — |
| `IPluginNotificationAccess.Access` | 获取当前授权结果。 | — |
| `IPluginNotificationAccess.AccessChanged` | 在授权结果变化时触发。 | — |
| `IPluginNotificationAccess.RequestAccessAsync` | 由宿主在界面线程发起系统通知授权。 | 返回：授权请求完成时结束的任务。 |
| `IPluginPermissions` | 查询宿主对当前插件授予的代理能力；操作系统隔离由宿主管理。 | — |
| `IPluginPermissions.IsGranted(String)` | 判断指定能力是否已由宿主授予。 | permission：清单中的能力标识。<br>返回：已经授予时为真。 |
| `PluginPermissions` | 定义插件能力名称；所有能力均由宿主核验并通过受限服务提供。 | 桌面及进程代理只提供给通过指定来源验证并声明范围的适配器，不授予直接系统访问。未知名称不授予。 |
| `PluginPermissions.Notifications` | 向宿主提交通知。 | — |
| `PluginPermissions.Settings` | 提供插件自己的数据目录；插件偏好通过宿主设置服务保存。 | — |
| `PluginPermissions.WeChat` | 保留旧版二进制兼容性的历史标签，当前宿主不再授予该能力。 | — |
| `PluginPermissions.Process` | 使用经来源核验的客户端模块代理，并为通知指定经宿主核对的点击目标。 | — |
| `PluginPermissions.Network` | 使用启用时确认联网须知的 IPluginNetwork 服务，不授予直接联网。 | — |
| `PluginPermissions.FileSystem` | 声明插件数据目录之外的文件访问需求。 | — |
| `PluginPermissions.Native` | 声明原生代码访问需求。 | — |
| `PluginPermissions.Automation` | 使用经来源及范围核验的 IPluginDesktop 服务，不授予直接桌面访问。 | — |
| `PluginPermissions.IsKnown(String)` | 判断能力名称是否由当前合约定义。 | permission：能力名称。<br>返回：属于已定义能力时为真。 |
| `PluginPermissions.IsHighRisk(String)` | 判断已知能力是否需要单独授权。 | permission：能力名称。<br>返回：属于高危能力时为真。 |
| `IPluginSettings` | 定义按插件标识隔离、支持并发调用的字符串和布尔设置存储。 | 宿主绑定存储范围，插件只传入范围内的键，不能选择其他插件的存储。 所有读写的键必须非空白且最多 128 字符，否则抛出 ArgumentException。 |
| `IPluginSettings.GetString(String,String)` | 读取字符串设置；键不存在或类型无效时返回指定默认值。 | key：当前插件范围内的设置键。<br>defaultValue：缺少有效值时返回的默认值。<br>返回：保存的字符串或默认值。 |
| `IPluginSettings.GetBoolean(String,Boolean)` | 读取布尔设置；键不存在或类型无效时返回指定默认值。 | key：当前插件范围内的设置键。<br>defaultValue：缺少有效值时返回的默认值。<br>返回：保存的布尔值或默认值。 |
| `IPluginSettings.SetString(String,String)` | 保存字符串设置，传入空引用时删除对应键。 | key：当前插件范围内的设置键。<br>value：要保存的字符串，或表示删除的空引用。 |
| `IPluginSettings.SetBoolean(String,Boolean)` | 保存布尔设置。 | key：当前插件范围内的设置键。<br>value：要保存的布尔值。 |
| `IPluginSettingsChanged` | 可选的设置变更回调。宿主按插件串行调度，停止后不再调用。 | — |
| `IPluginSettingsChanged.OnSettingsChangedAsync(Collections.Generic.IReadOnlyList{String})` | 设置已成功持久化；仅传入变化的键，不传敏感值。重新读取 context.Settings 获取当前值。 | keys：本插件范围内已变化的键；连续变更可能合并。<br>返回：变更处理任务，不应等待永久后台循环。 |
| `IPluginSettingsSchema` | 插件可选实现的设置声明，不依赖运行状态，不使用自定义 XAML。 | 属性可在 InitializeAsync 之前读取。默认值和业务校验由插件提供；宿主只校验声明形状。 |
| `IPluginSettingsSchema.SettingsSchema` | 获取动态 string/bool 设置项。扩展类型通过包内静态设置声明提供。 | — |
| `PluginSettingType` | 与 IPluginSettings 对应的设置类型。 | — |
| `PluginSettingType.String` | 字符串，对应 GetString/SetString。 | — |
| `PluginSettingType.Boolean` | 布尔值，对应 GetBoolean/SetBoolean。 | — |
| `PluginSettingType.Integer` | 整数，以不含分隔符的十进制字符串读写。 | — |
| `PluginSettingType.Choice` | 有限选项，以选项字符串读写。 | — |
| `PluginSettingType.Url` | 不含用户凭据的绝对 HTTPS URL。 | — |
| `PluginSettingType.Password` | 宿主加密保存、界面遮蔽的字符串。 | — |
| `PluginSetting` | 声明设置键（1–128 字符）、类型、标题和相应类型的默认值。 | Key：插件命名空间内的设置键。<br>Type：设置类型；扩展类型用于静态设置声明。<br>Title：插件提供的显示标题。<br>DefaultValue：字符串（允许 null）或布尔默认值；整数以十进制字符串表示；密码无默认值。 |
| `PluginSetting.#ctor(String,PluginSettingType,String,Object)` | 声明设置键（1–128 字符）、类型、标题和相应类型的默认值。 | Key：插件命名空间内的设置键。<br>Type：设置类型；扩展类型用于静态设置声明。<br>Title：插件提供的显示标题。<br>DefaultValue：字符串（允许 null）或布尔默认值；整数以十进制字符串表示；密码无默认值。 |
| `PluginSetting.Key` | 插件命名空间内的设置键。 | — |
| `PluginSetting.Type` | 设置类型；扩展类型用于静态设置声明。 | — |
| `PluginSetting.Title` | 插件提供的显示标题。 | — |
| `PluginSetting.DefaultValue` | 字符串（允许 null）或布尔默认值；整数以十进制字符串表示；密码无默认值。 | — |
| `PluginSetting.Required` | 是否必须提供非空值。 | — |
| `PluginSetting.Minimum` | 整数下限（包含）。 | — |
| `PluginSetting.Maximum` | 整数上限（包含）。 | — |
| `PluginSetting.Choices` | 枚举允许值，按大小写精确匹配。 | — |
| `IPluginStorage` | 提供与代码目录分离的数据目录，卸载代码时保留。 | — |
| `IPluginStorage.DataDirectory` | 获取当前插件的数据目录。 | — |
| `IWeChatHost` | 保留旧版二进制兼容性的历史接口；当前宿主不再提供此服务。 | 微信业务由独立插件实现，桌面访问改用可选的 IPluginDesktop 服务。 |
| `IWeChatHost.StartAsync` | 启动监听并建立静默基线，重复调用不重建监听。 | — |
| `IWeChatHost.PollAsync` | 刷新连接状态和弹窗监听。 | — |
| `IWeChatHost.RetryAsync` | 请求一次连接重试。 | — |
| `WeChatHostStatus` | 描述微信监听的连接结果和可重试状态。 | Status：连接结果。<br>CanRetry：当前是否允许重试。 |
| `WeChatHostStatus.#ctor(PluginStatus,Boolean)` | 描述微信监听的连接结果和可重试状态。 | Status：连接结果。<br>CanRetry：当前是否允许重试。 |
| `WeChatHostStatus.Status` | 连接结果。 | — |
| `WeChatHostStatus.CanRetry` | 当前是否允许重试。 | — |
| `PinnedContent` | 描述由宿主调度的单个固定内容，不包含窗口或界面对象。 | Id：插件内稳定且非空的内容标识。<br>PluginId：提交内容的插件标识，必须与上下文一致。<br>Title：供辅助功能和提示使用的标题。<br>CompactText：紧凑胶囊显示的当前值。<br>Body：可选的内容说明。<br>ActionId：可选的插件点击动作标识，不作为地址或命令执行。<br>Symbol：可选的图标名称，宿主不识别时使用默认图标。<br>Controls：可选的交互控件快照，由宿主创建原生控件。<br>CustomPanelXaml：保留给宿主内置来源的自定义面板；所有外部插件均忽略此字段，使用结构化控件。 |
| `PinnedContent.#ctor(String,String,String,String,String,String,String,Collections.Generic.IReadOnlyList{PinnedControl},String)` | 描述由宿主调度的单个固定内容，不包含窗口或界面对象。 | Id：插件内稳定且非空的内容标识。<br>PluginId：提交内容的插件标识，必须与上下文一致。<br>Title：供辅助功能和提示使用的标题。<br>CompactText：紧凑胶囊显示的当前值。<br>Body：可选的内容说明。<br>ActionId：可选的插件点击动作标识，不作为地址或命令执行。<br>Symbol：可选的图标名称，宿主不识别时使用默认图标。<br>Controls：可选的交互控件快照，由宿主创建原生控件。<br>CustomPanelXaml：保留给宿主内置来源的自定义面板；所有外部插件均忽略此字段，使用结构化控件。 |
| `PinnedContent.Id` | 插件内稳定且非空的内容标识。 | — |
| `PinnedContent.PluginId` | 提交内容的插件标识，必须与上下文一致。 | — |
| `PinnedContent.Title` | 供辅助功能和提示使用的标题。 | — |
| `PinnedContent.CompactText` | 紧凑胶囊显示的当前值。 | — |
| `PinnedContent.Body` | 可选的内容说明。 | — |
| `PinnedContent.ActionId` | 可选的插件点击动作标识，不作为地址或命令执行。 | — |
| `PinnedContent.Symbol` | 可选的图标名称，宿主不识别时使用默认图标。 | — |
| `PinnedContent.Controls` | 可选的交互控件快照，由宿主创建原生控件。 | — |
| `PinnedContent.CustomPanelXaml` | 保留给宿主内置来源的自定义面板；所有外部插件均忽略此字段，使用结构化控件。 | — |
| `IPinnedContentSource` | 提供绑定到当前插件的固定内容提交能力，可从后台线程调用。 | — |
| `IPinnedContentSource.Submit(PinnedContent)` | 提交或替换当前插件的固定内容；全局只呈现最近提交的一项。 | content：来源标识与当前上下文一致的内容。 |
| `IPinnedContentSource.Update(PinnedContent)` | 更新已提交的同标识内容，不恢复已撤下或被其他内容替换的项目。 | content：需要更新的完整内容快照。 |
| `IPinnedContentSource.Remove(String)` | 撤下当前插件指定标识的固定内容。 | id：插件内的内容标识。 |
| `IPinnedContentSource.SetInteractionHandler(Func{PinnedControlInteraction,Threading.Tasks.Task})` | 注册当前插件的交互处理器；宿主在后台串行回调，撤销上下文后不再调用。 | handler：接收当前内容交互的异步处理器，空引用表示取消注册。 |
| `PinnedControlKind` | 标识宿主支持的固定内容交互控件。 | — |
| `PinnedControlKind.Button` | 触发一次操作的按钮。 | — |
| `PinnedControlKind.Toggle` | 切换布尔值的开关。 | — |
| `PinnedControlKind.Slider` | 按步长调整数值的滑块。 | — |
| `PinnedControl` | 描述一个由宿主渲染的交互控件，未使用的字段保留默认值。 | Id：当前内容内唯一的控件标识。<br>Label：文案及辅助功能名称。<br>Kind：控件类型。<br>IsEnabled：是否允许交互。<br>IsOn：开关当前值。<br>Minimum：滑块最小值。<br>Maximum：滑块最大值。<br>Value：滑块当前值。<br>Step：滑块步长，必须为正数。<br>Symbol：可选按钮图标名称，未提供时使用文案。 |
| `PinnedControl.#ctor(String,String,PinnedControlKind,Boolean,Boolean,Double,Double,Double,Double,String)` | 描述一个由宿主渲染的交互控件，未使用的字段保留默认值。 | Id：当前内容内唯一的控件标识。<br>Label：文案及辅助功能名称。<br>Kind：控件类型。<br>IsEnabled：是否允许交互。<br>IsOn：开关当前值。<br>Minimum：滑块最小值。<br>Maximum：滑块最大值。<br>Value：滑块当前值。<br>Step：滑块步长，必须为正数。<br>Symbol：可选按钮图标名称，未提供时使用文案。 |
| `PinnedControl.Id` | 当前内容内唯一的控件标识。 | — |
| `PinnedControl.Label` | 文案及辅助功能名称。 | — |
| `PinnedControl.Kind` | 控件类型。 | — |
| `PinnedControl.IsEnabled` | 是否允许交互。 | — |
| `PinnedControl.IsOn` | 开关当前值。 | — |
| `PinnedControl.Minimum` | 滑块最小值。 | — |
| `PinnedControl.Maximum` | 滑块最大值。 | — |
| `PinnedControl.Value` | 滑块当前值。 | — |
| `PinnedControl.Step` | 滑块步长，必须为正数。 | — |
| `PinnedControl.Symbol` | 可选按钮图标名称，未提供时使用文案。 | — |
| `PinnedControlInteraction` | 描述经宿主校验并交还给插件的一次交互。 | ContentId：当前固定内容标识。<br>ControlId：控件标识。<br>IsOn：开关提交的值，其他控件为空引用。<br>Value：滑块提交的值，其他控件为空引用。 |
| `PinnedControlInteraction.#ctor(String,String,Nullable{Boolean},Nullable{Double})` | 描述经宿主校验并交还给插件的一次交互。 | ContentId：当前固定内容标识。<br>ControlId：控件标识。<br>IsOn：开关提交的值，其他控件为空引用。<br>Value：滑块提交的值，其他控件为空引用。 |
| `PinnedControlInteraction.ContentId` | 当前固定内容标识。 | — |
| `PinnedControlInteraction.ControlId` | 控件标识。 | — |
| `PinnedControlInteraction.IsOn` | 开关提交的值，其他控件为空引用。 | — |
| `PinnedControlInteraction.Value` | 滑块提交的值，其他控件为空引用。 | — |
| `PluginContract` | 提供插件合约的大版本及兼容性判断。 | 仅相同大版本兼容。同一大版本禁止新增必填接口方法，新能力通过 IPluginContext.GetService 返回可选服务。 发布新大版本必须同时提供迁移说明。 |
| `PluginContract.Version` | 当前合约的大版本。 | — |
| `PluginContract.IsCompatible(Int32,Int32)` | 判断插件要求的合约大版本是否与宿主支持的大版本相同，非正整数版本无效。 | pluginContractVersion：插件清单声明的合约大版本。<br>hostContractVersion：宿主支持的合约大版本，默认使用当前合约版本。<br>返回：两个版本均有效且相等时为真，否则为假。 |
| `PluginDiagnostic` | 描述一次插件诊断，由宿主补充时间、应用版本和错误编号。 | Area：固定的模块标识。<br>Operation：固定的操作标识。<br>IsFatal：是否为致命错误。<br>AdditionalInformation：可选的结构化错误元数据。 |
| `PluginDiagnostic.#ctor(String,String,Boolean,PluginDiagnosticInfo)` | 描述一次插件诊断，由宿主补充时间、应用版本和错误编号。 | Area：固定的模块标识。<br>Operation：固定的操作标识。<br>IsFatal：是否为致命错误。<br>AdditionalInformation：可选的结构化错误元数据。 |
| `PluginDiagnostic.Area` | 固定的模块标识。 | — |
| `PluginDiagnostic.Operation` | 固定的操作标识。 | — |
| `PluginDiagnostic.IsFatal` | 是否为致命错误。 | — |
| `PluginDiagnostic.AdditionalInformation` | 可选的结构化错误元数据。 | — |
| `PluginDiagnosticInfo` | 描述诊断允许携带的附加信息，不包含异常消息、原始堆栈或自由文本。 | ExceptionType：异常类型的完整名称。<br>HResult：异常的数值错误代码。<br>ClientVersion：第三方客户端文件版本，仅包含数字和点。 |
| `PluginDiagnosticInfo.#ctor(String,Nullable{Int32},String)` | 描述诊断允许携带的附加信息，不包含异常消息、原始堆栈或自由文本。 | ExceptionType：异常类型的完整名称。<br>HResult：异常的数值错误代码。<br>ClientVersion：第三方客户端文件版本，仅包含数字和点。 |
| `PluginDiagnosticInfo.ExceptionType` | 异常类型的完整名称。 | — |
| `PluginDiagnosticInfo.HResult` | 异常的数值错误代码。 | — |
| `PluginDiagnosticInfo.ClientVersion` | 第三方客户端文件版本，仅包含数字和点。 | — |
| `PluginId` | 定义插件标识的统一命名规则：固定前缀 wini_plugin_ 后接插件名称。 | — |
| `PluginId.Prefix` | 获取插件标识的固定前缀。 | — |
| `PluginId.IsValid(String)` | 判断标识是否符合 wini_plugin_名称 格式。 | id：待检查的插件标识。<br>返回：名称以小写英文字母开头，且仅包含小写英文字母、数字及分隔用下划线时为真。 |
| `PluginManifest` | 描述可由 System.Text.Json 读写的插件清单，不负责加载程序集或授予权限。 | Id：插件全局唯一标识，格式为 wini_plugin_名称。<br>Name：插件显示名称。<br>Version：插件自身的版本字符串。<br>EntryAssembly：相对于插件目录的入口程序集文件名。<br>EntryType：实现插件生命周期接口的入口类型完整名称。<br>ContractVersion：插件要求的合约大版本。<br>Permissions：声明的权限标识数组，不需要权限时使用空数组。<br>Author：可选的作者名称，最多 200 字符。<br>Description：可选的插件说明，最多 8000 字符。<br>DeveloperId：开发者标识，默认为空。<br>DeveloperName：可选的开发者名称。<br>RepositoryUrl：可选的公开 GitHub 仓库地址。<br>SourceKind：来源类型，默认为手动安装。<br>Terms：可选的包内纯文本条款及修订标识。<br>Network：声明网络能力时必需的目的地、用途和数据类别。 |
| `PluginManifest.#ctor(String,String,String,String,String,Int32,String[],String,String,String,String,String,PluginSourceKind,PluginTerms,PluginNetworkDisclosure)` | 描述可由 System.Text.Json 读写的插件清单，不负责加载程序集或授予权限。 | Id：插件全局唯一标识，格式为 wini_plugin_名称。<br>Name：插件显示名称。<br>Version：插件自身的版本字符串。<br>EntryAssembly：相对于插件目录的入口程序集文件名。<br>EntryType：实现插件生命周期接口的入口类型完整名称。<br>ContractVersion：插件要求的合约大版本。<br>Permissions：声明的权限标识数组，不需要权限时使用空数组。<br>Author：可选的作者名称，最多 200 字符。<br>Description：可选的插件说明，最多 8000 字符。<br>DeveloperId：开发者标识，默认为空。<br>DeveloperName：可选的开发者名称。<br>RepositoryUrl：可选的公开 GitHub 仓库地址。<br>SourceKind：来源类型，默认为手动安装。<br>Terms：可选的包内纯文本条款及修订标识。<br>Network：声明网络能力时必需的目的地、用途和数据类别。 |
| `PluginManifest.Id` | 插件全局唯一标识，格式为 wini_plugin_名称。 | — |
| `PluginManifest.Name` | 插件显示名称。 | — |
| `PluginManifest.Version` | 插件自身的版本字符串。 | — |
| `PluginManifest.EntryAssembly` | 相对于插件目录的入口程序集文件名。 | — |
| `PluginManifest.EntryType` | 实现插件生命周期接口的入口类型完整名称。 | — |
| `PluginManifest.ContractVersion` | 插件要求的合约大版本。 | — |
| `PluginManifest.Permissions` | 声明的权限标识数组，不需要权限时使用空数组。 | — |
| `PluginManifest.Author` | 可选的作者名称，最多 200 字符。 | — |
| `PluginManifest.Description` | 可选的插件说明，最多 8000 字符。 | — |
| `PluginManifest.DeveloperId` | 开发者标识，默认为空。 | — |
| `PluginManifest.DeveloperName` | 可选的开发者名称。 | — |
| `PluginManifest.RepositoryUrl` | 可选的公开 GitHub 仓库地址。 | — |
| `PluginManifest.SourceKind` | 来源类型，默认为手动安装。 | — |
| `PluginManifest.Terms` | 可选的包内纯文本条款及修订标识。 | — |
| `PluginManifest.Network` | 声明网络能力时必需的目的地、用途和数据类别。 | — |
| `PluginManifest.MinimumHostVersion` | 可选的最低宿主 SemVer 版本；省略时仅检查合约大版本。 | — |
| `PluginManifest.Desktop` | 可选的桌面客户端访问范围；只有通过来源及能力校验后才提供服务。 | — |
| `PluginNotification` | 描述插件提交的通知数据，不包含界面控件或进程对象。 | SourceId：产生此通知的插件全局唯一标识。<br>ConversationName：会话名称或通知标题。<br>Sender：发送者名称，无独立发送者时为空引用。<br>Body：通知正文。<br>CreatedAt：通知发生时间及其时区偏移。<br>AppIcon：应用图标的编码图像字节，缺少时由宿主提供回退。<br>SenderAvatar：会话或发送者头像的编码图像字节。<br>DeduplicationKey：来源内稳定的事件去重键；元数据补全沿用原键。<br>OriginProcessId：点击通知时应激活的目标进程编号。<br>AppName：第三方应用的显示名称。<br>AppId：第三方应用的稳定标识，供宿主匹配不同来源的同一应用。<br>Id：来源内的通知编号。<br>Symbol：无图像时可选的符号字形文本，由宿主解释和呈现。<br>IsMetadataUpdate：是否仅补全已有通知的图像等元数据，不代表新消息。<br>Kind：通知的传递途径，用于保留既有管线的合并优先级。<br>提交后插件不再修改图标和头像字节数组；图像解码、去重和点击处理由宿主执行。 |
| `PluginNotification.#ctor(String,String,String,String,DateTimeOffset,Byte[],Byte[],String,Nullable{Int32},String,String,UInt32,String,Boolean,PluginNotificationKind)` | 描述插件提交的通知数据，不包含界面控件或进程对象。 | SourceId：产生此通知的插件全局唯一标识。<br>ConversationName：会话名称或通知标题。<br>Sender：发送者名称，无独立发送者时为空引用。<br>Body：通知正文。<br>CreatedAt：通知发生时间及其时区偏移。<br>AppIcon：应用图标的编码图像字节，缺少时由宿主提供回退。<br>SenderAvatar：会话或发送者头像的编码图像字节。<br>DeduplicationKey：来源内稳定的事件去重键；元数据补全沿用原键。<br>OriginProcessId：点击通知时应激活的目标进程编号。<br>AppName：第三方应用的显示名称。<br>AppId：第三方应用的稳定标识，供宿主匹配不同来源的同一应用。<br>Id：来源内的通知编号。<br>Symbol：无图像时可选的符号字形文本，由宿主解释和呈现。<br>IsMetadataUpdate：是否仅补全已有通知的图像等元数据，不代表新消息。<br>Kind：通知的传递途径，用于保留既有管线的合并优先级。<br>提交后插件不再修改图标和头像字节数组；图像解码、去重和点击处理由宿主执行。 |
| `PluginNotification.SourceId` | 产生此通知的插件全局唯一标识。 | — |
| `PluginNotification.ConversationName` | 会话名称或通知标题。 | — |
| `PluginNotification.Sender` | 发送者名称，无独立发送者时为空引用。 | — |
| `PluginNotification.Body` | 通知正文。 | — |
| `PluginNotification.CreatedAt` | 通知发生时间及其时区偏移。 | — |
| `PluginNotification.AppIcon` | 应用图标的编码图像字节，缺少时由宿主提供回退。 | — |
| `PluginNotification.SenderAvatar` | 会话或发送者头像的编码图像字节。 | — |
| `PluginNotification.DeduplicationKey` | 来源内稳定的事件去重键；元数据补全沿用原键。 | — |
| `PluginNotification.OriginProcessId` | 点击通知时应激活的目标进程编号。 | — |
| `PluginNotification.AppName` | 第三方应用的显示名称。 | — |
| `PluginNotification.AppId` | 第三方应用的稳定标识，供宿主匹配不同来源的同一应用。 | — |
| `PluginNotification.Id` | 来源内的通知编号。 | — |
| `PluginNotification.Symbol` | 无图像时可选的符号字形文本，由宿主解释和呈现。 | — |
| `PluginNotification.IsMetadataUpdate` | 是否仅补全已有通知的图像等元数据，不代表新消息。 | — |
| `PluginNotification.Kind` | 通知的传递途径，用于保留既有管线的合并优先级。 | — |
| `PluginNotificationKind` | 保留既有通知管线区分传递途径的语义。 | — |
| `PluginNotificationKind.SystemNotification` | 系统通知，包括通过系统通知接入的其他应用。 | — |
| `PluginNotificationKind.ClientHook` | 客户端原生桥接或弹窗回退。 | — |
| `PluginNotificationKind.Power` | 系统电源事件。 | — |
| `PluginNotificationKind.ClientAutomation` | 客户端辅助访问会话监听。 | — |
| `PluginTerms` | 插件包内的纯文本使用条款；宿主在启用前完整展示并记录同意版本。 | Revision：条款修订标识。<br>Text：UTF-8 纯文本正文，不是远程页面地址。 |
| `PluginTerms.#ctor(String,String)` | 插件包内的纯文本使用条款；宿主在启用前完整展示并记录同意版本。 | Revision：条款修订标识。<br>Text：UTF-8 纯文本正文，不是远程页面地址。 |
| `PluginTerms.Revision` | 条款修订标识。 | — |
| `PluginTerms.Text` | UTF-8 纯文本正文，不是远程页面地址。 | — |
| `PluginNetworkDisclosure` | 插件通过宿主网络服务发送自有内容时的用途与范围披露。 | Origins：允许请求的 HTTPS origin，不包含路径、查询或凭据。<br>Purpose：网络使用目的。<br>DataCategories：可能发送的数据类别。<br>Notice：开发者或默认插件源提供的完整纯文本联网须知。 |
| `PluginNetworkDisclosure.#ctor(String[],String,String[],String)` | 插件通过宿主网络服务发送自有内容时的用途与范围披露。 | Origins：允许请求的 HTTPS origin，不包含路径、查询或凭据。<br>Purpose：网络使用目的。<br>DataCategories：可能发送的数据类别。<br>Notice：开发者或默认插件源提供的完整纯文本联网须知。 |
| `PluginNetworkDisclosure.Origins` | 允许请求的 HTTPS origin，不包含路径、查询或凭据。 | — |
| `PluginNetworkDisclosure.Purpose` | 网络使用目的。 | — |
| `PluginNetworkDisclosure.DataCategories` | 可能发送的数据类别。 | — |
| `PluginNetworkDisclosure.Notice` | 开发者或默认插件源提供的完整纯文本联网须知。 | — |
| `PluginSource` | 描述官方目录中按开发者区分的已收录来源。 | DeveloperId：严格的小写开发者标识。<br>DeveloperName：开发者显示名称。<br>RepositoryUrl：公开的 GitHub 仓库地址。<br>IndexUrl：该开发者已收录插件列表的绝对地址。<br>LastSync：宿主保存的最近成功同步时间。 |
| `PluginSource.#ctor(String,String,String,String,Nullable{DateTimeOffset})` | 描述官方目录中按开发者区分的已收录来源。 | DeveloperId：严格的小写开发者标识。<br>DeveloperName：开发者显示名称。<br>RepositoryUrl：公开的 GitHub 仓库地址。<br>IndexUrl：该开发者已收录插件列表的绝对地址。<br>LastSync：宿主保存的最近成功同步时间。 |
| `PluginSource.DeveloperId` | 严格的小写开发者标识。 | — |
| `PluginSource.DeveloperName` | 开发者显示名称。 | — |
| `PluginSource.RepositoryUrl` | 公开的 GitHub 仓库地址。 | — |
| `PluginSource.IndexUrl` | 该开发者已收录插件列表的绝对地址。 | — |
| `PluginSource.LastSync` | 宿主保存的最近成功同步时间。 | — |
| `PluginCatalogEntry` | 描述官方目录中的一个已收录插件版本，不包含审核过程状态。 | Id：插件标识。<br>Name：显示名称。<br>Version：完整语义版本。<br>DownloadUrl：公开 GitHub 仓库的 ZIP 发布附件地址。<br>Sha256：附件的 SHA-256 十六进制摘要。<br>MinHostVersion：最低宿主语义版本。<br>ContractVersion：合约大版本。<br>Permissions：版本声明的宿主能力。<br>Description：插件说明。<br>RepositoryUrl：公开的 GitHub 仓库地址。<br>DeveloperId：所属开发者标识。<br>Network：默认插件源提供的联网须知，作为包内声明缺失时的补充。 |
| `PluginCatalogEntry.#ctor(String,String,String,String,String,String,Int32,String[],String,String,String,PluginNetworkDisclosure)` | 描述官方目录中的一个已收录插件版本，不包含审核过程状态。 | Id：插件标识。<br>Name：显示名称。<br>Version：完整语义版本。<br>DownloadUrl：公开 GitHub 仓库的 ZIP 发布附件地址。<br>Sha256：附件的 SHA-256 十六进制摘要。<br>MinHostVersion：最低宿主语义版本。<br>ContractVersion：合约大版本。<br>Permissions：版本声明的宿主能力。<br>Description：插件说明。<br>RepositoryUrl：公开的 GitHub 仓库地址。<br>DeveloperId：所属开发者标识。<br>Network：默认插件源提供的联网须知，作为包内声明缺失时的补充。 |
| `PluginCatalogEntry.Id` | 插件标识。 | — |
| `PluginCatalogEntry.Name` | 显示名称。 | — |
| `PluginCatalogEntry.Version` | 完整语义版本。 | — |
| `PluginCatalogEntry.DownloadUrl` | 公开 GitHub 仓库的 ZIP 发布附件地址。 | — |
| `PluginCatalogEntry.Sha256` | 附件的 SHA-256 十六进制摘要。 | — |
| `PluginCatalogEntry.MinHostVersion` | 最低宿主语义版本。 | — |
| `PluginCatalogEntry.ContractVersion` | 合约大版本。 | — |
| `PluginCatalogEntry.Permissions` | 版本声明的宿主能力。 | — |
| `PluginCatalogEntry.Description` | 插件说明。 | — |
| `PluginCatalogEntry.RepositoryUrl` | 公开的 GitHub 仓库地址。 | — |
| `PluginCatalogEntry.DeveloperId` | 所属开发者标识。 | — |
| `PluginCatalogEntry.Network` | 默认插件源提供的联网须知，作为包内声明缺失时的补充。 | — |
| `PluginSourceKind` | 表示插件的安装来源，由宿主根据实际安装槽确定。 | — |
| `PluginSourceKind.BuiltIn` | 随宿主提供的内置插件。 | — |
| `PluginSourceKind.Official` | 从默认插件源安装的插件。 | — |
| `PluginSourceKind.Manual` | 用户手动安装的插件。 | — |
| `PluginConnectionState` | 描述消息来源的连接结果，不包含连接过程状态。 | — |
| `PluginConnectionState.Connected` | 已连接或已开启。 | — |
| `PluginConnectionState.Disconnected` | 未连接或连接失败，原因由状态附带。 | — |
| `PluginConnectionState.NotRunning` | 消息来源客户端未运行。 | — |
| `PluginStatusReason` | 描述连接结果的固定原因，由宿主按当前语言呈现。 | — |
| `PluginStatusReason.None` | 没有附加原因。 | — |
| `PluginStatusReason.ClientNotDetected` | 未检测到客户端。 | — |
| `PluginStatusReason.PermissionRequired` | 需要系统通知授权。 | — |
| `PluginStatusReason.PermissionDenied` | 系统通知授权被拒绝。 | — |
| `PluginStatusReason.RegistrationRequired` | 系统通知初始化未完成。 | — |
| `PluginStatusReason.Unavailable` | 来源不可用。 | — |
| `PluginStatusReason.VersionNotSupported` | 当前客户端版本不支持直连。 | — |
| `PluginStatusReason.ConnectionFailed` | 连接客户端失败。 | — |
| `PluginStatus` | 描述来源的连接结果和原因。 | State：当前连接结果。<br>Reason：可选的固定原因。 |
| `PluginStatus.#ctor(PluginConnectionState,PluginStatusReason)` | 描述来源的连接结果和原因。 | State：当前连接结果。<br>Reason：可选的固定原因。 |
| `PluginStatus.State` | 当前连接结果。 | — |
| `PluginStatus.Reason` | 可选的固定原因。 | — |
| `IPluginStatusProvider` | 提供消息来源的当前结果状态。 | — |
| `IPluginStatusProvider.Status` | 获取可从宿主读取的连接结果。 | — |
