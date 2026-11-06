param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$probeRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$probeRoot = Join-Path $probeRepo ('artifacts/verification/motion-simulated-allowed/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null

# Only these private source copies allow motion. Production sources and OS preferences are unchanged.
$probeMotion = Get-Content -LiteralPath (Join-Path $probeRepo 'src/WindowsIsland/components/SettingsMotion.cs') -Raw -Encoding utf8
$probeRead = 'SystemSettings?.AnimationsEnabled == true'
if ([regex]::Matches($probeMotion, [regex]::Escape($probeRead)).Count -ne 1) { throw 'The shared motion policy insertion point changed.' }
$probeMotion = $probeMotion.Replace($probeRead, 'true /* isolated simulated system allowance */')
[IO.File]::WriteAllText((Join-Path $probeRoot 'SettingsMotion.cs'), $probeMotion)

$probeChecks = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MotionUiChecks.cs') -Raw -Encoding utf8
$probeExpectation = 'var systemAllows = new UISettings().AnimationsEnabled;'
if (-not $probeChecks.Contains($probeExpectation)) { throw 'The isolated motion expectation insertion point changed.' }
$probeChecks = $probeChecks.Replace($probeExpectation, 'var systemAllows = true; // This copy intentionally simulates system allowance.')
$probeChecks = $probeChecks.Replace('Loaded motion follows the actual system animation preference', 'Copied motion policy follows the simulated system allowance; the real OS preference is unchanged')
[IO.File]::WriteAllText((Join-Path $probeRoot 'MotionUiChecks.cs'), $probeChecks)

$probeApp = Get-Content -LiteralPath (Join-Path $probeRepo 'src/WindowsIsland/App.xaml.cs') -Raw -Encoding utf8
$probeLaunch = 'if (_window is not null) { _window.OpenSettings(); return; }'
if (-not $probeApp.Contains($probeLaunch)) { throw 'The isolated application launch insertion point changed.' }
$probeApp = $probeApp.Replace($probeLaunch, 'await MotionAllowedProbe.RunAsync(this); return;')
[IO.File]::WriteAllText((Join-Path $probeRoot 'App.xaml.cs'), $probeApp)

$probeEntry = @'
using System.Text.Json;
using Windows.UI.ViewManagement;
namespace WindowsIsland;
internal static class MotionAllowedProbe
{
    public static async Task RunAsync(App app)
    {
        var output = AppContext.BaseDirectory;
        var results = new List<string>();
        var before = new UISettings().AnimationsEnabled;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            results.Add("PASS SIMULATED-SYSTEM-ALLOWS " + message);
            File.WriteAllLines(Path.Combine(output, "motion-simulated-allowed.log"), results);
        }
        try
        {
            Check(Microsoft.UI.Xaml.Media.Animation.Timeline.AllowDependentAnimations,
                "The private probe permits real dependent height storyboards");
            await MotionUiChecks.RunAsync(Check);
            var after = new UISettings().AnimationsEnabled;
            Check(before == after, "The actual OS animation preference is unchanged");
            File.WriteAllText(Path.Combine(output, "motion-simulated-allowed.json"), JsonSerializer.Serialize(new
            {
                Scope = "Real WinUI storyboards under a copied policy that simulates system allowance; not an OS-enabled animation test.",
                SimulatedSystemAllowance = true, ActualSystemAnimationsBefore = before, ActualSystemAnimationsAfter = after,
                GlobalSettingsChanged = false, Checks = results.Count
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "motion-simulated-allowed.success"), results.Count.ToString());
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "motion-simulated-allowed.error"), error.ToString());
            Environment.ExitCode = 1;
        }
        finally { app.Exit(); }
    }
}
'@
[IO.File]::WriteAllText((Join-Path $probeRoot 'MotionAllowedProbe.cs'), $probeEntry)
$probeTargets = @"
<Project>
  <ItemGroup Condition="'`$(MSBuildProjectName)' == 'WindowsIsland'">
    <Compile Remove="App.xaml.cs;components/SettingsMotion.cs" />
    <Compile Include="$probeRoot/App.xaml.cs" />
    <Compile Include="$probeRoot/SettingsMotion.cs" />
    <Compile Include="$probeRoot/MotionUiChecks.cs" />
    <Compile Include="$probeRoot/MotionAllowedProbe.cs" />
  </ItemGroup>
</Project>
"@
$probeTargetsPath = Join-Path $probeRoot 'MotionAllowedProbe.targets'
[IO.File]::WriteAllText($probeTargetsPath, $probeTargets)
Push-Location $probeRepo
try {
    dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64 --no-restore "-p:CustomAfterMicrosoftCommonTargets=$probeTargetsPath" "-p:OutDir=$probeRoot/app/" *> (Join-Path $probeRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $probeRoot 'build.log') -Tail 25; throw 'The isolated simulated-motion probe build failed.' }
    if ($BuildOnly) { Write-Output "Simulated-motion probe prepared without running: $probeRoot"; return }
    $probeProcess = Start-Process -FilePath (Join-Path $probeRoot 'app/WindowsIsland.exe') -ArgumentList '--verify-local', '--verify-light' -WindowStyle Hidden -PassThru
    $probeDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not $probeProcess.WaitForExit(250)) {
        if ([DateTime]::UtcNow -gt $probeDeadline) {
            $probeProcess.Kill($true)
            throw "The watchdog stopped only the private simulated-motion probe: $probeRoot"
        }
    }
    $probeFailure = Join-Path $probeRoot 'app/motion-simulated-allowed.error'
    if (Test-Path -LiteralPath $probeFailure) { throw (Get-Content -LiteralPath $probeFailure -Raw -Encoding utf8) }
    if (-not (Test-Path -LiteralPath (Join-Path $probeRoot 'app/motion-simulated-allowed.success'))) { throw "The simulated-motion probe did not finish: $probeRoot" }
    Get-Content -LiteralPath (Join-Path $probeRoot 'app/motion-simulated-allowed.log') -Encoding utf8
    Write-Output "Simulated-system-allowed motion results: $probeRoot"
} finally { Pop-Location }
