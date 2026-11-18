# Phase 4: run the converter on every input and corpus document, then run it again on each output
# (idempotence), then reconcile counts. Usage: run_phase4.ps1 -Label official [-MinScore 0.95]
param([string]$Label = 'official', [string]$MinScore = '')
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$exe = "$root\bin\MathMorph.exe"
$extra = @('--force')
if ($MinScore -ne '') { $extra += @('--min-score', $MinScore) }
if ($Label -eq 'official') { $realOut = "$root\output" } else { $realOut = "$root\out\${Label}_real" }
$corpusOut = "$root\out\${Label}_corpus"
$second = "$root\out\${Label}_second"
$t0 = Get-Date
# real documents: default input folder (if it has any) plus the one real sample next to the project folder
$real = @()
if (Test-Path "$root\input") { $real += Get-ChildItem "$root\input\*.docx" | Where-Object { $_.Name -notlike '~$*' } | ForEach-Object { $_.FullName } }
$sample = Join-Path (Split-Path -Parent $root) 'real_sample_manuscript.docx'
if (Test-Path $sample) { $real += $sample }
foreach ($f in $real) { & $exe $f $realOut @extra }
& $exe "$root\tests\corpus" $corpusOut @extra
"FIRST PASS DONE $((Get-Date) - $t0)"
# second pass on every produced .docx (no --min-score override needed: nothing should remain to convert at any threshold)
foreach ($o in @($realOut, $corpusOut)) {
    Get-ChildItem $o -Directory | Where-Object { $_.Name -notin @('_work', 'review') } | ForEach-Object {
        $d = Get-ChildItem $_.FullName -Filter *.docx | Select-Object -First 1
        if ($d) { & $exe $d.FullName $second @extra }
    }
}
"SECOND PASS DONE $((Get-Date) - $t0)"
& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\scripts\check_idempotence.ps1" -FirstOut $realOut -SecondOut $second
& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\scripts\check_idempotence.ps1" -FirstOut $corpusOut -SecondOut $second
& "$root\.venv\Scripts\python.exe" -I "$root\scripts\check_gate0.py" --expected "$root\tests\corpus\expected.json" --out $corpusOut
"ALL DONE $((Get-Date) - $t0)"
