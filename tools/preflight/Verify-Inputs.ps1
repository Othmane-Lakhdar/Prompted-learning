param(
    [Parameter(Mandatory)][string]$InputsRoot,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot '../../docs/testing/candidates.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$results = foreach ($candidate in $manifest.candidates) {
    $directory = Join-Path $InputsRoot ($candidate.repository.Split('/')[-1])
    $head = (& git -C $directory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $candidate.commit) { throw "Wrong revision: $directory" }
    & git -C $directory diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw "Tracked checkout differs from its pin: $directory" }
    $tree = @{}
    $treeLines = & git -C $directory ls-tree -r -l HEAD
    if ($LASTEXITCODE -ne 0) { throw "Cannot read Git tree: $directory" }
    foreach ($line in $treeLines) {
        if ($line -match '^\d+ blob ([0-9a-f]+)\s+(\d+)\t(.+)$') {
            $tree[$Matches[3]] = @{ sha = $Matches[1]; bytes = [long]$Matches[2] }
        }
    }
    $byteTotal = 0L
    foreach ($file in $candidate.measurement.files) {
        $actual = $tree[$file.path]
        if (!$actual -or $actual.sha -ne $file.blob_sha -or $actual.bytes -ne $file.bytes) {
            throw "Blob identity/size differs from manifest: $($candidate.repository)/$($file.path)"
        }
        $byteTotal += $actual.bytes
    }
    if ($byteTotal -ne $candidate.measurement.total_bytes -or
        $candidate.measurement.files.Count -ne $candidate.measurement.total_files) {
        throw "Manifest total mismatch: $($candidate.repository)"
    }
    [ordered]@{
        repository = $candidate.repository
        directory = $directory
        commit = $head
        verified_files = $candidate.measurement.files.Count
        verified_git_blob_bytes = $byteTotal
        status = 'passed'
        tracked_worktree_clean = $true
        scope = 'Clean tracked checkout plus every manifest-listed Git blob ID and byte count; not a new review of the selection policy or a raw worktree-byte measurement.'
        untracked_policy = 'Build outputs may be untracked or ignored; generated inputs require separate build provenance.'
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$results | ConvertTo-Json -Depth 5
