param(
    [Parameter(Mandatory)][string]$Executable,
    [string[]]$Arguments = @(),
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [Parameter(Mandatory)][string]$LogPath,
    [hashtable]$EnvironmentOverrides = @{}
)

# Capability evidence only: this is not an extractor or a milestone test harness.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$logFullPath = [IO.Path]::GetFullPath($LogPath)
New-Item -ItemType Directory -Force -Path (Split-Path $logFullPath) | Out-Null
[IO.File]::WriteAllText($logFullPath, '')
$started = [DateTimeOffset]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$exitCode = $null
$launchError = $null
$previousEnvironment = @{}
$relevantEnvironment = [ordered]@{}
$locationPushed = $false
try {
    foreach ($name in $EnvironmentOverrides.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $EnvironmentOverrides[$name], 'Process')
    }
    foreach ($name in @('CI','VCToolsVersion','WindowsSDKVersion','VSCMD_VER','VisualStudioVersion','DOTNET_ROOT','DOTNET_ROOT_X64')) {
        $relevantEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    Push-Location -LiteralPath $WorkingDirectory
    $locationPushed = $true
    $global:LASTEXITCODE = $null
    & $Executable @Arguments *> $logFullPath
    $exitCode = $LASTEXITCODE
} catch {
    $launchError = $_.Exception.Message
    $launchError | Out-File -LiteralPath $logFullPath -Append -Encoding utf8
} finally {
    if ($locationPushed) { Pop-Location }
    foreach ($name in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$name]) {
            Remove-Item -LiteralPath ('Env:\' + $name) -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
    $timer.Stop()
}
$result = [ordered]@{
    executable = $Executable
    arguments = $Arguments
    working_directory = $WorkingDirectory
    environment_overrides = $EnvironmentOverrides
    relevant_environment = $relevantEnvironment
    started_utc = $started.ToString('o')
    elapsed_seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
    exit_code = $exitCode
    launch_error = $launchError
    log = $logFullPath
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($logFullPath + '.json') -Encoding utf8
$result | ConvertTo-Json -Compress -Depth 5
Get-Content -LiteralPath $logFullPath -Tail 35
if ($null -eq $exitCode) { exit 127 }
exit $exitCode
