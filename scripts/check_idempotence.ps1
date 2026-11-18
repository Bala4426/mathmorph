<#
.SYNOPSIS
  Idempotence check: converting an already-converted document must change nothing.

.DESCRIPTION
  Run 1 converts the originals into -FirstOut; run 2 converts run-1's output .docx into -SecondOut.
  For each subfolder of FirstOut holding a .docx (ignoring _work and review):
    1. compare FirstOut\<doc>\<name>.docx with SecondOut\<doc>\<name>.docx using
       <project root>\bin\MathMorph.exe compare <a> <b>   (must exit 0 and report "identical": true)
    2. SecondOut\<doc>\report.csv must contain 0 rows with status CONVERTED
    3. reconcile: CONVERTED rows in FirstOut\<doc>\report.csv
         == m:oMath(second-run FINAL_REPORT.md) - m:oMath(first-run FINAL_REPORT.md)
       using the line "- Existing native equations: m:oMath=X, m:oMathPara=Y".
  Writes SecondOut\idempotence_check.csv, prints a table.
  Exit 0 = all pass, 1 = any failure, 2 = usage error. Never runs the converter.
  Windows PowerShell 5.1 compatible.
#>
param(
    [Parameter(Mandatory = $true)][string]$FirstOut,
    [Parameter(Mandatory = $true)][string]$SecondOut,
    [string]$MathMorph = (Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\MathMorph.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $FirstOut -PathType Container)) { Write-Host "error: FirstOut not found: $FirstOut"; exit 2 }
if (-not (Test-Path -LiteralPath $SecondOut -PathType Container)) { Write-Host "error: SecondOut not found: $SecondOut"; exit 2 }
if (-not (Test-Path -LiteralPath $MathMorph -PathType Leaf)) { Write-Host "error: MathMorph.exe not found: $MathMorph"; exit 2 }

function Read-ReportCsv([string]$path) {
    # UTF-8 (BOM) CSV; drop comment lines starting with '#'.
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $lines = $text -split "`r?`n" | Where-Object { $_ -ne '' -and -not $_.StartsWith('#') }
    if ($null -eq $lines -or @($lines).Count -le 1) { return @() }
    return @($lines | ConvertFrom-Csv)
}

function Count-Converted([string]$path) {
    $rows = Read-ReportCsv $path
    return @($rows | Where-Object { $_.status -eq 'CONVERTED' }).Count
}

function Get-NativeOMath([string]$path) {
    # Returns m:oMath count from FINAL_REPORT.md, or $null if the line is absent.
    foreach ($line in [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)) {
        $m = [regex]::Match($line, '^\s*-\s*Existing native equations:\s*m:oMath=(\d+),\s*m:oMathPara=(\d+)')
        if ($m.Success) { return [int]$m.Groups[1].Value }
    }
    return $null
}

$results = New-Object System.Collections.Generic.List[object]

$docDirs = Get-ChildItem -LiteralPath $FirstOut -Directory | Where-Object { $_.Name -ne '_work' -and $_.Name -ne 'review' } | Sort-Object Name
foreach ($dir in $docDirs) {
    $docxs = @(Get-ChildItem -LiteralPath $dir.FullName -File -Filter '*.docx' | Where-Object { $_.Name -notlike '~$*' } | Sort-Object Name)
    if ($docxs.Count -eq 0) { continue }
    foreach ($a in $docxs) {
        $r = [ordered]@{
            document = $dir.Name; docx = $a.Name
            compare_exit = ''; identical = ''; differ = ''; onlyA = ''; onlyB = ''; sameOrder = ''
            second_converted = ''; first_converted = ''; first_omath = ''; second_omath = ''; omath_delta = ''
            compare_ok = 'FAIL'; second_converted_ok = 'FAIL'; reconcile_ok = 'FAIL'; result = 'FAIL'; detail = ''
        }
        $notes = New-Object System.Collections.Generic.List[string]
        $secondDir = Join-Path $SecondOut $dir.Name
        $b = Join-Path $secondDir $a.Name

        # 1. structural compare
        if (Test-Path -LiteralPath $b -PathType Leaf) {
            $json = (& $MathMorph compare $a.FullName $b | Out-String)
            $code = $LASTEXITCODE
            $r.compare_exit = $code
            try {
                $o = $json | ConvertFrom-Json
                $r.identical = [string]$o.identical
                $r.sameOrder = [string]$o.sameOrder
                $r.differ = (@($o.differ) -join ';')
                $r.onlyA = (@($o.onlyA) -join ';')
                $r.onlyB = (@($o.onlyB) -join ';')
                if ($code -eq 0 -and $o.identical -eq $true) { $r.compare_ok = 'PASS' }
                else { $notes.Add('second-run docx differs from first-run docx') }
            } catch {
                $notes.Add("compare output not JSON: $($json.Trim())")
            }
        } else {
            $notes.Add("missing second-run docx: $b")
        }

        # 2. second run converts nothing
        $rep2 = Join-Path $secondDir 'report.csv'
        if (Test-Path -LiteralPath $rep2 -PathType Leaf) {
            $n2 = Count-Converted $rep2
            $r.second_converted = $n2
            if ($n2 -eq 0) { $r.second_converted_ok = 'PASS' } else { $notes.Add("second run CONVERTED=$n2 (expected 0)") }
        } else {
            $notes.Add("missing $rep2")
        }

        # 3. reconcile CONVERTED(run1) == oMath(run2) - oMath(run1)
        $rep1 = Join-Path $dir.FullName 'report.csv'
        $fin1 = Join-Path $dir.FullName 'FINAL_REPORT.md'
        $fin2 = Join-Path $secondDir 'FINAL_REPORT.md'
        $ok3 = $true
        if (Test-Path -LiteralPath $rep1 -PathType Leaf) { $r.first_converted = Count-Converted $rep1 } else { $ok3 = $false; $notes.Add("missing $rep1") }
        $o1 = $null; $o2 = $null
        if (Test-Path -LiteralPath $fin1 -PathType Leaf) { $o1 = Get-NativeOMath $fin1 } else { $notes.Add("missing $fin1") }
        if (Test-Path -LiteralPath $fin2 -PathType Leaf) { $o2 = Get-NativeOMath $fin2 } else { $notes.Add("missing $fin2") }
        if ($null -eq $o1 -or $null -eq $o2) { $ok3 = $false; $notes.Add('native-equation line not found in a FINAL_REPORT.md') }
        else {
            $r.first_omath = $o1; $r.second_omath = $o2; $r.omath_delta = $o2 - $o1
        }
        if ($ok3) {
            if ([int]$r.first_converted -eq [int]$r.omath_delta) { $r.reconcile_ok = 'PASS' }
            else { $notes.Add("first-run CONVERTED=$($r.first_converted) but oMath delta=$($r.omath_delta)") }
        }

        if ($r.compare_ok -eq 'PASS' -and $r.second_converted_ok -eq 'PASS' -and $r.reconcile_ok -eq 'PASS') { $r.result = 'PASS' }
        $r.detail = ($notes -join ' | ')
        $results.Add([pscustomobject]$r)
    }
}

$csvPath = Join-Path $SecondOut 'idempotence_check.csv'
if ($results.Count -gt 0) {
    $csvText = ($results | ConvertTo-Csv -NoTypeInformation) -join "`r`n"
} else {
    $csvText = '"document","docx","result","detail"' + "`r`n" + '"(none)","","FAIL","no .docx found in FirstOut subfolders"'
}
[System.IO.File]::WriteAllText($csvPath, $csvText + "`r`n", (New-Object System.Text.UTF8Encoding($true)))

$results | Format-Table document, compare_exit, identical, second_converted, first_converted, first_omath, second_omath, compare_ok, second_converted_ok, reconcile_ok, result -AutoSize | Out-String -Width 250 | Write-Host
$fails = @($results | Where-Object { $_.result -ne 'PASS' })
foreach ($f in $fails) { Write-Host ("FAIL {0}: {1}" -f $f.document, $f.detail) }
Write-Host ("idempotence: {0}/{1} documents passed -> {2}" -f ($results.Count - $fails.Count), $results.Count, $csvPath)

if ($results.Count -eq 0) { Write-Host 'error: no .docx found in FirstOut subfolders'; exit 1 }
if ($fails.Count -gt 0) { exit 1 }
exit 0
