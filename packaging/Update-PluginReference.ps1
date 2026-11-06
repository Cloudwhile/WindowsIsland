param([Parameter(Mandatory=$true)][string]$ContractsXml, [switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$document = [IO.File]::ReadAllText($ContractsXml)
function Plain($node) {
    if (!$node) { return '' }
    $value = if ($node -is [string]) { $node } else { $node.InnerText }
    [regex]::Replace($value.Trim(), '\s+', ' ').Replace('|','\|').Replace('<','&lt;').Replace('>','&gt;')
}
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# API 参考')
$lines.Add('')
$lines.Add('本页由随 SDK 分发的 Contracts XML 注释生成，覆盖公开类型、成员、输入字段和返回值。合约大版本为 1；程序集 ABI 为 1.0.0.0。记录类型的字段在对应类型行列出。')
$lines.Add('')
$lines.Add('生命周期方法由宿主调用，返回的 Task 应在本阶段完成时结束。设置、通知和固定内容入口可从后台线程调用；服务可能为 null。停用后旧上下文失效。参数或声明无效会被拒绝，网络还可能因授权、限额、取消和传输失败而失败；IPC 不保证保留远端异常的具体 .NET 类型。参见[错误码](./errors.md)、[设置](./settings.md)、[通知](./notifications.md)、[联网](./permissions.md)和[固定内容示例](./pinned-content.md)。')
$lines.Add('')
$lines.Add('| 成员 | 含义 | 输入、输出与约束 |')
$lines.Add('| --- | --- | --- |')
foreach ($member in $document.doc.members.member) {
    $name = $member.name.Substring(2).Replace('WindowsIsland.Contracts.','').Replace('System.','')
    $details = [Collections.Generic.List[string]]::new()
    foreach ($parameter in @($member.param) + @($member.typeparam)) {
        if ($parameter) { $details.Add($parameter.name + '：' + (Plain $parameter)) }
    }
    if ($member.returns) { $details.Add('返回：' + (Plain $member.returns)) }
    if ($member.remarks) { $details.Add((Plain $member.remarks)) }
    $summary = Plain $member.summary
    if (!$summary) { $summary = '—' }
    $detail = $details -join '<br>'
    if (!$detail) { $detail = '—' }
    $lines.Add('| `' + $name + '` | ' + $summary + ' | ' + $detail + ' |')
}
$content = ($lines -join "`n") + "`n"
$path = Join-Path $root 'docs/plugins/api.md'
if ($Check) {
    if ([IO.File]::ReadAllText($path).Replace("`r`n","`n") -cne $content) { throw 'API reference differs from Contracts XML. Run Update-PluginReference.ps1.' }
} else { [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false)) }
