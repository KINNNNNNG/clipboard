$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Get-RequiredInt64 {
    param(
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $property.Value -isnot [long]) {
        return $null
    }

    return $property.Value
}

function Get-RequiredBoolean {
    param(
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $property.Value -isnot [bool]) {
        return $null
    }

    return $property.Value
}

function Get-RequiredString {
    param(
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $property.Value -isnot [string]) {
        return $null
    }

    return $property.Value
}

function Get-JsonObjectCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Output
    )

    foreach ($entry in $Output) {
        $text = [string]$entry
        $start = -1
        $depth = 0
        $inString = $false
        $escaped = $false

        for ($index = 0; $index -lt $text.Length; $index++) {
            $character = $text[$index]
            if ($start -lt 0) {
                if ($character -eq '{') {
                    $start = $index
                    $depth = 1
                }

                continue
            }

            if ($inString) {
                if ($escaped) {
                    $escaped = $false
                }
                elseif ($character -eq '\') {
                    $escaped = $true
                }
                elseif ($character -eq '"') {
                    $inString = $false
                }

                continue
            }

            if ($character -eq '"') {
                $inString = $true
            }
            elseif ($character -eq '{') {
                $depth++
            }
            elseif ($character -eq '}') {
                $depth--
                if ($depth -eq 0) {
                    $text.Substring($start, $index - $start + 1)
                    $start = -1
                }
            }
        }
    }
}

function Test-JsonElementHasUniquePropertyNames {
    param(
        [Parameter(Mandatory = $true)]
        [System.Text.Json.JsonElement]$Element
    )

    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $propertyNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $propertyNames.Add($property.Name)) {
                return $false
            }

            if (-not (Test-JsonElementHasUniquePropertyNames -Element $property.Value)) {
                return $false
            }
        }

        return $true
    }

    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) {
            if (-not (Test-JsonElementHasUniquePropertyNames -Element $item)) {
                return $false
            }
        }
    }

    return $true
}

function Test-UniqueJsonObject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Candidate
    )

    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Candidate)
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            return $false
        }

        return (Test-JsonElementHasUniquePropertyNames -Element $document.RootElement)
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function Write-HistorySearchMetrics {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Output
    )

    $expected = @{
        substring = @{ Samples = 30; Results = 100; Threshold = 200 }
        combined_filters = @{ Samples = 30; Results = 10; Threshold = 200 }
        empty_query = @{ Samples = 3; Results = 10000; Threshold = $null }
    }
    $scenarioOrder = @('substring', 'combined_filters', 'empty_query')
    $reported = @{}

    foreach ($candidate in Get-JsonObjectCandidates -Output $Output) {
        if (-not (Test-UniqueJsonObject -Candidate $candidate)) {
            continue
        }

        try {
            $metric = ($candidate | ConvertFrom-Json -ErrorAction Stop)
            $scenario = Get-RequiredString -Object $metric -Name 'scenario'
            if (
                $null -eq $scenario -or
                -not ($scenarioOrder -ccontains $scenario)
            ) {
                continue
            }

            $samples = Get-RequiredInt64 -Object $metric -Name 'samples'
            $results = Get-RequiredInt64 -Object $metric -Name 'results'
            $p50 = Get-RequiredInt64 -Object $metric -Name 'p50_ms'
            $p95 = Get-RequiredInt64 -Object $metric -Name 'p95_ms'
            $passed = Get-RequiredBoolean -Object $metric -Name 'passed'
            $thresholdProperty = $metric.PSObject.Properties['threshold_ms']
            if (
                $null -eq $scenario -or
                $null -eq $samples -or
                $null -eq $results -or
                $null -eq $p50 -or
                $null -eq $p95 -or
                $null -eq $passed -or
                $null -eq $thresholdProperty
            ) {
                continue
            }

            $threshold = $thresholdProperty.Value
            $expectedThreshold = $expected[$scenario].Threshold
            if (
                ($null -eq $expectedThreshold -and $null -ne $threshold) -or
                ($null -ne $expectedThreshold -and $threshold -isnot [long])
            ) {
                continue
            }

            if (
                $samples -ne $expected[$scenario].Samples -or
                $results -ne $expected[$scenario].Results -or
                $passed -ne $true -or
                $p50 -lt 0 -or
                $p95 -lt $p50 -or
                $threshold -ne $expectedThreshold -or
                ($null -ne $threshold -and $p95 -gt $threshold)
            ) {
                continue
            }

            if ($reported.ContainsKey($scenario)) {
                return $false
            }

            $reported[$scenario] = [ordered]@{
                scenario = $scenario
                samples = $samples
                results = $results
                p50_ms = $p50
                p95_ms = $p95
                threshold_ms = $threshold
                passed = $passed
            }
        }
        catch {
        }
    }

    if ($reported.Count -ne $expected.Count) {
        return $false
    }

    foreach ($scenario in $scenarioOrder) {
        [Console]::Out.WriteLine(($reported[$scenario] | ConvertTo-Json -Compress))
    }

    return $true
}

$repositoryRoot = $null
$exitCode = 0
$locationPushed = $false
$cleanupFailed = $false
$failureReported = $false

try {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    Push-Location $repositoryRoot
    $locationPushed = $true

    $historyOutput = & cargo test -p clipboard-core --release --test history_search_performance -- --ignored --test-threads=1 --nocapture 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0 -and -not (Write-HistorySearchMetrics -Output $historyOutput)) {
        $exitCode = 1
        [Console]::Error.WriteLine('History search performance metrics were unavailable.')
        $failureReported = $true
    }
}
catch {
    if ($exitCode -eq 0) {
        $exitCode = 1
    }

    [Console]::Error.WriteLine('History search performance gate could not start.')
    $failureReported = $true
}
finally {
    if ($locationPushed) {
        try {
            Pop-Location
        }
        catch {
            $cleanupFailed = $true
        }
    }
}

if ($cleanupFailed) {
    [Console]::Error.WriteLine('History search performance cleanup failed.')
    $failureReported = $true
    if ($exitCode -eq 0) {
        $exitCode = 1
    }
}

if ($exitCode -ne 0) {
    if (-not $failureReported) {
        [Console]::Error.WriteLine('History search performance command failed.')
    }

    exit $exitCode
}
