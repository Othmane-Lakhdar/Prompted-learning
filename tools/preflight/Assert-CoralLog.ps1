param([Parameter(Mandatory)][string]$LogPath)
$ErrorActionPreference = 'Stop'
# Derived from active [Test] methods at Coral 320c90de, Main.cs:68-232.
# NativeInstanceTest is commented out; StringMarshalTest2 IS an active test.
$expectedManaged = @('SByteMarshalTest','ByteMarshalTest','ShortMarshalTest','UShortMarshalTest',
    'IntMarshalTest','UIntMarshalTest','LongMarshalTest','ULongMarshalTest','FloatMarshalTest',
    'DoubleMarshalTest','BoolMarshalTest','EmptyArrayTest','FloatArrayTest','IntPtrMarshalTest',
    'StringMarshalTest','StringMarshalTest2','DummyStructMarshalTest','DummyStructPtrMarshalTest','TypeMarshalTest')
$run = Get-Content -LiteralPath ($LogPath + '.json') -Raw | ConvertFrom-Json
if ($null -ne $run.launch_error -or $run.exit_code -ne 0) { throw 'Coral process did not exit successfully.' }
$logText = Get-Content -LiteralPath $LogPath -Raw
$logText = $logText -replace '\x1b\[[0-9;]*m', ''
$native = [regex]::Matches($logText, '\[NativeTest\]: Done\. (\d+) passed, (\d+) failed\.')
$managed = [regex]::Matches($logText, '\[Test\]: Done\. (\d+) passed, (\d+) failed\.')
if ($native.Count -ne 1 -or $native[0].Groups[1].Value -ne '41' -or $native[0].Groups[2].Value -ne '0') {
    throw 'Expected exactly one native summary: 41 passed, 0 failed.'
}
if ($managed.Count -ne 2) { throw 'Expected two managed test runs.' }
foreach ($summary in $managed) {
    if ($summary.Groups[1].Value -ne [string]$expectedManaged.Count -or $summary.Groups[2].Value -ne '0') {
        throw 'Expected 19 passed, 0 failed in each managed run.'
    }
}
foreach ($name in $expectedManaged) {
    $pattern = '\[\d+ / 19 \(' + [regex]::Escape($name) + '\)\]: Passed'
    if ([regex]::Matches($logText, $pattern).Count -ne 2) { throw "Expected two passing results for $name" }
}
foreach ($failure in @('Unhandled native exception:', 'Unhandled exception', 'Failed to start tests',
    'NuGet packages seem to be broken', 'Type cache is clashing', 'Failed to initialize',
    'Failed to find', 'Failed to load', '[Coral](Error)', ': Failed')) {
    if ($logText.Contains($failure)) { throw "Coral reported: $failure" }
}
Write-Output 'Coral verified: 41 native passes; 19 managed passes in each of two contexts; no recognized failure markers.'
