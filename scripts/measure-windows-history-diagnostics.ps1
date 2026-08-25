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

function Convert-SearchMetric {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Metric,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedSamples,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedResults
    )

    $samples = Get-RequiredInt64 -Object $Metric -Name 'samples'
    $results = Get-RequiredInt64 -Object $Metric -Name 'results'
    $p50 = Get-RequiredInt64 -Object $Metric -Name 'p50_ms'
    $p95 = Get-RequiredInt64 -Object $Metric -Name 'p95_ms'
    if (
        $null -eq $samples -or
        $null -eq $results -or
        $null -eq $p50 -or
        $null -eq $p95 -or
        $samples -ne $ExpectedSamples -or
        $results -ne $ExpectedResults -or
        $p50 -lt 0 -or
        $p95 -lt $p50
    ) {
        return $null
    }

    return [ordered]@{
        samples = $samples
        results = $results
        p50_ms = $p50
        p95_ms = $p95
    }
}

function Convert-ImageLruMetric {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Metric
    )

    $capacity = Get-RequiredInt64 -Object $Metric -Name 'capacity'
    $entries = Get-RequiredInt64 -Object $Metric -Name 'entries'
    $oldestEvicted = Get-RequiredBoolean -Object $Metric -Name 'oldest_evicted'
    $newestAvailable = Get-RequiredBoolean -Object $Metric -Name 'newest_available'
    $workingSetBefore = Get-RequiredInt64 -Object $Metric -Name 'working_set_before_bytes'
    $workingSetAfter = Get-RequiredInt64 -Object $Metric -Name 'working_set_after_bytes'
    if (
        $null -eq $capacity -or
        $null -eq $entries -or
        $null -eq $oldestEvicted -or
        $null -eq $newestAvailable -or
        $null -eq $workingSetBefore -or
        $null -eq $workingSetAfter -or
        $capacity -ne 64 -or
        $entries -ne 64 -or
        $oldestEvicted -ne $true -or
        $newestAvailable -ne $true -or
        $workingSetBefore -lt 0 -or
        $workingSetAfter -lt 0
    ) {
        return $null
    }

    return [ordered]@{
        capacity = $capacity
        entries = $entries
        oldest_evicted = $oldestEvicted
        newest_available = $newestAvailable
        working_set_before_bytes = $workingSetBefore
        working_set_after_bytes = $workingSetAfter
    }
}

function Write-DiagnosticReport {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Output
    )

    $safeReport = $null
    foreach ($candidate in Get-JsonObjectCandidates -Output $Output) {
        if (-not (Test-UniqueJsonObject -Candidate $candidate)) {
            continue
        }

        try {
            $report = ($candidate | ConvertFrom-Json -ErrorAction Stop)
            $gitRevision = Get-RequiredString -Object $report -Name 'git_revision'
            $windowsVersion = Get-RequiredString -Object $report -Name 'windows_version'
            $dotnetVersion = Get-RequiredString -Object $report -Name 'dotnet_version'
            $rustVersion = Get-RequiredString -Object $report -Name 'rust_version'
            $configuration = Get-RequiredString -Object $report -Name 'configuration'
            $warmOsFileCache = Get-RequiredBoolean -Object $report -Name 'warm_os_file_cache'
            $coldSearchObjects = Get-RequiredBoolean -Object $report -Name 'cold_search_objects'
            if (
                $null -eq $gitRevision -or
                $null -eq $windowsVersion -or
                $null -eq $dotnetVersion -or
                $null -eq $rustVersion -or
                $null -eq $configuration -or
                $null -eq $warmOsFileCache -or
                $null -eq $coldSearchObjects -or
                $gitRevision -notmatch '^(?:[0-9a-fA-F]{7,64}|unavailable)$' -or
                $windowsVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or
                $dotnetVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or
                $rustVersion -notmatch '^(?:rustc \d+\.\d+\.\d+ \([0-9a-f]{8,40} \d{4}-\d{2}-\d{2}\)|unavailable)$' -or
                $configuration -ne 'Release' -or
                $warmOsFileCache -ne $true -or
                $coldSearchObjects -ne $true
            ) {
                continue
            }

            $logicalProcessorCount = Get-RequiredInt64 -Object $report -Name 'logical_processor_count'
            $physicalMemoryBytes = Get-RequiredInt64 -Object $report -Name 'physical_memory_bytes'
            $peakWorkingSetBytes = Get-RequiredInt64 -Object $report -Name 'peak_working_set_bytes'
            $managedHeapBytes = Get-RequiredInt64 -Object $report -Name 'managed_heap_bytes'
            if (
                $null -eq $logicalProcessorCount -or
                $null -eq $physicalMemoryBytes -or
                $null -eq $peakWorkingSetBytes -or
                $null -eq $managedHeapBytes -or
                $logicalProcessorCount -le 0 -or
                $physicalMemoryBytes -le 0 -or
                $peakWorkingSetBytes -lt 0 -or
                $managedHeapBytes -lt 0
            ) {
                continue
            }

            $ffiSubstring = Convert-SearchMetric -Metric $report.ffi_substring -ExpectedSamples 30 -ExpectedResults 100
            $ffiCombined = Convert-SearchMetric -Metric $report.ffi_combined -ExpectedSamples 30 -ExpectedResults 10
            $viewModelSubstring = Convert-SearchMetric -Metric $report.view_model_substring -ExpectedSamples 30 -ExpectedResults 100
            $viewModelCombined = Convert-SearchMetric -Metric $report.view_model_combined -ExpectedSamples 30 -ExpectedResults 10
            $emptyQuery = Convert-SearchMetric -Metric $report.empty_query -ExpectedSamples 3 -ExpectedResults 10000
            $imageLru = Convert-ImageLruMetric -Metric $report.image_lru
            if (
                $null -eq $ffiSubstring -or
                $null -eq $ffiCombined -or
                $null -eq $viewModelSubstring -or
                $null -eq $viewModelCombined -or
                $null -eq $emptyQuery -or
                $null -eq $imageLru
            ) {
                continue
            }

            $candidateReport = [ordered]@{
                git_revision = $gitRevision
                windows_version = $windowsVersion
                dotnet_version = $dotnetVersion
                rust_version = $rustVersion
                logical_processor_count = $logicalProcessorCount
                physical_memory_bytes = $physicalMemoryBytes
                configuration = 'Release'
                warm_os_file_cache = $true
                cold_search_objects = $true
                ffi_substring = $ffiSubstring
                ffi_combined = $ffiCombined
                view_model_substring = $viewModelSubstring
                view_model_combined = $viewModelCombined
                empty_query = $emptyQuery
                image_lru = $imageLru
                peak_working_set_bytes = $peakWorkingSetBytes
                managed_heap_bytes = $managedHeapBytes
            }

            if ($null -ne $safeReport) {
                return $false
            }

            $safeReport = $candidateReport
        }
        catch {
        }
    }

    if ($null -eq $safeReport) {
        return $false
    }

    [Console]::Out.WriteLine(($safeReport | ConvertTo-Json -Compress -Depth 4))
    return $true
}

$repositoryRoot = $null
$dataDirectory = $null
$exitCode = 0
$locationPushed = $false
$cleanupFailed = $false
$failureReported = $false

try {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $dataDirectory = Join-Path ([IO.Path]::GetTempPath()) (
        'clipboard-history-performance-' + [Guid]::NewGuid().ToString('N'))
    Push-Location $repositoryRoot
    $locationPushed = $true

    [void](New-Item -ItemType Directory -Path $dataDirectory -Force)

    $ffiBuildOutput = & cargo build -p clipboard-ffi --release *> $null
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        $toolchainOutput = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-windows-client-toolchain.ps1') -Configuration Release *> $null
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        $fixtureBuildOutput = & cargo build -p clipboard-core --release --bin prepare_history_performance *> $null
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        $fixtureBinary = Join-Path $repositoryRoot 'target\release\prepare_history_performance.exe'
        $fixtureOutput = & $fixtureBinary --data-dir $dataDirectory *> $null
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        $restoreOutput = & dotnet restore 'tests\Clipboard.Windows.Performance\Clipboard.Windows.Performance.csproj' *> $null
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        $diagnosticOutput = & dotnet run --project 'tests\Clipboard.Windows.Performance\Clipboard.Windows.Performance.csproj' -c Release --no-restore -- --data-dir $dataDirectory --vault-id 00000000-0000-0000-0000-000000005a17 --configuration Release 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0 -and -not (Write-DiagnosticReport -Output $diagnosticOutput)) {
            $exitCode = 1
            [Console]::Error.WriteLine('Windows history diagnostics report was unavailable.')
            $failureReported = $true
        }
    }
}
catch {
    if ($exitCode -eq 0) {
        $exitCode = 1
    }

    [Console]::Error.WriteLine('Windows history diagnostics could not start.')
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

    try {
        if ($null -ne $dataDirectory -and (Test-Path -LiteralPath $dataDirectory)) {
            Remove-Item -LiteralPath $dataDirectory -Recurse -Force
        }
    }
    catch {
        $cleanupFailed = $true
    }
}

if ($cleanupFailed) {
    [Console]::Error.WriteLine('Windows history diagnostics cleanup failed.')
    $failureReported = $true
    if ($exitCode -eq 0) {
        $exitCode = 1
    }
}

if ($exitCode -ne 0) {
    if (-not $failureReported) {
        [Console]::Error.WriteLine('Windows history diagnostics command failed.')
    }

    exit $exitCode
}
