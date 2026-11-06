$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root ('artifacts/plugin-docs/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$examples = 0
foreach ($page in Get-ChildItem (Join-Path $root 'docs/plugins') -Filter '*.md' -File) {
    $markdown = [IO.File]::ReadAllText($page.FullName)
    $blocks = [regex]::Matches($markdown, '(?ms)^```csharp\r?\n(.*?)^```')
    foreach ($block in $blocks) {
        $examples++
        $code = $block.Groups[1].Value
        if ($code -notmatch 'public (sealed |static )?class ') {
            # 片段只补充周围上下文；被展示的代码逐字参与编译。
            if ($code.TrimStart().StartsWith('public IReadOnlyList<PluginSetting>')) {
                $code = "using WindowsIsland.Contracts; public sealed class DocumentationExample$examples : IPluginSettingsSchema {`n$code`n}"
            } else {
                $pinned = if ($code -match '\bpinned\b' -and $code -notmatch 'var pinned\s*=') { 'var pinned = context.PinnedContent;' } else { '' }
                $code = "using WindowsIsland.Contracts; public static class DocumentationExample$examples { public static void Run(IPluginContext context, byte[] avatarBytes) {`n$pinned`n$code`n} }"
            }
        }
        [IO.File]::WriteAllText((Join-Path $output ($page.BaseName + "-$examples.cs")), $code, $utf8)
    }
    foreach ($include in [regex]::Matches($markdown, '(?m)^<<<\s+([^\r\n]+\.cs)\s*$')) {
        $path = [IO.Path]::GetFullPath((Join-Path $page.DirectoryName $include.Groups[1].Value.Trim()))
        if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Documentation include escapes the repository.' }
        $examples++
        Copy-Item -LiteralPath $path -Destination (Join-Path $output "included-$examples.cs")
    }
}
if ($examples -lt 13) { throw 'Expected all plugin C# blocks and included samples.' }
$contracts = [System.Security.SecurityElement]::Escape((Join-Path $root 'src/WindowsIsland.Contracts/WindowsIsland.Contracts.csproj'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$contracts" /></ItemGroup>
</Project>
"@
$projectPath = Join-Path $output 'PluginDocumentation.csproj'
[IO.File]::WriteAllText($projectPath, $project, $utf8)
& dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin documentation examples failed to compile.' }
$xml = Join-Path $root 'artifacts/bin/WindowsIsland.Contracts/Release/net10.0/WindowsIsland.Contracts.xml'
& (Join-Path $PSScriptRoot 'Update-PluginReference.ps1') -ContractsXml $xml -Check
Write-Output "$examples plugin documentation examples compiled; API reference matches contract XML."
