using System.Text.Json;

namespace WindowsIsland.PluginSecurityFixture;

// 只接受显式的私有 canary 根目录，不探测正式产品数据或系统路径。
public sealed record CanaryPlan(string Root, int HostProcessId = 0, int LoopbackPort = 0,
    bool ProbeBroker = false, bool ThrowOnStart = false, long HostWindow = 0)
{
    public const string FileName = "worker-canary.json";
    public const string SettingsKey = "SecurityFixture.Plan";
    public const string ResultKey = "SecurityFixture.Result";
    public const string BrokerUrl = "https://security-fixture.invalid/upload";
    public const string SecretEnvironmentKey = "WINDOWSISLAND_SECURITY_FIXTURE_SECRET";

    public static CanaryPlan? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return null;
        var plan = JsonSerializer.Deserialize<CanaryPlan>(json);
        if (plan is null) return null;
        var root = Path.GetFullPath(plan.Root);
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        const string prefix = "windowsisland-plugin-security-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
            || plan.HostProcessId < 0 || plan.LoopbackPort is < 0 or > 65535)
            throw new InvalidDataException("Invalid canary plan.");
        return plan with { Root = root };
    }

    public static CanaryPlan? FromCodeDirectory()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(CanaryPlan).Assembly.Location)!, FileName);
        return File.Exists(path) ? Read(File.ReadAllText(path)) : null;
    }

    public string OwnData => Path.Combine(Root, "own-data");
    public string HostCanary => Path.Combine(Root, "host-private", "host-canary.txt");
    public string OtherPluginCanary => Path.Combine(Root, "other-plugin", "other-plugin-canary.txt");
    public string OtherPluginCode => Path.Combine(Root, "peer-code", "WindowsIsland.PluginSecurityFixture.dll");
    public string ReceiptCanary => Path.Combine(Root, "host-private", "host-consent-canary.json");
}

public sealed record CanaryResult(bool OwnReadWrite, bool HostReadable, bool HostWritable,
    bool OtherPluginReadable, bool OtherPluginWritable, bool ReceiptReadable, bool ReceiptWritable,
    bool NativeHostReadable, bool HostProcessAccessible, bool DirectHttpSucceeded, bool DirectSocketSucceeded,
    bool BrokerAvailable, bool BrokerSucceeded, int BrokerStatusCode, bool SettingsRoundTrip,
    bool EnvironmentSecretPresent, bool ChildProcessCreated, string? TokenSid,
    int PollCount, int RetryCount, int InteractionCount, bool HostWindowReadable = false, bool OtherPluginCodeReadable = false,
    bool HostAclWritable = false, bool PeerAclWritable = false, bool ReceiptAclWritable = false, bool HostHandlesAccessible = false);
