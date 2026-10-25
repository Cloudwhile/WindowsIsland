using System.Globalization;
using System.IO.Pipes;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.PluginWorker;

internal static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length != 4 || arguments[0] != "--read-handle" || arguments[2] != "--write-handle"
            || !ValidHandle(arguments[1]) || !ValidHandle(arguments[3]) || arguments[1] == arguments[3]) return 2;
        try
        {
            using var input = new AnonymousPipeClientStream(PipeDirection.In, arguments[1]);
            using var output = new AnonymousPipeClientStream(PipeDirection.Out, arguments[3]);
            using var session = new WorkerSession();
            using var rpc = new IsolationRpc(input, output, session.HandleAsync);
            session.Attach(rpc);
            await rpc.Completion.ConfigureAwait(false);
            return 0;
        }
        catch
        {
            // 插件提供的异常可能包含用户内容或路径，不输出异常文本或原始堆栈。
            return 1;
        }
    }

    private static bool ValidHandle(string value) => ulong.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var handle) && handle is > 0 and <= long.MaxValue;
}
