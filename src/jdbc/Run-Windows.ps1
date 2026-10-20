# Run only from the protected manual workflow. No child stdout/stderr is forwarded.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Build', 'Live', 'Verify', 'Gate')][string] $Phase,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string] $Runtime,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string] $Architecture,
    [string] $SourceSha = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ExpectedSourceSha = if ($SourceSha -cmatch '\A[0-9a-fA-F]{40}\z') { $SourceSha.ToLowerInvariant() } else { '' }
$root = Join-Path $env:RUNNER_TEMP "jdbc-rc-$Runtime"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$reportPath = Join-Path $root "report-$Runtime.json"
$secretNames = @('EVENTHOUSE_DB_NAME', 'AZURE_TENANT_ID', 'AZURE_SPN_CLIENT_ID', 'AZURE_SPN_SECRET', 'EVENTHOUSE_QUERY_HOST')
$stage = 'initialise'
$outcome = 'fail'
$report = [ordered]@{
    profile = 'jdbc-rc1-qualification'; source_sha = $ExpectedSourceSha; source_clean = $false
    contract_version = 'product 0x00010003 / 16 exports'
    runner = $(if ($Architecture -eq 'arm64') { 'windows-11-arm' } else { 'windows-2025' })
    runtime = $Runtime; architecture = $Architecture; phase = $Phase
    run_id = $env:GITHUB_RUN_ID; run_attempt = $env:GITHUB_RUN_ATTEMPT
    sdk = '10.0.113'; release_ready = $false; outcome = 'blocked'; stage = $stage
    jvms = @(); sha256 = @{}; limitations = 'Experimental unsigned candidate. Qualification is not release approval or original-consumer compatibility.'
    gates = [ordered]@{ live_inflight_cancel = 'blocked'; live_inflight_timeout = 'blocked'
        clean_machine_without_dotnet = 'blocked'; secret_scan = 'blocked'; dependency_scan = 'blocked'
        licence_review = 'blocked'; independent_review = 'blocked'; human_promotion = 'pending-final-release-review'
        live_auth_failure_refresh = 'blocked'; install_rollback = 'blocked'
         threat_model = 'blocked'; runtime_sbom_coverage = 'blocked'
          distribution_notices = 'blocked' }
    rc_deferrals = @{ live_overcell = 'deferred-not-requested'; forced_expiry = 'deferred-not-qualified'
        bespoke_tls = 'deferred-not-qualified' }
    commands = @(); managed_tests = @{ mode = 'product --streaming'; outcome = 'blocked'; summary = 'unknown'
        exit_code = $null; pass = $null; fail = $null; skipped = $null; blocked = $null; tests = @() }
    published_native_tests = @{ mode = '--native-streaming'; outcome = 'blocked'; pass = 0; fail = 0; skipped = 0; blocked = 1 }
    route_outcome = 'blocked'; secret_presence = @{}
}

function Invoke-Child([string] $File, [string[]] $Arguments, [int] $Seconds = 300, [switch] $Live) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File
    $info.WorkingDirectory = $repo
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    if (-not $Live) { foreach ($name in $secretNames) { $null = $info.Environment.Remove($name) } }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        # Exit alone is insufficient: a descendant may still own either pipe.
        while (-not ($process.HasExited -and $stdout.IsCompleted -and $stderr.IsCompleted) -and
            $clock.Elapsed.TotalSeconds -lt $Seconds) {
            [Threading.Thread]::Sleep(20)
        }
        if ($clock.Elapsed.TotalSeconds -ge $Seconds) {
            try { if (-not $process.HasExited) { $process.Kill($true) } } catch { }
            $clock.Restart()
            while (-not ($process.HasExited -and $stdout.IsCompleted -and $stderr.IsCompleted) -and
                $clock.ElapsedMilliseconds -lt 2000) {
                [Threading.Thread]::Sleep(20)
            }
            # Never wait indefinitely for EOF or forward partial child output.
            try { $process.StandardOutput.Dispose() } catch { }
            try { $process.StandardError.Dispose() } catch { }
            throw 'Child timeout.'
        }
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        return @{ Code = $process.ExitCode; Text = $text; ErrorText = $errorText }
    } finally { $process.Dispose() }
}

function Read-ManagedStreamingDiagnostics($Result) {
    # Exact product --streaming descriptions from managed/Program.cs. Persist only
    # fixed IDs/statuses, never descriptions, exception suffixes or unmatched lines.
    $descriptions = @(
        'cursor split reads preserve dynamic bytes',
        'cursor consumes generated response beyond old cap',
        'cursor late protocol failures never become EOF',
        'cursor chosen guards reject excess',
        'cursor EOF releases lease while result metadata remains',
        'cursor idle cancellation detaches and recovers',
        'cursor idle timeout detaches and recovers',
        'cursor close races admitted fetch safely',
        'cursor cancelled disposal retains reservation after token close until drain',
        'cursor row metadata and column boundaries',
        'cursor auxiliary schemas order and successful status',
        'cursor fetch cancellation timeout and operation close',
        'cursor physical EOF and transport interruption',
        'cursor parser capacities and nested spill remain bounded',
        'cursor rejects invalid UTF8 strings and nested property names',
        'cursor metadata budget survives primary token spill',
        'cursor terminal getters preserve cancel timeout and protocol status',
        'cursor row rejection diagnostics are closed and latched',
        'cursor inline service objects fail with closed categories',
        'complete-result request preserves SQL and excludes overriding options',
        'inline messages expose only closed machine-code categories',
        'product ABI cursor and private operation path',
        'product security ABI version is explicit',
        'JWT audience must match exactly',
        'both HTTP handlers disable redirects',
        'metadata content type fails before token and query',
        'malformed metadata fails before token and query',
        'metadata redirect fails before token and query',
        'wrong-resource metadata remains authentication failure on both paths',
        'token audience mismatch dispatches no query',
        'Eventhouse redirect is not followed',
        'metadata transport failure is classified on both paths',
        'nested token transport failure is classified on both paths',
        'aggregate token transport failure is classified on both paths',
        'query transport failure is classified on both paths',
        'unknown transport reason falls back safely on both paths',
        'hostile endpoint is rejected before network',
        'error text is fixed and bounded',
        'Fabric raw origins validate before URI normalisation',
        'native open retains configured normalised origin',
        'metadata HTTP failures retain authentication status on both execute paths',
        'query HTTP failures use safe stage-specific statuses on both execute paths',
        'token authentication failures remain sanitised on both paths',
        'malformed metadata object kinds reject authentication on both paths'
    )
    $records = [ordered]@{}
    $summary = $null
    $summaryCount = 0
    $unexpected = $false
    foreach ($line in (($Result.Text + "`n" + $Result.ErrorText) -split '\r\n|\n|\r')) {
        if ($line -ceq '') { continue }
        $recognised = $false
        for ($index = 0; $index -lt $descriptions.Count; $index++) {
            $description = $descriptions[$index]
            $id = 'streaming-{0:D2}' -f ($index + 1)
            if ($line -ceq "PASS $description") {
                $recognised = $true
                if ($records.Contains($id)) { $unexpected = $true }
                # A repeated PASS must not conceal a previously observed FAIL.
                if (-not $records.Contains($id)) { $records[$id] = 'PASS' }
            } elseif ($line -cmatch ('\AFAIL ' + [regex]::Escape($description) + ': [^\r\n]+\z')) {
                $recognised = $true
                $records[$id] = 'FAIL'
            }
        }
        if ($line -cmatch '\ARESULT pass=([0-9]{1,2}) fail=([0-9]{1,2}) skipped=([0-9]{1,2}) blocked=([0-9]{1,2})\z') {
            $recognised = $true
            $summaryCount++
            $counts = @([int] $Matches[1], [int] $Matches[2], [int] $Matches[3], [int] $Matches[4])
            if (($counts | Measure-Object -Sum).Sum -eq 44 -and
                @($counts | Where-Object { $_ -gt 44 }).Count -eq 0) {
                $summary = $counts
            }
        }
        if (-not $recognised) { $unexpected = $true }
    }
    $diagnostic = [ordered]@{
        mode = 'product --streaming'; outcome = 'unknown'; exit_code = [int] $Result.Code
        summary = 'unknown'; pass = $null; fail = $null; skipped = $null; blocked = $null
        unexpected_output = $unexpected
        tests = @($records.Keys | ForEach-Object { @{ id = $_; status = $records[$_] } })
    }
    if ($summaryCount -eq 1 -and $null -ne $summary) {
        $diagnostic.summary = 'observed'
        $diagnostic.pass = $summary[0]; $diagnostic.fail = $summary[1]
        $diagnostic.skipped = $summary[2]; $diagnostic.blocked = $summary[3]
        $diagnostic.outcome = if ($Result.Code -eq 0 -and $summary[0] -eq 44 -and
            $records.Count -eq 44 -and -not $unexpected -and $records.Values -notcontains 'FAIL') { 'pass' }
            elseif ($Result.Code -ne 0 -or $summary[1] -gt 0 -or $records.Values -contains 'FAIL') { 'fail' }
            else { 'blocked' }
    }
    return $diagnostic
}

function Read-RcDiagnostics($Result) {
    # RcSmoke's closed output grammar. Never persist raw text or arbitrary tokens.
    $cases = @('cursor_cancel', 'idle_deadline', 'invalid_secret', 'overcell')
    $records = @(); $unexpected = -not [string]::IsNullOrEmpty($Result.ErrorText)
    $base = 'PASS WP005K_RC assertions=45 known_values=pass query_rejection=pass recovery=pass unsupported=pass repeat_close=pass live_inflight_cancel=blocked live_inflight_timeout=blocked'
    $token = '(?:(?:SQLTimeoutException|SQLException|other):(?:HY008|HYT00|HY000|HY010|08003|08006|28000|42000|other):(?:[0-9]|1[0-4]|other)|(?:IllegalStateException|InterruptedException|other):none:none)'
    $lines = @($Result.Text -split '\r\n|\n|\r')
    # Permit one terminating newline, not arbitrary blank/unmatched child output.
    if ($lines.Count -gt 0 -and $lines[-1] -ceq '') { $lines = @($lines | Select-Object -SkipLast 1) }
    foreach ($line in $lines) {
        if ($line -ceq $base) {
            $records += @{ kind = 'base'; assertions = 45 }
        } elseif ($line -cmatch '\AWP006D case=(cursor_cancel|idle_deadline|invalid_secret|overcell) outcome=(pass|fail|blocked_no_first_row|blocked_deadline_before_first_row|blocked_not_requested) assertions=([0-9]{1,10})\z') {
            $records += @{ kind = 'case'; name = $Matches[1]; outcome = $Matches[2]; assertions = [long] $Matches[3] }
        } elseif ($line -cmatch ('\AWP006D diagnostic=(cursor_cancel|idle_deadline|invalid_secret|overcell) phase=([0-9]|1[0-6]) failure=(' + $token + ') terminal=(none|' + $token + ') suppressed=(none|' + $token + ')\z')) {
            $records += @{ kind = 'diagnostic'; name = $Matches[1]; phase = [int] $Matches[2]
                failure = $Matches[3]; terminal = $Matches[4]; suppressed = $Matches[5] }
        } elseif ($line -cmatch '\AWP006D cases=4 pass=([0-4]) fail=([0-4]) blocked=([0-4]) assertions=([0-9]{1,10})\z') {
            $records += @{ kind = 'totals'; pass = [int] $Matches[1]; fail = [int] $Matches[2]
                blocked = [int] $Matches[3]; assertions = [long] $Matches[4] }
        } elseif ($line -cmatch '\AFAIL WP005K_RC stage=(configuration|open|select-one|known-values|query-rejection|recovery|unsupported|repeat-close|live-failures)\z') {
            $records += @{ kind = 'failure'; stage = $Matches[1] }
        } else { $unexpected = $true }
    }
    $diagnostic = @{ outcome = 'blocked'; child_exit = [int] $Result.Code; records = $records
        unexpected_output = $unexpected; overcell = 'deferred-not-requested'; base_assertions = 45 }
    if (@($records | Where-Object { $_.kind -ceq 'failure' -or
        ($_.kind -ceq 'case' -and $_.outcome -ceq 'fail') -or
        ($_.kind -ceq 'totals' -and $_.fail -gt 0) }).Count -gt 0) {
        $diagnostic.outcome = 'fail'; return $diagnostic
    }
    if ($unexpected -or $Result.Code -ne 2 -or $records.Count -ne 6 -or
        $records[0].kind -cne 'base' -or $records[5].kind -cne 'totals') { return $diagnostic }
    $counts = @(7, 7, 6, 0)
    for ($index = 0; $index -lt 4; $index++) {
        $record = $records[$index + 1]
        $expectedOutcome = if ($index -eq 3) { 'blocked_not_requested' } else { 'pass' }
        if ($record.kind -cne 'case' -or $record.name -cne $cases[$index] -or
            $record.outcome -cne $expectedOutcome -or $record.assertions -ne $counts[$index]) { return $diagnostic }
    }
    $total = $records[5]
    if ($total.pass -eq 3 -and $total.fail -eq 0 -and $total.blocked -eq 1 -and $total.assertions -eq 20) {
        $diagnostic.outcome = 'pass'
    }
    return $diagnostic
}

function Require-Success($Result, [ValidateSet('restore', 'publish', 'javac', 'jar', 'managed')][string] $BuildCommand) {
    if ($Result.Code -ne 0) {
        # Only credential-free build calls opt in. Never forward raw child text.
        if ($BuildCommand -eq 'managed') {
            # Read-ManagedStreamingDiagnostics already captured bounded evidence.
            # Managed failure output must never enter the generic raw-line path.
            foreach ($test in $report.managed_tests.tests) {
                if ($test.status -ceq 'FAIL') { Write-Host "JDBC_MANAGED_FAIL $($test.id)" }
            }
        } elseif ($Phase -eq 'Build' -and $BuildCommand) {
            # Persist only closed diagnostic codes. Arbitrary build text is unsafe too.
            $codes = @([regex]::Matches(($Result.Text + "`n" + $Result.ErrorText),
                '(?im)\b(?:error|warning)\s+((?:NU|NETSDK|MSB|CS|IL)[0-9]{4}|LNK[0-9]{4}|C[0-9]{4})\s*:') |
                ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() } | Sort-Object -Unique | Select-Object -First 16)
            $report.build_failure = [ordered]@{
                command = $BuildCommand; exit_code = [int] $Result.Code; diagnostic_codes = $codes
                category = $(if ($codes.Count) { 'coded-diagnostic' } else { 'unclassified-build-failure' })
            }
        }
        throw 'Child failed. Raw output suppressed.'
    }
}

function Hash([string] $Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

function Source-IdentityReason([string] $Expected, $Head, $Status) {
    if ($Expected -cnotmatch '\A[0-9a-f]{40}\z') { return 'invalid_identity' }
    if ($null -eq $Head -or $Head.Code -ne 0 -or $Head.ErrorText -or
        $Head.Text.Trim() -cnotmatch '^[0-9a-f]{40}$') { return 'git_head' }
    if ($null -eq $Status -or $Status.Code -ne 0 -or $Status.ErrorText) { return 'git_status' }
    if ($Head.Text.Trim() -cne $Expected) { return 'head_mismatch' }
    if ($Status.Text.Trim()) { return 'dirty_checkout' }
    return 'none'
}

function Require-ProductSource {
    $head = $null; $status = $null
    if ($ExpectedSourceSha -cmatch '^[0-9a-f]{40}$') {
        try { $head = Invoke-Child 'git' @('rev-parse', 'HEAD') } catch { }
        if ($null -ne $head) {
            try { $status = Invoke-Child 'git' @('status', '--porcelain') } catch { }
        }
    }
    $reason = Source-IdentityReason $ExpectedSourceSha $head $status
    if ($reason -cne 'none') {
        $report.source_failure = $reason
        Write-Output "BLOCKED JDBC_SOURCE reason=$reason"
        throw 'Product source identity rejected.'
    }
}

function Require-Pe([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Missing DOS signature.' }
        $stream.Position = 0x3c
        $offset = $reader.ReadUInt32()
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Missing PE signature.' }
        $machine = $reader.ReadUInt16()
        $expectedMachine = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
        if ($machine -ne $expectedMachine) { throw 'PE architecture mismatch. Emulation is not admitted.' }
        return ('{0:X4}' -f $machine)
    } finally { $reader.Dispose() }
}

function Payload-Hashes([string] $Directory) {
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName) {
        $hashes[[IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')] = Hash $file.FullName
    }
    return $hashes
}

function Read-ReleaseGate($Evidence, [string] $Source, [string] $TargetRuntime,
    [string] $TargetArchitecture, [string] $RunId, [string] $Attempt) {
    if ($Source -cnotmatch '^[0-9a-f]{40}$' -or $RunId -cnotmatch '^[1-9][0-9]*$' -or
        $Attempt -cnotmatch '^[1-9][0-9]*$' -or $TargetArchitecture -cnotin @('x64', 'arm64') -or
        $TargetRuntime -cne "win-$TargetArchitecture" -or $Evidence -isnot [Collections.IDictionary]) {
        throw 'Invalid gate context.'
    }
    foreach ($key in 'source_sha', 'source_clean', 'runtime', 'architecture', 'run_id', 'run_attempt',
        'phase', 'outcome', 'route_outcome', 'stage', 'release_ready', 'gates') {
        if (-not $Evidence.Contains($key)) { throw 'Missing gate identity.' }
    }
    foreach ($key in 'source_sha', 'runtime', 'architecture', 'run_id', 'run_attempt',
        'phase', 'outcome', 'route_outcome', 'stage') {
        if ($Evidence[$key] -isnot [string]) { throw 'Invalid gate identity type.' }
    }
    if ($Evidence.source_sha -isnot [string] -or $Evidence.source_sha -cne $Source -or
        $Evidence.runtime -cne $TargetRuntime -or $Evidence.architecture -cne $TargetArchitecture -or
        $Evidence.run_id -isnot [string] -or $Evidence.run_id -cne $RunId -or
        $Evidence.run_attempt -isnot [string] -or $Evidence.run_attempt -cne $Attempt -or
        $Evidence.source_clean -isnot [bool] -or -not $Evidence.source_clean -or
        $Evidence.release_ready -isnot [bool] -or $Evidence.release_ready -ne $false -or
        $Evidence.phase -cne 'Live' -or $Evidence.outcome -cne 'pass' -or
        $Evidence.stage -cne 'complete' -or $Evidence.route_outcome -cne 'pass') {
        throw 'Incomplete or stale live report.'
    }
    $expected = @{
        live_inflight_cancel = @('blocked', 'pass: local cursor termination and same-connection recovery')
        live_inflight_timeout = @('blocked', 'pass: idle cursor deadline and same-connection recovery')
        live_auth_failure_refresh = @('blocked', 'pass: invalid-secret/reconnect only; transparent refresh not qualified')
        clean_machine_without_dotnet = @('blocked', 'pass')
        secret_scan = @('blocked', 'blocked: no approved scanner result', 'pass')
        dependency_scan = @('blocked', 'fail', 'pass')
        licence_review = @('blocked', 'pass')
        independent_review = @('blocked', 'pass')
        human_promotion = @('pending-final-release-review', 'pass')
        install_rollback = @('blocked', 'pass')
        threat_model = @('blocked', 'pass')
        runtime_sbom_coverage = @('blocked', 'pass')
        distribution_notices = @('blocked', 'pass')
    }
    $gates = $Evidence.gates
    if ($gates -isnot [Collections.IDictionary] -or $gates.Count -ne $expected.Count) {
        throw 'Invalid mandatory gate set.'
    }
    $pending = 0
    foreach ($key in $expected.Keys) {
        if (@($gates.Keys) -cnotcontains $key -or $gates[$key] -isnot [string] -or
            $gates[$key] -cnotin $expected[$key]) { throw 'Missing or invalid mandatory gate.' }
        if ($gates[$key] -cnotmatch '^pass(?:$|:)') { $pending++ }
    }
    return $pending
}

function Public-Summary($Evidence) {
    $pending = Read-ReleaseGate $Evidence $ExpectedSourceSha $Runtime $Architecture $env:GITHUB_RUN_ID $env:GITHUB_RUN_ATTEMPT
    foreach ($key in 'jar', 'dll', 'jna', 'windows_lock', 'managed_lock') {
        if ($Evidence.sha256[$key] -isnot [string] -or $Evidence.sha256[$key] -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid digest.' }
    }
    if ($Evidence.package_sha256 -isnot [string] -or $Evidence.package_sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid archive digest.' }
    # Positive projection only: no arbitrary keys, paths, version strings, messages,
    # live row counts or nested report objects cross the public boundary.
    return [ordered]@{ schema_version = 1; source_sha = $ExpectedSourceSha; runtime = $Runtime
        architecture = $Architecture; run_id = $env:GITHUB_RUN_ID; run_attempt = $env:GITHUB_RUN_ATTEMPT
        outcome = 'live-route-pass'; release_ready = $false; pending_release_gates = $pending
        distribution = 'blocked-notice-and-native-attribution-review'; candidate_uploaded = $false
        package_sha256 = $Evidence.package_sha256; jar_sha256 = $Evidence.sha256.jar
        dll_sha256 = $Evidence.sha256.dll; jna_sha256 = $Evidence.sha256.jna }
}

function Safe-PayloadName([string] $Name) {
    if ($Name.Length -gt 240 -or $Name -cnotmatch '^[A-Za-z0-9_./-]+$' -or
        $Name.StartsWith('/') -or $Name.Contains('//') -or
        @($Name.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { return $false }
    return $Name -cmatch ('^(?:README\.md|LICENSE|SECURITY\.md|THIRD-PARTY-NOTICES\.md|checksums\.sha256|provenance\.json|sbom\.cdx\.json|eventhouse-jdbc-0\.1\.0\.jar|lib/jna-5\.17\.0\.jar|native/' + [regex]::Escape($Runtime) + '/eventhouse-engine\.dll|docs/BUILD-INSTALL\.md|contracts/(?:eventhouse-engine\.h|product-abi\.md|operation-control\.md|streaming-results\.md|dynamic-json\.md)|infra/release/notice-materials\.json|licences/[A-Za-z0-9_./-]+)$')
}

function Verify-Archive([string] $Zip, $Expected, [string] $Destination) {
    if ($Expected -isnot [Collections.IDictionary] -or $Expected.Count -lt 20 -or $Expected.Count -gt 1000) { throw 'Invalid payload set.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $total = 0L
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if (-not (Safe-PayloadName $name) -or -not $names.Add($name) -or
                -not (@($Expected.Keys) -ccontains $name) -or $Expected[$name] -cnotmatch '^[0-9a-f]{64}$' -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                $entry.Length -gt 134217728) { throw 'Invalid archive entry.' }
            $total += $entry.Length
            if ($total -gt 268435456) { throw 'Archive too large.' }
            $target = Join-Path $Destination $name
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
            if ((Hash $target) -cne $Expected[$name]) { throw 'Payload digest mismatch.' }
        }
        if ($names.Count -ne $Expected.Count) { throw 'Incomplete archive.' }
    } finally { $archive.Dispose() }
    $checks = [ordered]@{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Destination 'checksums.sha256'))) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9_./-]+)$') { throw 'Invalid checksum line.' }
        $digest = $Matches[1]; $name = $Matches[2]
        if (-not (Safe-PayloadName $name) -or $name -ceq 'checksums.sha256' -or $checks.Contains($name) -or
            -not (@($Expected.Keys) -ccontains $name) -or $Expected[$name] -cne $digest) { throw 'Invalid checksum set.' }
        $checks[$name] = $digest
    }
    if ($checks.Count -ne $Expected.Count - 1) { throw 'Incomplete checksums.' }
    foreach ($required in 'LICENSE', 'README.md', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'sbom.cdx.json',
        'provenance.json', 'infra/release/notice-materials.json', 'eventhouse-jdbc-0.1.0.jar',
        'lib/jna-5.17.0.jar', "native/$Runtime/eventhouse-engine.dll") {
        if (-not (@($Expected.Keys) -ccontains $required)) { throw 'Required payload absent.' }
    }
    if ((Hash (Join-Path $Destination 'LICENSE')) -cne (Hash (Join-Path $repo 'LICENSE'))) { throw 'Project licence mismatch.' }
    $materials = Get-Content -LiteralPath (Join-Path $repo 'infra/release/notice-materials.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($material in $materials.materials) {
        if (-not (@($Expected.Keys) -ccontains $material.path) -or $Expected[$material.path] -cne $material.sha256) { throw 'Notice missing or changed.' }
    }
}

if ($Phase -eq 'Verify') {
    $safeRoot = Join-Path $root 'public-safe'
    $verifyRoot = Join-Path $root 'content-verification'
    try {
        Require-ProductSource
        # This branch receives no live configuration. Never upload the full report
        # or candidate while notice/distribution acceptance remains incomplete.
        foreach ($directory in $safeRoot, $verifyRoot) {
            if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
        if ((Get-Item -LiteralPath $reportPath).Length -gt 1048576) { throw 'Report too large.' }
        $existing = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -AsHashtable
        $summary = Public-Summary $existing
        $zip = Join-Path $root "eventhouse-jdbc-0.1.0-rc.1-$Runtime.zip"
        if ((Hash $zip) -cne $existing.package_sha256) { throw 'Archive identity mismatch.' }
        $payload = Join-Path $verifyRoot 'payload'
        New-Item -ItemType Directory -Path $payload | Out-Null
        Verify-Archive $zip $existing.payload_sha256 $payload
        foreach ($pair in @(@('eventhouse-jdbc-0.1.0.jar', $summary.jar_sha256),
            @('lib/jna-5.17.0.jar', $summary.jna_sha256), @("native/$Runtime/eventhouse-engine.dll", $summary.dll_sha256))) {
            if ((Hash (Join-Path $payload $pair[0])) -cne $pair[1]) { throw 'Pair identity mismatch.' }
        }
        $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $safeRoot 'summary.json') -Encoding utf8
        $records = @($existing.payload_sha256.Keys | Sort-Object | ForEach-Object {
            @{ path = $_; sha256 = $existing.payload_sha256[$_] }
        })
        @{ schema_version = 1; files = $records } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $safeRoot 'payload-hashes.json') -Encoding utf8
        $scannerName = "gitleaks_8.30.1_windows_$Architecture.zip"
        $scannerPin = if ($Architecture -eq 'arm64') { 'b95f5e4f5c425cedca7ee203d9afd29597e692c4924a12ed42f970537c72cc0f' }
            else { 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e' }
        $base = 'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/'
        $checksPath = Join-Path $verifyRoot 'official-checksums.txt'
        $scannerZip = Join-Path $verifyRoot 'scanner.zip'
        Invoke-WebRequest ($base + 'gitleaks_8.30.1_checksums.txt') -OutFile $checksPath
        if ((Hash $checksPath) -cne '061476c21adaf5441516f96f185c1a4706a83cd6329b9b38762271b3d4a52fae') { throw 'Scanner checksum identity mismatch.' }
        if (@([IO.File]::ReadAllLines($checksPath) | Where-Object { $_ -ceq "$scannerPin  $scannerName" }).Count -ne 1) { throw 'Scanner official checksum mismatch.' }
        Invoke-WebRequest ($base + $scannerName) -OutFile $scannerZip
        if ((Hash $scannerZip) -cne $scannerPin) { throw 'Scanner archive identity mismatch.' }
        $scannerDirectory = Join-Path $verifyRoot 'scanner'
        Expand-Archive -LiteralPath $scannerZip -DestinationPath $scannerDirectory
        $scanner = Join-Path $scannerDirectory 'gitleaks.exe'
        $version = Invoke-Child $scanner @('version')
        if ($version.Code -ne 0 -or $version.Text.Trim() -cne '8.30.1' -or $version.ErrorText) { throw 'Scanner version mismatch.' }
        $config = Join-Path $verifyRoot 'scanner.toml'
        @'
[extend]
useDefault = true
[[rules]]
id = "deployment-host"
description = "Fabric deployment host is not public evidence"
regex = '''(?i)[a-z0-9][a-z0-9.-]*\.kusto\.fabric\.microsoft\.com'''
[[rules.allowlists]]
regexTarget = "match"
regexes = ['''(?i)^(?:example\.z2|synthetic-test)\.kusto\.fabric\.microsoft\.com$''']
'@ | Set-Content -LiteralPath $config -Encoding utf8
        # Ignore environment scanner overrides inherited from a runner.
        foreach ($name in @([Environment]::GetEnvironmentVariables().Keys | Where-Object { $_ -like 'GITLEAKS_*' })) {
            [Environment]::SetEnvironmentVariable($name, $null)
        }
        foreach ($scanPath in $payload, $safeRoot) {
            $scan = Invoke-Child $scanner @('dir', $scanPath, '--config', $config,
                '--gitleaks-ignore-path', (Join-Path $verifyRoot 'no-ignore'), '--ignore-gitleaks-allow',
                '--redact=100', '--max-archive-depth=4', '--max-decode-depth=5',
                '--no-banner', '--no-color', '--log-level=error', '--timeout=180') 200
            if ($scan.Code -ne 0) { throw 'Content scan failed or incomplete.' }
        }
        if ((Hash $zip) -cne $summary.package_sha256) { throw 'Candidate changed during scan.' }
        $safeHashes = Payload-Hashes $safeRoot
        if ($safeHashes.Count -ne 2) { throw 'Unexpected public file.' }
        if ($env:GITHUB_OUTPUT) { 'content_safe=true' >> $env:GITHUB_OUTPUT }
        Write-Output 'PASS JDBC_CONTENT summary_and_hashes_only candidate_upload=false release_ready=false'
        exit 0
    } catch {
        if (Test-Path -LiteralPath $safeRoot) { Remove-Item -LiteralPath $safeRoot -Recurse -Force }
        Write-Output 'BLOCKED JDBC_CONTENT content_safe=false'
        exit 2
    }
}

# This consumes only this run's report. It cannot turn missing release evidence
# into success, accepts no external gate override and performs no publication.
if ($Phase -eq 'Gate') {
    try {
        Require-ProductSource
        $existing = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -AsHashtable
        $pending = Read-ReleaseGate $existing $ExpectedSourceSha $Runtime $Architecture $env:GITHUB_RUN_ID $env:GITHUB_RUN_ATTEMPT
        if ($pending -gt 0) {
            Write-Output "BLOCKED JDBC_RELEASE pending_gates=$pending release_ready=false"
            exit 2
        }
        # A future independently reviewed release operation must consume the
        # evidence. Qualification never signs, promotes or asserts approval.
        Write-Output 'PASS JDBC_QUALIFICATION release_ready=false'
        exit 0
    } catch {
        Write-Output 'BLOCKED JDBC_RELEASE report=missing_or_invalid release_ready=false'
        exit 2
    }
}

try {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    if ($Runtime -cne "win-$Architecture" -or -not $IsWindows -or
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant() -cne $Architecture -or
        [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() -cne $Architecture) {
        $outcome = 'blocked'; throw 'Native Windows OS/process/runtime architecture required.'
    }
    if ($root.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staging must be outside checkout.'
    }
    $stage = 'source'
    Require-ProductSource
    $report.source_clean = $true
    if ($Phase -eq 'Live') {
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -AsHashtable
        if ($report.source_sha -cne $ExpectedSourceSha -or $report.stage -cne 'build-complete' -or
            $report.runtime -cne $Runtime -or $report.architecture -cne $Architecture) { throw 'Exact architecture build report missing.' }
    }
    $report.phase = $Phase
    $stage = 'architecture-locks'
    $lock = Join-Path $repo "src\engine\packages.$Runtime.lock.json"
    $managedLock = Join-Path $repo "tests\contract\wp003\managed\packages.product.$Runtime.lock.json"
    $report.managed_lock_path = [IO.Path]::GetRelativePath($repo, $managedLock).Replace('\', '/')
    foreach ($requiredLock in $lock, $managedLock) {
        if (-not (Test-Path -LiteralPath $requiredLock -PathType Leaf)) {
            $outcome = 'blocked'
            $report.missing_lock = [IO.Path]::GetRelativePath($repo, $requiredLock).Replace('\', '/')
            throw 'Architecture-specific reviewed lock required. Never generate or reuse another RID lock in CI.'
        }
    }
    $stage = 'installed-jdks'
    $jdks = @{}
    foreach ($major in 21, 25) {
        $jdkHome = [Environment]::GetEnvironmentVariable("EVENTHOUSE_JDK_$major")
        if (-not $jdkHome -or -not (Test-Path -LiteralPath (Join-Path $jdkHome 'bin\java.exe'))) {
            $outcome = 'blocked'
            Write-Output "BLOCKED JDBC missing=EVENTHOUSE_JDK_$major"
            throw 'Explicit native JDK installation required.'
        }
        $java = Join-Path $jdkHome 'bin\java.exe'
        $javaMachine = Require-Pe $java
        $version = Invoke-Child $java @('-XshowSettings:properties', '-version')
        Require-Success $version
        if ($version.ErrorText -cnotmatch "(?m)^\s*java.version = ($major\.[0-9A-Za-z.+-]+)\s*$") { throw 'JDK version mismatch.' }
        $versionText = $Matches[1]
        $javaArch = if ($Architecture -eq 'arm64') { 'aarch64' } else { 'amd64' }
        if ($version.ErrorText -notmatch "(?m)^\s*os.arch = $javaArch\s*$") { throw 'JDK architecture mismatch.' }
        $jdks[$major] = @{ Home = $jdkHome; Java = $java; Version = $versionText }
    }
    $package = Join-Path $root 'package'
    $extracted = Join-Path $root 'extracted'
    $zip = Join-Path $root "eventhouse-jdbc-0.1.0-rc.1-$Runtime.zip"
    $jar = Join-Path $package 'eventhouse-jdbc-0.1.0.jar'
    $jna = Join-Path $package 'lib\jna-5.17.0.jar'
    $dll = Join-Path $package "native\$Runtime\eventhouse-engine.dll"
    $classes = Join-Path $root 'classes'
    $tests = Join-Path $root 'test-classes'
    if ($Phase -eq 'Build') {
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        foreach ($directory in $package, $extracted, (Join-Path $root 'packages'),
            (Join-Path $root 'product-obj'), (Join-Path $root 'product-bin'), (Join-Path $root 'managed-artifacts')) {
            if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $jna), (Split-Path $dll) | Out-Null
        $stage = 'native-toolchain'
        $sdk = Invoke-Child 'dotnet' @('--version')
        Require-Success $sdk
        if ($sdk.Text.Trim() -cne '10.0.113') { throw 'SDK mismatch.' }
        $outcome = 'blocked'
        $sdkInfo = Invoke-Child 'dotnet' @('--info')
        Require-Success $sdkInfo
        if ($sdkInfo.Text -notmatch "(?im)^\s*Architecture:\s+$Architecture\s*$" -or
            $sdkInfo.Text -notmatch "(?im)^\s*RID:\s+$Runtime\s*$") { $outcome = 'blocked'; throw 'Native .NET SDK required.' }
        $dotnetMachine = Require-Pe (Get-Command dotnet.exe -ErrorAction Stop).Source
        $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        $component = if ($Architecture -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
        $vsResult = Invoke-Child $vswhere @('-latest', '-products', '*', '-requires', $component, '-property', 'installationPath')
        Require-Success $vsResult
        $vs = $vsResult.Text.Trim()
        if (-not $vs) { $outcome = 'blocked'; throw 'Required native MSVC target tools absent. No provisioning authorised.' }
        Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll') | Out-Null
        # DevShell accepts amd64 as input; VSCMD_ARG_* reports x64, not that alias.
        $msvcArch = if ($Architecture -eq 'arm64') { 'arm64' } else { 'amd64' }
        Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$msvcArch -host_arch=$msvcArch" *> $null
        if ($env:VSCMD_ARG_HOST_ARCH -ne $Architecture -or $env:VSCMD_ARG_TGT_ARCH -ne $Architecture) {
            $outcome = 'blocked'; throw 'Native MSVC host and target required.'
        }
        $compilerMachine = Require-Pe (Get-Command cl.exe -ErrorAction Stop).Source
        $report.toolchain = @{ dotnet_pe_machine = $dotnetMachine; compiler_pe_machine = $compilerMachine
            msvc_host = $env:VSCMD_ARG_HOST_ARCH; msvc_target = $env:VSCMD_ARG_TGT_ARCH
            msvc = $env:VCToolsVersion; windows_sdk = $env:WindowsSDKVersion; java_pe_machine = $javaMachine }
        $outcome = 'fail'
        $stage = 'managed-regressions'
        $testProject = Join-Path $repo 'tests\contract\wp003\managed\Eventhouse.Engine.Spike.ContractTests.csproj'
        # Restore the engine with its existing lock, then only the managed project
        # with its product-identity lock. Never propagate the managed lock to engine.
        $managedProperties = @("-p:NuGetLockFilePath=$managedLock", '-p:UseArtifactsOutput=true',
            "-p:ArtifactsPath=$root\managed-artifacts", '-p:EventhouseProduct=true')
        $managedLockHash = Hash $managedLock
        Require-Success (Invoke-Child 'dotnet' @('restore', (Join-Path $repo 'src\engine\Eventhouse.Engine.Spike.csproj'),
            '-r', $Runtime, '--locked-mode', "-p:NuGetLockFilePath=$lock", '-p:UseArtifactsOutput=true',
            "-p:ArtifactsPath=$root\managed-artifacts", '-p:EventhouseProduct=true') 420) -BuildCommand restore
        Require-Success (Invoke-Child 'dotnet' (@('restore', $testProject, '-r', $Runtime, '--locked-mode', '--no-dependencies') + $managedProperties) 420) -BuildCommand restore
        $managed = Invoke-Child 'dotnet' (@('run', '--project', $testProject, '-c', 'Release', '-r', $Runtime, '--no-restore') + $managedProperties + @('--', '--streaming')) 420
        $report.managed_tests = Read-ManagedStreamingDiagnostics $managed
        $report.commands += "dotnet restore engine locked existing RID lock; dotnet restore managed --locked-mode --no-dependencies product RID lock; dotnet run managed Release $Runtime EventhouseProduct=true --streaming isolated artifacts"
        Require-Success $managed -BuildCommand managed
        if ($report.managed_tests.outcome -cne 'pass') { throw 'Product streaming 44 evidence incomplete.' }
        $stage = 'locked-native-build'
        $lockHash = Hash $lock
        $project = Join-Path $repo 'src\engine\Eventhouse.Engine.Spike.csproj'
        $properties = @('-p:EventhouseProduct=true', '-p:RestorePackagesWithLockFile=true', "-p:NuGetLockFilePath=$lock",
            "-p:BaseIntermediateOutputPath=$root\product-obj\", "-p:BaseOutputPath=$root\product-bin\",
            "-p:RestorePackagesPath=$root\packages")
        $stage = 'locked-native-restore'
        Require-Success (Invoke-Child 'dotnet' (@('restore', $project, '--runtime', $Runtime, '--locked-mode', '--no-cache', '-p:Configuration=Release') + $properties) 420) -BuildCommand restore
        $stage = 'locked-native-publish'
        # Publish must restore in its own _IsPublishing context to acquire the
        # NativeAOT runtime pack. Standalone restore is not an equivalent graph.
        # Keep locked mode below; --no-restore leaves PrivateSdkAssemblies empty.
        Require-Success (Invoke-Child 'dotnet' (@('publish', $project, '--configuration', 'Release', '--runtime', $Runtime,
            '--self-contained', 'true', '--output', (Split-Path $dll),
            '-p:RestoreLockedMode=true', '-p:TreatWarningsAsErrors=true', '-p:ILLinkTreatWarningsAsErrors=true',
            '-p:IlcTreatWarningsAsErrors=true') + $properties) 600) -BuildCommand publish
        if ((Hash $lock) -cne $lockHash) { throw 'Lock changed.' }
        # Publish folder may contain symbols. Only the actual DLL belongs in the ZIP.
        Get-ChildItem -LiteralPath (Split-Path $dll) -File | Where-Object Name -cne 'eventhouse-engine.dll' | Remove-Item -Force
        $report.commands += "dotnet restore/publish Release $Runtime locked EventhouseProduct=true NativeAOT warnings-as-errors"
        $stage = 'native-exports'
        $report.native_diagnostics = [ordered]@{ dll_sha256 = (Hash $dll)
            exports_exit = $null; expected_exports = 16; parsed_exports = 0
            eh_symbols = @(); export_alias_annotation = $false
            pe_machine = $null; headers_exit = $null; imports_exit = $null
            clr_zero_header = $null; managed_runtime_imports = @(); abi_exit = $null }
        $exports = Invoke-Child 'dumpbin.exe' @('/exports', $dll)
        $report.native_diagnostics.exports_exit = [int] $exports.Code
        # Diagnostic inventory only: acceptance uses declaration rows below.
        # Persist symbol tokens, not dumpbin lines, paths, addresses or annotations.
        $report.native_diagnostics.eh_symbols = @([regex]::Matches($exports.Text,
            '(?m)^\s*\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(eh_[A-Za-z0-9_]+)(?=\s|$)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $report.native_diagnostics.export_alias_annotation = $exports.Text -cmatch
            '(?m)^\s*\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+eh_[A-Za-z0-9_]+\s+='
        Require-Success $exports
        $expected = @('eh_abi_version_v1','eh_open_v1','eh_execute_v1','eh_result_shape_v1',
            'eh_result_column_v1','eh_result_cell_v1','eh_connection_close_v1','eh_result_close_v1',
            'eh_error_code_v1','eh_error_message_v1','eh_operation_prepare_v1','eh_operation_execute_v1',
            'eh_operation_cancel_v1','eh_operation_close_v1','eh_result_next_v1','eh_result_current_cell_v1') | Sort-Object
        # Capture only the declared export, never a symbol in dumpbin's " = ..."
        # annotation. Preserve duplicate rows for the exact count/set check.
        $actual = @([regex]::Matches($exports.Text, '(?m)^[ \t]*\d+[ \t]+[0-9A-F]+[ \t]+[0-9A-F]+[ \t]+(eh_[A-Za-z0-9_]+)(?:[ \t]*\r?$|[ \t]+=[^\r\n]*\r?$)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        $report.native_diagnostics.parsed_exports = $actual.Count
        if ($actual.Count -ne 16 -or (Compare-Object $expected $actual)) { throw 'Product exports mismatch.' }
        $stage = 'native-pe'
        $nativeMachine = Require-Pe $dll
        $report.native_diagnostics.pe_machine = $nativeMachine
        $headers = Invoke-Child 'dumpbin.exe' @('/headers', $dll)
        $report.native_diagnostics.headers_exit = [int] $headers.Code
        $imports = Invoke-Child 'dumpbin.exe' @('/imports', $dll)
        $report.native_diagnostics.imports_exit = [int] $imports.Code
        $report.native_diagnostics.clr_zero_header = $headers.Text -match
            '(?im)^\s*0\s+\[\s*0\]\s+RVA \[size\] of (?:COM Descriptor|CLR Runtime Header)'
        $report.native_diagnostics.managed_runtime_imports = @([regex]::Matches($imports.Text,
            '(?i)\b(?:mscoree|coreclr|clr|hostfxr|hostpolicy)\.dll\b') |
            ForEach-Object { $_.Value.ToLowerInvariant() } | Sort-Object -Unique)
        Require-Success $headers; Require-Success $imports
        if ($headers.Text -notmatch '(?im)^\s*0\s+\[\s*0\]\s+RVA \[size\] of (?:COM Descriptor|CLR Runtime Header)' -or
            $imports.Text -match '(?i)\b(mscoree|coreclr|clr|hostfxr|hostpolicy)\.dll\b') { throw 'Native dependency/header check failed.' }
        $stage = 'native-abi'
        $nativeTest = Invoke-Child 'dotnet' (@('run', '--project', $testProject, '-c', 'Release', '-r', $Runtime, '--no-build', '--no-restore') +
            $managedProperties + @('--', '--native-streaming', $dll)) 120
        $report.native_diagnostics.abi_exit = [int] $nativeTest.Code
        Require-Success $nativeTest
        if ($nativeTest.Text -cnotmatch '(?m)^RESULT pass=1 fail=0 skipped=0 blocked=0\r?$') { throw 'Published ABI10003 test absent.' }
        if ((Hash $managedLock) -cne $managedLockHash -or (Hash $lock) -cne $lockHash) { throw 'Locks changed.' }
        $report.published_native_tests = @{ mode = '--native-streaming'; outcome = 'pass'; pass = 1; fail = 0; skipped = 0; blocked = 0 }
        $report.commands += "dotnet run managed Release $Runtime EventhouseProduct=true --native-streaming actual-published-DLL"
        $report.native_contract = @{ exports = 16; abi = '0x00010003'; pe_machine = $nativeMachine; clr_header = 'absent'; managed_runtime_imports = 'absent'
            clean_machine_proof = 'blocked: hosted runner has .NET installed' }
        $stage = 'jna'
        Invoke-WebRequest -Uri 'https://repo.maven.apache.org/maven2/net/java/dev/jna/jna/5.17.0/jna-5.17.0.jar' -OutFile $jna
        if ((Hash $jna) -cne 'b3a9408e7c51e08ef0e3bfcc08f443f6ec0f6191ba8cd7c18d53d2b22e5bdbc0') { throw 'JNA hash mismatch.' }
        $stage = 'java-build'
        foreach ($directory in $classes, $tests) {
            if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
        $javac = Join-Path $jdks[21].Home 'bin\javac.exe'
        $sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\main\java\com\microsoft\eventhouse\jdbc') -Filter '*.java' | Sort-Object Name | ForEach-Object FullName)
        Require-Success (Invoke-Child $javac (@('--release', '21', '-Xlint:all', '-Werror', '-cp', $jna, '-d', $classes) + $sources)) -BuildCommand javac
        Copy-Item -Path (Join-Path $PSScriptRoot 'src\main\resources\*') -Destination $classes -Recurse -Force
        Require-Success (Invoke-Child (Join-Path $jdks[21].Home 'bin\jar.exe') @('--create', '--date=2026-10-16T00:00:00Z', '--file', $jar, '-C', $classes, '.')) -BuildCommand jar
        $sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\test\java\com\microsoft\eventhouse\jdbc') -Filter '*.java' | Sort-Object Name | ForEach-Object FullName)
        Require-Success (Invoke-Child $javac (@('--release', '21', '-Xlint:all', '-Werror', '-cp', "$jar;$jna", '-d', $tests) + $sources)) -BuildCommand javac
        $stage = 'offline-tests'
        foreach ($major in 21, 25) {
            # Optional native test uses the actual Windows DLL, synthetic credentials, no live query.
            $result = Invoke-Child $jdks[$major].Java @('--enable-native-access=ALL-UNNAMED', '-cp', "$tests;$jar;$jna", 'com.microsoft.eventhouse.jdbc.DriverTest', $dll) 120
            Require-Success $result
            if ($result.Text -cnotmatch '(?m)^DriverTest PASS assertions=([0-9]+) failures=0 \(offline, scripted native boundary\)\r?$') { throw 'Direct test summary absent.' }
            $assertions = [int] $Matches[1]
            if ($assertions -ne 1701 -or $result.Text -cnotmatch '(?m)^NativeSmoke PASS product ABI0x00010003/all16symbols, configured-host open/close, invalid-handle next/current-cell and prepare/cancel/close\. Dummy credentials, no query or network\. assertions=1706\r?$') { throw 'Product native smoke absent.' }
            $report.jvms += @{ major = $major; version = $jdks[$major].Version; architecture = $Architecture; offline_assertions = $assertions; native_total_assertions = 1706; offline_failures = 0; native_smoke = 'pass'; live = 'blocked' }
        }
        $report.sha256 = @{ jar = (Hash $jar); dll = (Hash $dll); jna = (Hash $jna); windows_lock = $lockHash; managed_lock = $managedLockHash }
        $stage = 'dependencies-and-licences'
        $report.dependency_step = 'assets-parse'
        $assetsPath = Join-Path $root 'product-obj\project.assets.json'
        if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
            $outcome = 'blocked'; throw 'Product assets missing.'
        }
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $components = @(); $dependencies = @(); $licencesUnknown = 0
        $dependencyEdges = @{}
        $licenceDirectory = Join-Path $package 'licences'
        $report.dependency_step = 'licence-directory'
        New-Item -ItemType Directory -Force -Path $licenceDirectory | Out-Null
        # Include every resolved package, including build tools, not merely Azure.Identity.
        $resolved = @{}
        $report.dependency_step = 'resolved-packages'
        foreach ($key in $assets.libraries.Keys) {
            if ($assets.libraries[$key].type -eq 'package') { $resolved[$key.ToLowerInvariant()] = $true }
        }
        # NativeAOT/runtime reference packs can be downloadDependencies, outside libraries.
        $report.dependency_step = 'nuspec-enumeration'
        $packagesPath = Join-Path $root 'packages'
        if (-not (Test-Path -LiteralPath $packagesPath -PathType Container)) {
            $outcome = 'blocked'; throw 'Restored packages missing.'
        }
        foreach ($nuspec in Get-ChildItem -LiteralPath $packagesPath -Filter '*.nuspec' -Recurse -File) {
            $report.dependency_step = 'nuspec-parse'
            [xml] $metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
            $id = [string] $metadata.package.metadata.id
            $version = [string] $metadata.package.metadata.version
            $key = "$id/$version".ToLowerInvariant()
            $null = $resolved.Remove($key)
            $ref = "pkg:nuget/$id@$version"
            $component = [ordered]@{ type = 'library'; 'bom-ref' = $ref; name = $id; version = $version; purl = $ref
                properties = @(@{ name = 'eventhouse:scope'; value = 'resolved product restore; includes build-only packages' }) }
            $report.dependency_step = 'package-archive'
            $nupkg = @(Get-ChildItem -LiteralPath $nuspec.DirectoryName -Filter '*.nupkg' -File)
            if ($nupkg.Count -ne 1) { $outcome = 'blocked'; throw 'Exact restored package archive missing.' }
            $component.hashes = @(@{ alg = 'SHA-256'; content = (Hash $nupkg[0].FullName) })
            $report.dependency_step = 'package-licence'
            $licenseNode = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
            if ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'expression') {
                $component.licenses = @(@{ expression = $licenseNode.InnerText })
            } elseif ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'file' -and
                (Test-Path -LiteralPath (Join-Path $nuspec.DirectoryName $licenseNode.InnerText) -PathType Leaf)) {
                $component.licenses = @(@{ license = @{ name = "Package licence file: $($licenseNode.InnerText)" } })
            } else { $licencesUnknown++ }
            $destination = Join-Path $licenceDirectory "$id-$version"
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $destination 'package.nuspec')
            $evidenceFiles = @(Get-ChildItem -LiteralPath $nuspec.DirectoryName -Recurse -File |
                Where-Object { $_.Name -match '(?i)^(licen[cs]e|notice|third.party)' })
            if ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
                if (-not (Test-Path -LiteralPath (Join-Path $nuspec.DirectoryName $licenseNode.InnerText) -PathType Leaf)) {
                    $outcome = 'blocked'; throw 'Declared package licence file missing.'
                }
                $evidenceFiles += Get-Item -LiteralPath (Join-Path $nuspec.DirectoryName $licenseNode.InnerText)
            }
            $report.dependency_step = 'package-licence-copy'
            foreach ($file in $evidenceFiles | Sort-Object FullName -Unique) {
                $relative = [IO.Path]::GetRelativePath($nuspec.DirectoryName, $file.FullName)
                $target = Join-Path $destination $relative
                New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target
            }
            $components += $component
        }
        $report.dependency_step = 'resolved-metadata-closure'
        if ($resolved.Count) { $outcome = 'blocked'; throw 'Resolved NuGet licence metadata missing.' }
        $report.dependency_step = 'dependency-edges'
        foreach ($target in $assets.targets.Values) {
            foreach ($key in $target.Keys) {
                if ($assets.libraries[$key].type -ne 'package') { continue }
                $parts = $key.Split('/')
                $children = @()
                if ($target[$key].ContainsKey('dependencies')) {
                    foreach ($id in $target[$key].dependencies.Keys) {
                        $child = @($target.Keys | Where-Object { $_.StartsWith("$id/", [StringComparison]::OrdinalIgnoreCase) })
                        if ($child.Count -ne 1) { throw 'Ambiguous resolved dependency.' }
                        $children += 'pkg:nuget/' + $child[0].Replace('/', '@')
                    }
                }
                $reference = "pkg:nuget/$($parts[0])@$($parts[1])"
                if (-not $dependencyEdges.ContainsKey($reference)) { $dependencyEdges[$reference] = @() }
                $dependencyEdges[$reference] = @(@($dependencyEdges[$reference]) + $children | Sort-Object -Unique)
            }
        }
        foreach ($reference in $dependencyEdges.Keys) {
            $dependencies += @{ ref = $reference; dependsOn = @($dependencyEdges[$reference]) }
        }
        # JNA licence evidence comes from the exact hash-pinned JAR, not an inferred licence table.
        $report.dependency_step = 'jna-licences'
        $jnaZip = [IO.Compression.ZipFile]::OpenRead($jna)
        try {
            $jnaLicences = @($jnaZip.Entries | Where-Object { $_.FullName -match '(?i)(^|/)(LICENSE|LGPL|AL2\.0|NOTICE)([^/]*)(\.txt)?$' })
            if (-not $jnaLicences.Count) { $licencesUnknown++ }
            foreach ($entry in $jnaLicences) {
                $target = Join-Path $licenceDirectory ('jna-5.17.0/' + $entry.FullName)
                New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally { $jnaZip.Dispose() }
        $components += @{ type = 'library'; 'bom-ref' = 'pkg:maven/net.java.dev.jna/jna@5.17.0'
            name = 'jna'; group = 'net.java.dev.jna'; version = '5.17.0'; purl = 'pkg:maven/net.java.dev.jna/jna@5.17.0'
            hashes = @(@{ alg = 'SHA-256'; content = (Hash $jna) })
            licenses = @(@{ license = @{ name = 'Apache-2.0 elected; see bundled texts' } }) }
        $productRef = 'eventhouse-jdbc-0.1.0-rc.1'
        $jarRef = 'eventhouse-jdbc-jar'
        $dllRef = "eventhouse-engine-$Runtime"
        $jnaRef = 'pkg:maven/net.java.dev.jna/jna@5.17.0'
        # All these hashes come from this build, never a historical candidate.
        $report.dependency_step = 'product-components'
        $components += @{ type = 'library'; 'bom-ref' = $jarRef; name = 'eventhouse-jdbc'; version = '0.1.0-rc.1'
            hashes = @(@{ alg = 'SHA-256'; content = (Hash $jar) }); licenses = @(@{ expression = 'MIT' }) }
        $components += @{ type = 'library'; 'bom-ref' = $dllRef; name = 'eventhouse-engine'; version = '0.1.0-rc.1'
            hashes = @(@{ alg = 'SHA-256'; content = (Hash $dll) }); licenses = @(@{ expression = 'MIT' })
            properties = @(@{ name = 'eventhouse:native-composition'; value = 'NativeAOT runtime and Brotli attribution incomplete; exports are not source provenance' }) }
        $members = @()
        $report.dependency_step = 'jna-members'
        $jnaZip = [IO.Compression.ZipFile]::OpenRead($jna)
        try {
            foreach ($entry in $jnaZip.Entries | Where-Object { $_.FullName -match '\.(dll|so|jnilib|a)$' } | Sort-Object FullName) {
                $inputStream = $entry.Open()
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try { $digest = [Convert]::ToHexString($algorithm.ComputeHash($inputStream)).ToLowerInvariant() }
                finally { $algorithm.Dispose(); $inputStream.Dispose() }
                $memberRef = 'jna-member:' + $entry.FullName
                $members += $memberRef
                $components += @{ type = 'file'; 'bom-ref' = $memberRef; name = $entry.FullName
                    hashes = @(@{ alg = 'SHA-256'; content = $digest })
                    properties = @(@{ name = 'eventhouse:attribution'; value = 'JNA archive member; exact libffi linkage/source version unverified' }) }
                $dependencies += @{ ref = $memberRef; dependsOn = @() }
            }
        } finally { $jnaZip.Dispose() }
        $report.dependency_step = 'component-attribution'
        $components += @{ type = 'library'; 'bom-ref' = 'native-attribution:libffi'; name = 'libffi'
            properties = @(@{ name = 'eventhouse:status'; value = 'Tagged JNA source attribution only; prebuilt member linkage and version unverified' }) }
        $components += @{ type = 'library'; 'bom-ref' = 'native-attribution:brotli'; name = 'Brotli'
            properties = @(@{ name = 'eventhouse:status'; value = 'Native exported symbols observed; exact linked source version unverified' }) }
        $dependencies += @{ ref = 'native-attribution:libffi'; dependsOn = @() }
        $dependencies += @{ ref = 'native-attribution:brotli'; dependsOn = @() }
        $dependencies += @{ ref = $productRef; dependsOn = @($jarRef, $dllRef, $jnaRef) }
        $dependencies += @{ ref = $jarRef; dependsOn = @($jnaRef, $dllRef) }
        $dependencies += @{ ref = $dllRef; dependsOn = @() }
        # Archive containment is a component property, not an invented link edge.
        $jnaComponent = @($components | Where-Object { $_.'bom-ref' -ceq $jnaRef })[0]
        $jnaComponent.licenses = @(@{ expression = 'Apache-2.0' })
        $jnaComponent.properties = @(@{ name = 'eventhouse:archive-members'; value = ($members -join ',') })
        $dependencies += @{ ref = $jnaRef; dependsOn = @() }
        foreach ($component in $components | Where-Object { $_.Contains('purl') -and $_.purl -like 'pkg:nuget/*' }) {
            $id = $component.name
            $scope = if ($id -match 'ILCompiler|ILLink') { 'build-tool' }
                elseif ($id -match 'Microsoft\.(WindowsDesktop|AspNetCore)\.App\.Runtime') { 'download-input; linkage not established' }
                elseif ($id -match 'Microsoft\.NETCore\.App\.Runtime') { 'NativeAOT/runtime attribution input; exact linked composition unverified' }
                else { 'resolved application dependency; trimming/link composition not attested' }
            $component.properties = @(@{ name = 'eventhouse:scope'; value = $scope })
            if ($id -ceq 'System.Security.Cryptography.ProtectedData') { $component.licenses = @(@{ expression = 'MIT' }) }
            if (@($dependencies | Where-Object { $_.ref -ceq $component.'bom-ref' }).Count -eq 0) {
                $dependencies += @{ ref = $component.'bom-ref'; dependsOn = @() }
            }
        }
        $report.dependency_step = 'notice-manifest-parse'
        $noticeManifestPath = Join-Path $repo 'infra\release\notice-materials.json'
        if (-not (Test-Path -LiteralPath $noticeManifestPath -PathType Leaf)) {
            $outcome = 'blocked'; throw 'Notice manifest missing.'
        }
        $noticeManifest = Get-Content -LiteralPath $noticeManifestPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($material in $noticeManifest.materials) {
            $report.dependency_step = 'notice-material-verify'
            $relative = [string] $material.path
            if ($relative -cnotmatch '^licences/[A-Za-z0-9_./-]+$' -or $relative.Contains('..')) { throw 'Invalid notice path.' }
            $source = Join-Path $repo $relative
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                $outcome = 'blocked'; throw 'Vendored notice missing.'
            }
            if ((Hash $source) -cne $material.sha256) { throw 'Vendored notice hash mismatch.' }
            $report.dependency_step = 'notice-material-copy'
            $destination = Join-Path $package $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
        $report.dependency_step = 'notice-manifest-copy'
        Copy-Item -LiteralPath $noticeManifestPath -Destination (Join-Path $licenceDirectory 'notice-materials.json')
        $report.dependency_step = 'sbom-write'
        @{ bomFormat = 'CycloneDX'; specVersion = '1.5'; version = 1
            metadata = @{ component = @{ type = 'application'; 'bom-ref' = $productRef; name = 'eventhouse-jdbc'; version = '0.1.0-rc.1'
                licenses = @(@{ expression = 'MIT' }) }
                properties = @(@{ name = 'eventhouse:coverage'; value = 'Product files, resolved restore inputs and JNA members; native attribution incomplete' }) }
            components = @($components | Sort-Object 'bom-ref')
            dependencies = @($dependencies | Sort-Object ref -Unique) } |
            ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $package 'sbom.cdx.json') -Encoding utf8
        # ProtectedData's exact full bundled text resolves its legacy licence URL.
        $report.dependency_step = 'licence-report'
        $report.licences = @{ components = $components.Count; restore_metadata_unknown = $licencesUnknown
            project_licence = 'MIT'; jna_election = 'Apache-2.0'; native_attribution = 'blocked'
            outcome = 'blocked'; reason = 'Full vendored texts included; WindowsDesktop aggregation and exact native attribution still require independent closure.' }
        $stage = 'dependency-advisories'
        $scan = Invoke-Child 'dotnet' (@('restore', $project, '-r', $Runtime, '--locked-mode', '--force',
            '-p:Configuration=Release', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:NuGetAuditLevel=low') + $properties) 180
        $codes = @([regex]::Matches(($scan.Text + $scan.ErrorText), '\bNU(?:190[0-5]|3040)\b') |
            ForEach-Object Value | Sort-Object -Unique)
        $nugetOutcome = if ($codes -contains 'NU1903' -or $codes -contains 'NU1904') { 'fail' }
            elseif ($scan.Code -eq 0 -and $codes -notcontains 'NU1900' -and $codes -notcontains 'NU1905' -and
                $codes -notcontains 'NU3040') { 'pass' } else { 'blocked' }
        $report.security = @{ dependency_tool = 'dotnet restore / NuGetAudit=true Mode=all Level=low, locked product graph'
            dependency_exit = $scan.Code; diagnostic_codes = $codes; nuget_outcome = $nugetOutcome
            dependency_outcome = $(if ($nugetOutcome -eq 'fail') { 'fail' } else { 'blocked' })
            jna_advisories = 'blocked: no approved Maven advisory scanner result'
            secret_tool = 'not run'; secret_outcome = 'blocked' }
        $report.gates.dependency_scan = $report.security.dependency_outcome
        if ($report.security.dependency_outcome -eq 'fail') { throw 'High/critical advisories block package.' }
        # No repo-wide secret scanner is silently substituted with a few regexes.
        $report.gates.secret_scan = 'blocked: no approved scanner result'
        # Package only reviewed public instructions, never internal source evidence.
        Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $package 'README.md')
        Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $package 'LICENSE')
        Copy-Item -LiteralPath (Join-Path $repo 'SECURITY.md') -Destination (Join-Path $package 'SECURITY.md')
        New-Item -ItemType Directory -Force -Path (Join-Path $package 'docs'), (Join-Path $package 'contracts'), (Join-Path $package 'infra\release') | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo 'docs\BUILD-INSTALL.md') -Destination (Join-Path $package 'docs\BUILD-INSTALL.md')
        foreach ($contractFile in 'eventhouse-engine.h', 'product-abi.md', 'operation-control.md', 'streaming-results.md', 'dynamic-json.md') {
            Copy-Item -LiteralPath (Join-Path $repo "contracts\$contractFile") -Destination (Join-Path $package "contracts\$contractFile")
        }
        Copy-Item -LiteralPath $noticeManifestPath -Destination (Join-Path $package 'infra\release\notice-materials.json')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $package
        $report.commands += 'javac --release 21 -Xlint:all -Werror; fixed-date jar; DriverTest product-native Java21/25; dumpbin exports/headers/imports'
        $provenance = [ordered]@{ source_sha = $ExpectedSourceSha; source_clean = $true; kind = 'jdbc-rc1-build'
            profile = "experimental unsigned 0.1.0-rc.1 / product ABI0x00010003 / $Runtime"
            runtime = $Runtime; architecture = $Architecture; runner = $report.runner; toolchain = $report.toolchain
            sdk = $sdk.Text.Trim(); msvc = $env:VCToolsVersion; windows_sdk = $env:WindowsSDKVersion
            jvms = $report.jvms; commands = $report.commands; lock_sha256 = $lockHash
            payload_sha256 = (Payload-Hashes $package); gates = $report.gates; security = $report.security }
        $provenance | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $package 'provenance.json') -Encoding utf8
        $manifest = Payload-Hashes $package
        @($manifest.Keys | ForEach-Object { "$($manifest[$_])  $_" }) |
            Set-Content -LiteralPath (Join-Path $package 'checksums.sha256') -Encoding utf8
        $stage = 'zip'
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName) {
                $name = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::Parse('2026-10-16T00:00:00Z')
                $stream = $entry.Open(); $inputStream = [IO.File]::OpenRead($file.FullName)
                try { $inputStream.CopyTo($stream) } finally { $inputStream.Dispose(); $stream.Dispose() }
            }
        } finally { $archive.Dispose() }
        Expand-Archive -LiteralPath $zip -DestinationPath $extracted
        $report.package_sha256 = Hash $zip
        $report.payload_sha256 = Payload-Hashes $extracted
        if ((ConvertTo-Json -InputObject (Payload-Hashes $package) -Compress) -cne
            (ConvertTo-Json -InputObject $report.payload_sha256 -Compress)) { throw 'Extraction changed payload.' }
        # Execute the actual extracted payload, not the build-tree DLL/JAR.
        foreach ($major in 21, 25) {
            $result = Invoke-Child $jdks[$major].Java @('--enable-native-access=ALL-UNNAMED', '-cp',
                "$tests;$extracted\eventhouse-jdbc-0.1.0.jar;$extracted\lib\jna-5.17.0.jar",
                'com.microsoft.eventhouse.jdbc.DriverTest', "$extracted\native\$Runtime\eventhouse-engine.dll") 120
            Require-Success $result
            if ($result.Text -cnotmatch '(?m)^DriverTest PASS assertions=1701 failures=0 \(offline, scripted native boundary\)\r?$' -or
                $result.Text -cnotmatch '(?m)^NativeSmoke PASS product ABI0x00010003/all16symbols, configured-host open/close, invalid-handle next/current-cell and prepare/cancel/close\. Dummy credentials, no query or network\. assertions=1706\r?$') { throw 'Extracted tests absent.' }
        }
        $report.route_outcome = 'build-pass-live-blocked'
        $stage = 'build-complete'
        $outcome = 'pass' # Build route only. Release evidence remains incomplete.
    } else {
        $jar = Join-Path $extracted 'eventhouse-jdbc-0.1.0.jar'
        $jna = Join-Path $extracted 'lib\jna-5.17.0.jar'
        $dll = Join-Path $extracted "native\$Runtime\eventhouse-engine.dll"
        $stage = 'verify-before-live'
        if ((Hash $zip) -cne $report.package_sha256 -or
            (ConvertTo-Json -InputObject (Payload-Hashes $extracted) -Compress) -cne
            (ConvertTo-Json -InputObject $report.payload_sha256 -Compress)) { throw 'Package changed.' }
        if ((Hash $jar) -cne $report.sha256.jar -or (Hash $dll) -cne $report.sha256.dll -or
            (Hash $jna) -cne $report.sha256.jna -or (Hash $lock) -cne $report.sha256.windows_lock -or
            (Hash $managedLock) -cne $report.sha256.managed_lock) { throw 'Hashes changed.' }
        $stage = 'secret-presence'
        foreach ($name in $secretNames) {
            $report.secret_presence[$name] = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))
        }
        if ($report.secret_presence.Values -contains $false) { $outcome = 'blocked'; throw 'Missing inherited variable. Owner must confirm database and approved runner route.' }
        $env:EVENTHOUSE_NATIVE_LIBRARY = $dll
        $outcome = 'fail'
        $stage = 'live-transactions'
        foreach ($major in 21, 25) {
            $stage = 'live-transactions'
            $result = Invoke-Child $jdks[$major].Java @('--enable-native-access=ALL-UNNAMED', '-cp', "$tests;$jar;$jna", 'com.microsoft.eventhouse.jdbc.TransactionsSmoke') 420 -Live
            if ($result.Code -ne 0) {
                $report.live_failure = @{ caller = 'TransactionsSmoke'; child_exit = [int] $result.Code
                    classification = 'unclassified-retained-failure' }
                throw 'Live child failed. Raw output suppressed.'
            }
            if ($result.ErrorText -or $result.Text -cnotmatch '\APASS WP005Q_UNRESTRICTED rows=([1-9][0-9]{0,18}) columns=([1-9][0-9]{0,9}) null_cells=([0-9]{1,19}) returned_utf8_bytes=([0-9]{1,19})\r?\n\z') { throw 'Validated unrestricted caller summary absent.' }
            $rows = [long] $Matches[1]; $columns = [int] $Matches[2]; $nulls = [long] $Matches[3]; $bytes = [long] $Matches[4]
            if ($bytes -le 4194304 -or $columns -gt 4096 -or [decimal] $nulls -gt ([decimal] $rows * $columns)) { throw 'Unrestricted caller counters invalid.' }
            $entry = @($report.jvms | Where-Object { $_.major -eq $major })[0]
            if ($entry.version -cne $jdks[$major].Version) { throw 'JVM changed.' }
            $entry.live = 'pass'; $entry.rows = $rows; $entry.columns = $columns; $entry.null_cells = $nulls
            $entry.returned_utf8_bytes = $bytes; $entry.validated_eof = $true
            $entry.operation_deadline_seconds = 300; $entry.outer_deadline_seconds = 420
            $stage = 'live-rc'
            $rc = Invoke-Child $jdks[$major].Java @('--enable-native-access=ALL-UNNAMED', '-cp', "$tests;$jar;$jna", 'com.microsoft.eventhouse.jdbc.RcSmoke', '--live-failures') 420 -Live
            $entry.rc_retained = Read-RcDiagnostics $rc
            if ($entry.rc_retained.outcome -cne 'pass') {
                $outcome = $entry.rc_retained.outcome
                throw 'Retained RC cases failed or blocked. Raw output suppressed.'
            }
            $entry.rc_assertions = 45; $entry.rc_smoke = 'pass'
        }
        $report.gates.live_inflight_cancel = 'pass: local cursor termination and same-connection recovery'
        $report.gates.live_inflight_timeout = 'pass: idle cursor deadline and same-connection recovery'
        $report.gates.live_auth_failure_refresh = 'pass: invalid-secret/reconnect only; transparent refresh not qualified'
        $stage = 'verify-binaries-and-source'
        if ((Hash $jar) -cne $report.sha256.jar -or (Hash $dll) -cne $report.sha256.dll -or
            (Hash $jna) -cne $report.sha256.jna -or (Hash $lock) -cne $report.sha256.windows_lock -or
            (Hash $managedLock) -cne $report.sha256.managed_lock) { throw 'Hashes changed.' }
        Require-ProductSource
        if ((Hash $zip) -cne $report.package_sha256 -or
            (ConvertTo-Json -InputObject (Payload-Hashes $extracted) -Compress) -cne
            (ConvertTo-Json -InputObject $report.payload_sha256 -Compress)) { throw 'Payload changed during execution.' }
        $report.route_outcome = 'pass' # This architecture's bounded tests only, never full RC approval.
        $stage = 'complete'; $outcome = 'pass' # Live route only. Never promotes a release.
    }
} catch {
    # Intentionally discard exception text and all native/compiler stderr.
    $report.route_outcome = $outcome
    if ($stage -ceq 'dependencies-and-licences') {
        # Positive allowlist only. Never print exception IDs, lines, paths or package metadata.
        $reason = 'unclassified'
        if ($report.Contains('dependency_step') -and $report.dependency_step -cin @(
            'assets-parse', 'licence-directory', 'resolved-packages', 'nuspec-enumeration',
            'nuspec-parse', 'package-archive', 'package-licence', 'package-licence-copy',
            'resolved-metadata-closure', 'dependency-edges', 'jna-licences', 'product-components',
            'jna-members', 'component-attribution', 'notice-manifest-parse', 'notice-material-verify',
            'notice-material-copy', 'notice-manifest-copy', 'sbom-write', 'licence-report')) {
            $reason = $report.dependency_step
        }
        Write-Output "$($outcome.ToUpperInvariant()) JDBC_DEPENDENCIES reason=$reason"
    }
    Write-Output "$($outcome.ToUpperInvariant()) JDBC runtime=$Runtime stage=$stage"
    $report.outcome = $outcome; $report.stage = $stage
    if (Test-Path -LiteralPath $root) { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8 }
    exit 1
}
$report.outcome = $outcome; $report.stage = $stage
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output "PASS JDBC runtime=$Runtime phase=$Phase stage=$stage release_ready=false"
exit 0
