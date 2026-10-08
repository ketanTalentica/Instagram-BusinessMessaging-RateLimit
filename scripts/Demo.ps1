<#
    RateLimit demo console - interactive runner for every use case in docs/DEMO.md.

    Starts all four services against the LOCAL MOCK ONLY, then drives each demo, reads the
    service logs back and prints PASS/FAIL per expected line. Nothing here can reach the real
    Graph API: Assert-MockOnly refuses to start unless the sender's base URL is localhost:5020,
    and Test-Wiring proves the mock actually received a call before any demo runs.

    Usage (normally via run-all.bat):
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Demo.ps1
        ... -SkipStart          attach to services that are already running
        ... -Run 5              run one demo with defaults and exit
        ... -Run outbound       run demos 1-8 back to back with defaults and exit

    Windows PowerShell 5.1 compatible: ASCII only (5.1 reads BOM-less .ps1 as ANSI and would
    mangle box-drawing characters), no ternary, no ??, no utf8NoBOM. Native commands are called
    with stderr discarded rather than 2>&1, which in 5.1 turns exe stderr into terminating errors.
#>
[CmdletBinding()]
param(
    [switch]$SkipStart,
    [switch]$NoLogWindow,
    [string]$Run
)

$ErrorActionPreference = 'Continue'   # native exes here signal failure via exit codes, not errors

$Root    = Split-Path $PSScriptRoot -Parent
$LogDir  = Join-Path $Root 'logs'
$TempDir = Join-Path $env:TEMP 'ratelimit-demo'

$MockUrl   = 'http://localhost:5020'
$SenderUrl = 'http://localhost:5001'
$IngestUrl = 'http://localhost:5002'
$SimUrl    = 'http://localhost:5010'

$DbServer = '(localdb)\mssqllocaldb'
$DbName   = 'SenderDB'

# Ports come from each project's appsettings "Urls" - launch profiles are deliberately not used.
$Services = [ordered]@{
    mock   = @{ Name = 'InstagramGraphMock';      Port = 5020; Label = 'Graph API mock' }
    sender = @{ Name = 'InstagramSenderApi';      Port = 5001; Label = 'Sender API' }
    ingest = @{ Name = 'WebhookIngestApi';        Port = 5002; Label = 'Webhook ingest' }
    sim    = @{ Name = 'WebhookTrafficSimulator'; Port = 5010; Label = 'Traffic simulator' }
}

$Actors = @{
    Sender   = 'InstagramSenderApi     - OUR app that sends to Instagram      (:5001)'
    Graph    = 'InstagramGraphMock     - stands in for Instagram              (:5020)'
    Attacker = 'WebhookTrafficSimulator- stands in for Instagram / attackers  (:5010)'
    Ingest   = 'WebhookIngestApi       - OUR public webhook endpoint          (:5002)'
}

# One entry per use case, keyed by the id used in docs/DEMO.md. Plain = layman briefing shown
# before the demo runs; Note = the technical detail that makes the demo meaningful.
$DemoCatalog = [ordered]@{
    '2.1' = @{
        Fn    = 'Demo-1-PerSecondGate'
        Title = 'We pace ourselves so Instagram never has to say no'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('Instagram allows only so many messages per second.',
                  'Our app puts its own brake on first, so it never trips that limit.')
        Note  = 'An empty payload on /messages is the TEXT class, so PerSecondDispatchLimit is the cap in force.'
    }
    '2.2' = @{
        Fn    = 'Demo-2-RetryAfter429'
        Title = 'Instagram says "wait 90 seconds" and we actually wait'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('Instagram refuses the call and puts the wait time in a header.',
                  'The error code is meaningless here - only the header tells us to back off.')
        Note  = 'Body carries Meta code 1 ("API Unknown"), NOT a rate-limit code, so the header is the only signal.'
    }
    '2.3' = @{
        Fn    = 'Demo-3-SharedAppBudget'
        Title = 'One quota is shared by every account we manage'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('One busy account eats most of the quota that ALL accounts share.',
                  'A brand-new account is slowed down on its very first call because of it.')
        Note  = 'App % and per-account % are stored on separate rows and never merged; the guard uses max(both).'
    }
    '2.4' = @{
        Fn    = 'Demo-4-Classic'
        Title = 'Classic scenarios - watch only, nothing asserted'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('Pick a scenario and watch the log: quota creeping up, a sudden ban,',
                  'a ban that flickers on and off, several accounts at once, or recovery.')
        Note  = 'Observation demo - use it to narrate behaviour, not to prove a specific line.'
    }
    '2.5' = @{
        Fn    = 'Demo-5-BucBlock'
        Title = 'One Instagram account has used up its own quota'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('Instagram blocks ONE account for 8 minutes.',
                  'Only that account stops. Every other account keeps sending.')
        Note  = 'HTTP 400 with no Retry-After: only the recognised-code list can classify error 80002.'
    }
    '2.6' = @{
        Fn    = 'Demo-6-Custom613'
        Title = 'Instagram dislikes our traffic pattern and gives no wait time'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('Instagram flags the app, and refuses to say how long to wait.',
                  'We pick 15 minutes ourselves - retrying in 1 minute walks straight back in.')
        Note  = 'Error 613 / subcode 1996 arrives with no estimated_time_to_regain_access. App level.'
    }
    '2.7' = @{
        Fn    = 'Demo-7-AppLevelBlock'
        Title = 'Our whole app is banned - EVERY account must stop'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('One account gets the error, but the ban covers the whole app.',
                  'An unrelated, perfectly healthy account is stopped before it even dials out.')
        Note  = 'Error 4 must land on the shared app row, not on the account that happened to receive it.'
    }
    '2.8' = @{
        Fn    = 'Demo-8-DispatchClasses'
        Title = 'Video is capped 10x tighter than text - same endpoint'
        Dir   = 'OUTBOUND - we call Instagram'
        From  = $Actors.Sender; To = $Actors.Graph
        Plain = @('Text messages: 100 per second. Video: only 10. Chat lists: only 2.',
                  'Same address for all of them - the content decides which limit applies.')
        Note  = 'DispatchClassifier reads the payload attachment type; the gate runs before the HTTP call.'
    }
    '3.1' = @{
        Fn    = 'Demo-9-InboundScenario'
        Title = 'Attack traffic slams our public webhook'
        Dir   = 'INBOUND - someone calls US'
        From  = $Actors.Attacker; To = $Actors.Ingest
        Plain = @('Floods, fake signatures, huge payloads, slow-drip connections.',
                  'Our gate accepts a fair share and rejects the rest with a "try later".')
        Note  = 'Enforce mode. The accepted count varies with window timing - judge the shape, not the exact split.'
    }
    '3.2' = @{
        Fn    = 'Demo-10-BypassRules'
        Title = 'Health checks and Meta handshakes must never be blocked'
        Dir   = 'INBOUND - someone calls US'
        From  = 'curl (stands in for a monitor / for Meta)'
        To    = $Actors.Ingest
        Plain = @('Two things always get through, even mid-attack: our /health check,',
                  'and any GET - because Meta verifies the webhook with a GET.')
        Note  = 'Blocking the GET handshake would break webhook verification with Meta.'
    }
    '3.3' = @{
        Fn    = 'Demo-11-ObserveOnly'
        Title = 'Watch-only mode - log what we WOULD block, block nothing'
        Dir   = 'INBOUND - someone calls US'
        From  = $Actors.Attacker; To = $Actors.Ingest
        Plain = @('Before switching enforcement on in production, run it silently for a week.',
                  'Nothing is rejected; every would-be rejection is written to the log.')
        Note  = 'Rollout step P1 - this is how real thresholds get chosen from real traffic.'
    }
}

$script:QuitRequested    = $false
$script:ServiceEnvState  = @{}    # service key -> env override currently running, as a label
$script:ServiceEnvValues = @{}    # service key -> the hashtable itself, to relaunch identically
$script:Auto            = $false  # true = take every default, never prompt
$script:Results         = @()     # accumulated PASS/FAIL rows for the summary
$script:GraphVersion    = 'v25.0' # replaced by Assert-MockOnly from live config
$script:HasSqlCmd       = $false

# ------------------------------------------- output helpers ----------------------------------

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
}
function Write-Step([string]$Text) { Write-Host "-> $Text" -ForegroundColor White }
function Write-Ok  ([string]$Text) { Write-Host "  OK   $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "  WARN $Text" -ForegroundColor Yellow }
function Write-Err ([string]$Text) { Write-Host "  FAIL $Text" -ForegroundColor Red }
function Write-Dim ([string]$Text) { Write-Host "       $Text" -ForegroundColor DarkGray }

function Ask {
    param([string]$Prompt, $Default)
    $d = [string]$Default
    if ($script:Auto) { Write-Dim "$Prompt = $d"; return $d }
    $answer = Read-Host "  $Prompt [$d]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $d }
    return $answer.Trim()
}

function Ask-Int {
    param([string]$Prompt, [int]$Default)
    $v = Ask $Prompt $Default
    $parsed = 0
    if ([int]::TryParse($v, [ref]$parsed)) { return $parsed }
    Write-Warn "'$v' is not a number - using $Default"
    return $Default
}

function Pause-Menu {
    if ($script:Auto) { return }
    Write-Host ''
    Read-Host '  press Enter to return to the menu' | Out-Null
}

# ------------------------------------------- briefing + navigation ---------------------------

# Shown before every run so anyone watching knows what is about to happen, in which direction,
# and which service is calling which.
function Show-Briefing([string]$Id) {
    $d = $DemoCatalog[$Id]
    Write-Host ''
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  USE CASE $Id - $($d.Title)" -ForegroundColor Cyan
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  Direction : $($d.Dir)" -ForegroundColor White
    Write-Host "  Sender    : $($d.From)" -ForegroundColor Gray
    Write-Host "  Receiver  : $($d.To)" -ForegroundColor Gray
    Write-Host '  In plain words:' -ForegroundColor White
    foreach ($line in $d.Plain) { Write-Host "     $line" -ForegroundColor Gray }
    if ($d.Note) { Write-Host "  Detail    : $($d.Note)" -ForegroundColor DarkGray }
    Write-Host ''
}

# Accepts a use case id ('2.5') or a menu ordinal ('5'). Returns the id, or $null.
# Param is not named $Input - that is an automatic variable in PowerShell.
function Resolve-DemoId([string]$Choice) {
    $value = $Choice.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    if ($DemoCatalog.Contains($value)) { return $value }

    $ordinal = 0
    if ([int]::TryParse($value, [ref]$ordinal)) {
        $ids = @($DemoCatalog.Keys)
        if ($ordinal -ge 1 -and $ordinal -le $ids.Count) { return $ids[$ordinal - 1] }
    }
    return $null
}

function Get-NextDemoId([string]$Id) {
    $ids = @($DemoCatalog.Keys)
    $i   = [array]::IndexOf($ids, $Id)
    if ($i -ge 0 -and $i -lt ($ids.Count - 1)) { return $ids[$i + 1] }
    return $null
}

# Asked after every demo: chain to the next one, jump to a specific use case, or stop.
function Prompt-NextDemo([string]$CurrentId) {
    if ($script:Auto) { return $null }

    $next = Get-NextDemoId $CurrentId
    Write-Host ''
    Write-Host ('-' * 74) -ForegroundColor DarkGray
    if ($next) {
        Write-Host "  Next use case: $next - $($DemoCatalog[$next].Title)" -ForegroundColor Yellow
        Write-Host '  [Enter] run it   |   or type a use case id (2.1 - 3.3)   |   m = menu   |   q = quit' -ForegroundColor DarkGray
    } else {
        Write-Host '  That was the last use case.' -ForegroundColor Yellow
        Write-Host '  Type a use case id (2.1 - 3.3)   |   m = menu   |   q = quit' -ForegroundColor DarkGray
    }

    $answer = Read-Host '  choice'
    if ([string]::IsNullOrWhiteSpace($answer)) { return $next }   # Enter = the immediate next one

    switch ($answer.Trim().ToUpper()) {
        'M' { return $null }
        'Q' { $script:QuitRequested = $true; return $null }
    }

    $picked = Resolve-DemoId $answer
    if (-not $picked) { Write-Warn "'$answer' is not a use case id - back to the menu"; return $null }
    return $picked
}

# Runs one use case, then keeps going for as long as the operator asks for another.
function Invoke-DemoChain([string]$StartId) {
    $id = $StartId
    while ($id) {
        try { & $DemoCatalog[$id].Fn }
        catch {
            Write-Err "use case $id could not run: $($_.Exception.Message)"
            return   # back to the menu rather than chaining on top of a broken state
        }
        $id = Prompt-NextDemo $id
    }
}

# ------------------------------------------- infrastructure ----------------------------------

function Log-Path([string]$Key) { Join-Path $LogDir "$Key.log" }

# Services hold their log file open, so a plain Get-Content can hit a sharing violation.
function Read-LogLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object System.IO.StreamReader($stream)
        return ($reader.ReadToEnd() -split "`r?`n")
    } catch { return @() }
    finally { if ($stream) { $stream.Dispose() } }
}

function Get-LogMark([string]$Key) { return @(Read-LogLines (Log-Path $Key)).Count }

function Get-LogSince([string]$Key, [int]$Mark) {
    $all = @(Read-LogLines (Log-Path $Key))
    if ($all.Count -le $Mark) { return @() }
    return $all[$Mark..($all.Count - 1)]
}

function Test-Port([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try   { $client.Connect('127.0.0.1', $Port); return $true }
    catch { return $false }
    finally { $client.Close() }
}

function Wait-Port([int]$Port, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Port $Port) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Wait-PortClosed([int]$Port, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Port $Port)) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# taskkill is asynchronous, so waiting for the port to actually close matters: starting a
# replacement while the old process still holds the port makes the new one fail to bind, and
# Wait-Port would then happily confirm the STALE process instead.
# Returns $true only when the service is genuinely gone. A silent failure here is the worst
# outcome available: demos would run against a stale process that still holds queue state and
# writes to a log file we no longer read, and every assertion would lie.
function Stop-OneService([string]$Key) {
    $name = $Services[$Key].Name
    $port = $Services[$Key].Port

    # cmd keeps taskkill's stderr out of PowerShell, where 2>&1 on a native exe becomes a
    # NativeCommandError in Windows PowerShell.
    $out  = & cmd /c "taskkill /F /IM ""$name.exe"" 2>&1"
    $code = $LASTEXITCODE

    # 0 = terminated, 128 = was not running. Anything else is a real failure ("Access is denied"
    # when the process belongs to another session, for instance).
    $killFailed = ($code -ne 0 -and $code -ne 128)
    if ($killFailed) {
        Write-Err "taskkill could not stop $name (exit $code): $(($out | Where-Object { $_ -match '\S' }) -join ' ')"
    }
    if (-not (Wait-PortClosed $port)) {
        Write-Err "port $port is still in use - $name (or a stale copy) is still running"
        return $false
    }
    # A free port does NOT redeem a failed kill: a survivor listening elsewhere (an old run on a
    # different port, say) still holds a lock on its own binary, and the build dies with MSB3021.
    if ($killFailed) {
        Write-Err "$name is still running even though :$port is free - it will lock its own binary"
        return $false
    }
    return $true
}

function Stop-AllServices {
    Write-Step 'Stopping services (this also releases the build outputs)'
    $stuck = @()
    foreach ($key in $Services.Keys) {
        if (-not (Stop-OneService $key)) { $stuck += $Services[$key].Name }
    }
    $script:ServiceEnvState = @{}
    if ($stuck.Count -gt 0) {
        Write-Err "still running: $($stuck -join ', ')"
        Write-Warn 'end those processes (Task Manager, or stop-all.bat from an elevated prompt) before demoing'
        return $false
    }
    Write-Ok 'all stopped'
    return $true
}

function Invoke-Build {
    Write-Step 'Building RateLimit.sln'
    # Services must be down first: a running exe locks its own binary and the build fails MSB3021.
    $output = & dotnet build (Join-Path $Root 'RateLimit.sln') --nologo -v m
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 25 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        throw 'Build failed.'
    }
    $warnings = @($output | Where-Object { $_ -match ':\s*warning\s' }).Count
    Write-Ok "build succeeded ($warnings warnings)"
}

function Start-OneService {
    param([string]$Key, [hashtable]$EnvOverrides)

    $svc = $Services[$Key]
    $exe = Join-Path $Root ($svc.Name + '\bin\Debug\net9.0\' + $svc.Name + '.exe')
    if (-not (Test-Path -LiteralPath $exe)) { throw "Not built: $exe" }

    # The port must be free BEFORE launching. Otherwise the new process loses the bind, exits, and
    # the port check below would confirm the stale owner instead.
    if (Test-Port $svc.Port) {
        throw "$($svc.Label): port $($svc.Port) is already in use. End the process holding it (stop-all.bat) and retry."
    }

    $outLog = Log-Path $Key
    $errLog = Join-Path $LogDir "$Key.err.log"
    foreach ($f in @($outLog, $errLog)) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
    }

    # Child processes inherit this process's environment block, so set the overrides here,
    # launch, then restore - that keeps one demo's override from leaking into the next.
    $desired = @{ 'ASPNETCORE_ENVIRONMENT' = 'Development' }
    if ($EnvOverrides) { foreach ($e in $EnvOverrides.GetEnumerator()) { $desired[$e.Name] = [string]$e.Value } }

    $script:ServiceEnvValues[$Key] = $EnvOverrides   # remembered so a reset can rebuild identically

    $saved = @{}
    foreach ($k in $desired.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $desired[$k])
    }
    $proc = $null
    try {
        $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) `
            -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden -PassThru
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }

    if (-not (Wait-Port $svc.Port)) {
        Write-Err "$($svc.Label) did not open port $($svc.Port) - last log lines:"
        Get-LogSince $Key 0 | Select-Object -Last 12 | ForEach-Object { Write-Dim $_ }
        Read-LogLines $errLog | Select-Object -Last 6 | ForEach-Object { Write-Dim $_ }
        throw "$($svc.Name) failed to start."
    }

    # An open port is not proof OUR process owns it. If the one just launched has already exited,
    # the port belongs to something else and every demo would be testing a stale binary.
    Start-Sleep -Milliseconds 400
    if ($proc -and $proc.HasExited) {
        Write-Err "$($svc.Name) exited immediately (code $($proc.ExitCode)) yet :$($svc.Port) answers - a stale process owns that port"
        Read-LogLines $errLog | Select-Object -Last 6 | ForEach-Object { Write-Dim $_ }
        throw "$($svc.Name) is not the process serving :$($svc.Port)."
    }
    Write-Ok "$($svc.Label) on :$($svc.Port)  -> logs\$Key.log"
}

function Format-EnvState([hashtable]$EnvOverrides) {
    if (-not $EnvOverrides -or $EnvOverrides.Count -eq 0) { return '(defaults)' }
    $parts = $EnvOverrides.GetEnumerator() | Sort-Object Name | ForEach-Object {
        $shortName = ($_.Name -split '__')[-1]
        "$shortName=$($_.Value)"
    }
    return ($parts -join ' ')
}

# Restart a service only when a demo needs different settings than what is already running.
function Ensure-ServiceEnv {
    param([string]$Key, [hashtable]$EnvOverrides)
    $desired = Format-EnvState $EnvOverrides
    if ($script:ServiceEnvState[$Key] -eq $desired) { return }
    Write-Step "Restarting $Key with $desired"
    if (-not (Stop-OneService $Key)) {
        throw "Cannot restart $Key - the old process is still running, so this demo would test stale state."
    }
    Start-OneService $Key $EnvOverrides
    $script:ServiceEnvState[$Key] = $desired
}

# ------------------------------------------- safety checks -----------------------------------

function Assert-MockOnly {
    $devFile = Join-Path $Root 'InstagramSenderApi\appsettings.Development.json'
    $url     = (Get-Content -LiteralPath $devFile -Raw | ConvertFrom-Json).Instagram.GraphApiBaseUrl

    # An inherited env var outranks appsettings, so it has to be checked too.
    $envUrl = [Environment]::GetEnvironmentVariable('Instagram__GraphApiBaseUrl')
    if ($envUrl) {
        Write-Warn "Instagram__GraphApiBaseUrl is set in this shell: $envUrl"
        $url = $envUrl
    }
    if ($url -notmatch '^http://(localhost|127\.0\.0\.1):5020/') {
        throw "SAFETY STOP: sender would call '$url'. These demos run against the local mock only."
    }

    $script:GraphVersion = ([uri]$url).AbsolutePath.Trim('/')
    Write-Ok "mock-only confirmed: sender -> $url (Graph version $script:GraphVersion)"
}

# Config being right is not proof the call landed. Send one probe and read it back off the mock.
function Test-Wiring {
    $tenant = 'preflight-probe'

    # Clear state first, or a leftover app row at 100% delays the probe by 60 s and this check
    # reports a wiring failure that isn't one. No cache wait needed: the sender just started, so
    # its 10 s state cache is still cold and it will read the emptied table.
    & curl.exe -s -m 10 -o NUL -X POST "$MockUrl/simulator/reset" 2>$null
    $file = Write-TempJson 'appusage.json' '{"pct":null}'
    & curl.exe -s -m 10 -o NUL -X POST "$MockUrl/simulator/app-usage" `
               -H 'Content-Type: application/json' --data "@$file" 2>$null
    Invoke-Sql 'DELETE FROM TenantRateLimitState;' | Out-Null

    Send-Job $tenant (Endpoint-For $tenant) | Out-Null

    $seen = $false
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep -Seconds 1
        $state = & curl.exe -s -m 10 "$MockUrl/simulator/state/$tenant" 2>$null
        if ($state -match 'callCountPct') { $seen = $true; break }
    }
    if ($seen) { Write-Ok 'sender -> mock verified (the mock recorded the probe call)' }
    else { Write-Err 'probe never reached the mock - check logs\sender.log before demoing' }

    & curl.exe -s -m 10 -o NUL -X POST "$MockUrl/simulator/reset/$tenant" 2>$null
    Invoke-Sql "DELETE FROM TenantRateLimitState WHERE TenantId = '$tenant';" | Out-Null
}

# ------------------------------------------- SQL + HTTP --------------------------------------

function Invoke-Sql([string]$Query) {
    if (-not $script:HasSqlCmd) { return $null }
    return (& sqlcmd -S $DbServer -d $DbName -Q $Query -W 2>$null)
}

function Show-StateRows {
    if (-not $script:HasSqlCmd) { Write-Warn 'sqlcmd not found - skipping the SQL row check'; return }
    Write-Host '  TenantRateLimitState:' -ForegroundColor White
    $rows = Invoke-Sql 'SELECT TenantId, MaxCallCountPct, BlockedUntilUtc FROM TenantRateLimitState ORDER BY TenantId;'
    $rows | Where-Object { $_ -and $_ -notmatch '^-+$' } | ForEach-Object { Write-Dim $_ }
}

function Get-RowCount {
    if (-not $script:HasSqlCmd) { return '?' }
    $out = Invoke-Sql 'SET NOCOUNT ON; SELECT COUNT(*) FROM TenantRateLimitState;'
    $hit = $out | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1
    if ($hit) { return $hit.Trim() }
    return '?'
}

function Endpoint-For([string]$Tenant, [string]$Resource = 'messages') {
    return '/' + $script:GraphVersion + '/' + $Tenant + '/' + $Resource
}

function Write-TempJson([string]$Name, [string]$Json) {
    if (-not (Test-Path -LiteralPath $TempDir)) { New-Item -ItemType Directory -Path $TempDir | Out-Null }
    $path = Join-Path $TempDir $Name
    [System.IO.File]::WriteAllText($path, $Json, (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

function Send-Job {
    param([string]$Tenant, [string]$Endpoint, [string]$PayloadJson = '{}')
    $body = '{"tenantId":"' + $Tenant + '","targetEndpoint":"' + $Endpoint + '","payload":' + $PayloadJson + '}'
    $file = Write-TempJson 'send.json' $body
    $code = & curl.exe -s -m 20 -o NUL -w '%{http_code}' -X POST "$SenderUrl/send" `
                 -H 'Content-Type: application/json' --data "@$file" 2>$null
    # Without this, a rejected or unreachable /send shows up much later as an unexplained
    # "missing expected log line" instead of the actual problem.
    if ("$code" -ne '202') { Write-Warn "POST /send for $Tenant returned HTTP $code (expected 202)" }
    return $code
}

# One curl process with repeated --next: a ForEach-Object { curl } loop spends tens of ms per
# process and can never fill a per-second bucket.
function Send-Burst {
    param([int]$Count, [string]$Tenant, [string]$Endpoint, [string]$PayloadJson = '{}')
    $body = '{"tenantId":"' + $Tenant + '","targetEndpoint":"' + $Endpoint + '","payload":' + $PayloadJson + '}'
    $file = Write-TempJson "burst-$Tenant.json" $body

    $argv = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Count; $i++) {
        if ($i -gt 0) { $argv.Add('--next') }
        $argv.AddRange([string[]]@('-s', '-o', 'NUL', '-w', '%{http_code} ', '-X', 'POST',
                                   "$SenderUrl/send", '-H', 'Content-Type: application/json',
                                   '--data', "@$file"))
    }
    $sw  = [Diagnostics.Stopwatch]::StartNew()
    $out = & curl.exe @($argv.ToArray()) 2>$null
    $sw.Stop()

    $codes = @(($out -join ' ') -split '\s+' | Where-Object { $_ -match '\d' })
    $bad   = @($codes | Where-Object { $_ -ne '202' })
    Write-Dim "$Count requests enqueued in $($sw.ElapsedMilliseconds) ms"
    if ($bad.Count -gt 0) {
        Write-Warn "$($bad.Count) of $Count sends were not accepted (codes: $(($bad | Select-Object -Unique) -join ', '))"
    }
}

function Set-Scenario([hashtable]$Body) {
    $json = ($Body | ConvertTo-Json -Compress)
    $file = Write-TempJson 'scenario.json' $json
    & curl.exe -s -m 10 -o NUL -X POST "$MockUrl/simulator/scenario" `
               -H 'Content-Type: application/json' --data "@$file" 2>$null
    Write-Dim "scenario applied: $json"
}

function Show-MockState([string]$Tenant) {
    $raw = & curl.exe -s -m 10 "$MockUrl/simulator/state/$Tenant" 2>$null
    if (-not $raw) { Write-Dim "mock has no state for $Tenant"; return }
    $s = $raw | ConvertFrom-Json
    Write-Dim ("mock $Tenant -> isBlocked=$($s.isBlocked) callCountPct=$($s.callCountPct) " +
               "errorCode=$($s.returnErrorCode) subcode=$($s.returnErrorSubcode)")
}

# ------------------------------------------- reset -------------------------------------------

# Three kinds of state have to go, and only two of them live in a store:
#   1. the mock's per-tenant state and its shared X-App-Usage override
#   2. the SQL rows
#   3. the sender's IN-MEMORY state: a 10 s cache, and per-tenant queue pauses that can last
#      minutes. No API clears that, so the sender is restarted. Without this, re-running an
#      outbound demo inside its own block window looks broken: /send returns 202 and then
#      nothing happens, because the job is queued behind a tenant that is still sleeping.
# Restarting also empties the state cache, which is why there is no 10-second wait any more.
function Reset-Outbound {
    param([hashtable]$SenderEnv = @{})

    Write-Step 'Resetting outbound state (mock + app-usage override + SQL rows)'
    & curl.exe -s -m 10 -o NUL -X POST "$MockUrl/simulator/reset" 2>$null
    $file = Write-TempJson 'appusage.json' '{"pct":null}'
    & curl.exe -s -m 10 -o NUL -X POST "$MockUrl/simulator/app-usage" `
               -H 'Content-Type: application/json' --data "@$file" 2>$null
    Invoke-Sql 'DELETE FROM TenantRateLimitState;' | Out-Null

    if ($SkipStart) {
        Write-Warn 'attach mode: the sender cannot be restarted, so a tenant paused by an earlier'
        Write-Warn 'run stays paused and its demo will show no activity. Use fresh tenant ids.'
        for ($i = 11; $i -gt 0; $i--) {
            Write-Host "`r  waiting out the 10 s state cache ... $i  " -NoNewline -ForegroundColor DarkGray
            Start-Sleep -Seconds 1
        }
        Write-Host "`r  state cache cleared                 " -ForegroundColor Green
        return
    }

    Write-Step ('Restarting the sender for an empty queue and cache: ' + (Format-EnvState $SenderEnv))
    if (-not (Stop-OneService 'sender')) {
        throw 'Cannot reset: the sender is still running, so its queue state would leak into this demo.'
    }
    Start-OneService 'sender' $SenderEnv
    $script:ServiceEnvState['sender'] = Format-EnvState $SenderEnv
}

# ------------------------------------------- assertions --------------------------------------

# Patterns starting with '!' must NOT appear. Returns $true when every expectation held.
function Assert-Log {
    param([string]$Key, [int]$Mark, [string[]]$Patterns, [string]$Filter)

    $lines = @(Get-LogSince $Key $Mark | Where-Object { $_ -match '\S' })
    Write-Host '  observed:' -ForegroundColor White
    $shown = $lines
    if ($Filter) { $shown = @($lines | Where-Object { $_ -match $Filter }) }
    if ($shown.Count -eq 0) { Write-Dim "(no matching lines - see logs\$Key.log for everything)" }
    $shown | Select-Object -First 14 | ForEach-Object { Write-Dim $_ }

    $pass = $true
    Write-Host '  expected:' -ForegroundColor White
    foreach ($pattern in $Patterns) {
        $negate = $pattern.StartsWith('!')
        $regex  = $pattern
        if ($negate) { $regex = $pattern.Substring(1) }
        $hit = @($lines | Where-Object { $_ -match $regex })

        if ($negate) {
            if ($hit.Count -eq 0) { Write-Ok "absent: $regex" }
            else { Write-Err "should be absent, found $($hit.Count)x: $regex"; $pass = $false }
        } else {
            if ($hit.Count -gt 0) { Write-Ok "$regex  ($($hit.Count)x)" }
            else { Write-Err "missing: $regex"; $pass = $false }
        }
    }
    return [bool]$pass
}

function Record-Result([string]$Demo, [bool]$Pass) {
    $verdict = 'FAIL'
    if ($Pass) { $verdict = 'PASS' }
    $script:Results += New-Object psobject -Property @{ Demo = $Demo; Result = $verdict }
    Write-Host ''
    if ($Pass) { Write-Host "  == $Demo : PASS ==" -ForegroundColor Green }
    else       { Write-Host "  == $Demo : FAIL ==" -ForegroundColor Red }
}

# ------------------------------------------- demos -------------------------------------------

function Demo-1-PerSecondGate {
    Show-Briefing '2.1'

    $tenant  = Ask 'tenant id' 'ps-tenant'
    $mockCap = Ask-Int 'mock cap (calls/s)' 5
    $gate    = Ask-Int 'sender text-class gate (calls/s)' 4
    $count   = Ask-Int 'sends to fire' 15

    Reset-Outbound -SenderEnv @{ 'RateLimiting__Outbound__PerSecondDispatchLimit' = $gate }
    Set-Scenario @{ name = 'PerSecondRateLimit'; tenantId = $tenant; perSecondLimit = $mockCap }

    $mark = Get-LogMark 'sender'
    Send-Burst $count $tenant (Endpoint-For $tenant)
    $wait = [math]::Ceiling($count / [double]$gate) + 5
    Write-Step "waiting ${wait}s for the queue to drain"
    Start-Sleep -Seconds $wait

    $pass = Assert-Log 'sender' $mark @("Sent job for tenant $tenant", "!Rate-limit error for tenant $tenant") $tenant
    Show-MockState $tenant
    Record-Result '2.1 per-second gate' $pass
}

function Demo-2-RetryAfter429 {
    Show-Briefing '2.2'

    $tenant  = Ask 'tenant id' 'ra-tenant'
    $seconds = Ask-Int 'Retry-After (s)' 90

    Reset-Outbound
    Set-Scenario @{ name = 'RetryAfter429'; tenantId = $tenant; retryAfterSeconds = $seconds }

    $expectedMinutes = [math]::Ceiling($seconds / 60.0)
    $mark = Get-LogMark 'sender'
    Send-Job $tenant (Endpoint-For $tenant) | Out-Null
    Start-Sleep -Seconds 6

    $pass = Assert-Log 'sender' $mark @(
        "HTTP 429 for tenant $tenant.*Retry-After",
        "Tenant $tenant is blocked for $expectedMinutes min",
        "Tenant $tenant rate-limited \(TooManyRequests\)"
    ) $tenant
    Write-Dim 'the pause exceeds the inline-retry cap, so the queue owns the retry - no inline attempt'
    Show-StateRows
    Record-Result '2.2 Retry-After 429' $pass
}

function Demo-3-SharedAppBudget {
    Show-Briefing '2.3'

    $pct = Ask-Int 'app usage %' 92

    Reset-Outbound
    Set-Scenario @{ name = 'SharedAppBudget'; appUsagePct = $pct }

    $mark = Get-LogMark 'sender'
    Send-Job 'tenant-heavy' (Endpoint-For 'tenant-heavy') | Out-Null
    Start-Sleep -Seconds 4
    Send-Job 'tenant-light' (Endpoint-For 'tenant-light') | Out-Null
    Start-Sleep -Seconds 14

    $pass = Assert-Log 'sender' $mark @('Proactive throttle for tenant tenant-light') 'tenant-(heavy|light)'
    Show-StateRows
    Write-Dim "level separation: the app row should read $pct while tenant-light keeps its OWN low % (not $pct)"
    Record-Result '2.3 shared app budget' $pass
}

function Demo-4-Classic {
    Show-Briefing '2.4'
    $names = @('GradualApproach', 'SuddenBlock', 'FlappingBlock', 'MultiTenantMix', 'RecoveryTest')
    for ($i = 0; $i -lt $names.Count; $i++) { Write-Host "   $($i + 1)) $($names[$i])" -ForegroundColor Gray }

    $pick = Ask-Int 'scenario' 1
    if ($pick -lt 1 -or $pick -gt $names.Count) { $pick = 1 }
    $name   = $names[$pick - 1]
    $tenant = Ask 'tenant id' 'tenant-1'
    $count  = Ask-Int 'sends to fire' 22

    Reset-Outbound
    Set-Scenario @{ name = $name; tenantId = $tenant; blockForMinutes = 2 }

    $mark  = Get-LogMark 'sender'
    $watch = $tenant
    if ($name -eq 'MultiTenantMix') {
        foreach ($t in @('tenant-a', 'tenant-b', 'tenant-c')) { Send-Burst 8 $t (Endpoint-For $t) }
        $watch = 'tenant-(a|b|c)'
    } else {
        Send-Burst $count $tenant (Endpoint-For $tenant)
    }
    Write-Step 'watching for 20s'
    Start-Sleep -Seconds 20

    Get-LogSince 'sender' $mark | Where-Object { $_ -match $watch } |
        Select-Object -First 25 | ForEach-Object { Write-Dim $_ }
    if ($name -ne 'MultiTenantMix') { Show-MockState $tenant }
    Show-StateRows
}

function Demo-5-BucBlock {
    Show-Briefing '2.5'

    $tenant  = Ask 'tenant id' 'tenant-buc'
    $minutes = Ask-Int 'block minutes' 8

    Reset-Outbound
    Set-Scenario @{ name = 'InstagramBucBlock'; tenantId = $tenant; blockForMinutes = $minutes }

    $mark = Get-LogMark 'sender'
    Send-Job $tenant (Endpoint-For $tenant) | Out-Null
    Start-Sleep -Seconds 6

    $pass = Assert-Log 'sender' $mark @(
        'code=80002, subcode=0, level=account',
        "Tenant $tenant is blocked for $minutes min",
        "Tenant $tenant rate-limited \(BadRequest\)"
    ) $tenant
    Write-Dim 'level=account is the point: the block belongs on the tenant row, not the app row'
    Show-StateRows
    Record-Result '2.5 BUC 80002' $pass
}

function Demo-6-Custom613 {
    Show-Briefing '2.6'

    $tenant = Ask 'tenant id' 'tenant-613'

    Reset-Outbound
    Set-Scenario @{ name = 'CustomRateLimit613'; tenantId = $tenant }

    $mark = Get-LogMark 'sender'
    Send-Job $tenant (Endpoint-For $tenant) | Out-Null
    Start-Sleep -Seconds 6

    $pass = Assert-Log 'sender' $mark @(
        'code=613, subcode=1996, level=app',
        'Tenant app:.* is blocked for 15 min',
        "Tenant $tenant rate-limited \(BadRequest\)"
    ) "($tenant|app:)"
    Show-StateRows
    Record-Result '2.6 613/1996' $pass
}

function Demo-7-AppLevelBlock {
    Show-Briefing '2.7'

    $guilty   = Ask 'tenant that receives the error' 'tenant-guilty'
    $innocent = Ask 'unrelated tenant' 'tenant-innocent'
    $minutes  = Ask-Int 'block minutes' 6

    Reset-Outbound
    Set-Scenario @{ name = 'AppLevelBlock'; tenantId = $guilty; blockForMinutes = $minutes }

    $mark = Get-LogMark 'sender'
    Send-Job $guilty (Endpoint-For $guilty) | Out-Null
    Start-Sleep -Seconds 6
    Send-Job $innocent (Endpoint-For $innocent) | Out-Null
    Start-Sleep -Seconds 5

    $pass = Assert-Log 'sender' $mark @(
        'code=4, subcode=0, level=app',
        "Tenant app:.* is blocked for $minutes min",
        "Tenant $innocent held by an APP-level block"
    ) '(tenant-|app:)'
    Show-StateRows
    Write-Dim "expect: the app row blocked, $guilty NOT blocked (its own budget is fine), no $innocent row at all"
    Record-Result '2.7 app-level code 4' $pass
}

function Demo-8-DispatchClasses {
    Show-Briefing '2.8'

    $count = Ask-Int 'requests per burst' 20

    Reset-Outbound   # restart puts the default caps 100 / 10 / 2 / 2 back in force

    $mark = Get-LogMark 'sender'
    Write-Step 'media burst (attachment.type = video -> MediaSend, cap 10/s)'
    Send-Burst $count 't-media' (Endpoint-For 't-media') '{"message":{"attachment":{"type":"video"}}}'
    Write-Step 'text burst (message.text -> TextSend, cap 100/s)'
    Send-Burst $count 't-text' (Endpoint-For 't-text') '{"message":{"text":"hi"}}'
    Write-Step 'conversations burst (tightest class, cap 2/s)'
    Send-Burst 6 't-conv' (Endpoint-For 't-conv' 'conversations')
    Start-Sleep -Seconds 12

    $pass = Assert-Log 'sender' $mark @(
        'class=MediaSend cap=10/s',
        'class=Conversations cap=2/s',
        '!class=TextSend cap=100/s'
    ) 'Per-second gate'
    Write-Dim 'the mock has no /conversations route, but the gate runs BEFORE the HTTP call'
    Record-Result '2.8 per-class caps' $pass
}

function Demo-9-InboundScenario {
    Show-Briefing '3.1'
    $names = @('SteadyTraffic', 'BurstSingleIp', 'DDoSMultiIp', 'OversizedPayload',
               'InvalidSignature', 'SlowLoris', 'GlobalFlood', 'MixedAttack')
    for ($i = 0; $i -lt $names.Count; $i++) { Write-Host "   $($i + 1)) $($names[$i])" -ForegroundColor Gray }

    $pick = Ask-Int 'scenario' 2
    if ($pick -lt 1 -or $pick -gt $names.Count) { $pick = 2 }
    $name     = $names[$pick - 1]
    $duration = Ask-Int 'duration (s)' 15

    Ensure-ServiceEnv 'ingest' @{}
    $body = @{ scenarioName = $name; durationSeconds = $duration } | ConvertTo-Json -Compress
    $file = Write-TempJson 'inbound.json' $body

    Write-Step "running $name for ${duration}s (the call blocks until the scenario finishes)"
    $raw = & curl.exe -s -m 900 -X POST "$SimUrl/simulator/run" -H 'Content-Type: application/json' --data "@$file" 2>$null
    if (-not $raw) { Write-Err 'no result came back from the simulator'; return }

    $result = $raw | ConvertFrom-Json
    Write-Host '  status counts:' -ForegroundColor White
    $result.statusCounts.PSObject.Properties | ForEach-Object { Write-Dim "HTTP $($_.Name) -> $($_.Value)" }
    Write-Dim "total sent $($result.totalSent), avg latency $([math]::Round($result.averageLatencyMs, 1)) ms"

    $expect = @{
        'SteadyTraffic'    = '202 only'
        'BurstSingleIp'    = '~60x 202 then 429s (per-IP limit is 60/min)'
        'DDoSMultiIp'      = 'mixed 202/429 across the virtual IPs'
        'OversizedPayload' = '413s'
        'InvalidSignature' = '401s'
        'SlowLoris'        = '202s then 429s from the concurrency cap (needs Development env)'
        'GlobalFlood'      = 'run 32s+ to exceed the 1000/min global window'
        'MixedAttack'      = 'a blend of 401/413/429'
    }
    Write-Host "  expected shape: $($expect[$name])" -ForegroundColor White
}

function Demo-10-BypassRules {
    Show-Briefing '3.2'

    Ensure-ServiceEnv 'ingest' @{}
    $file = Write-TempJson 'tiny.json' '{"x":1}'

    $health = & curl.exe -s -m 10 -o NUL -w '%{http_code}' -X POST "$IngestUrl/health" `
                         -H 'Content-Type: application/json' --data "@$file" 2>$null
    $get    = & curl.exe -s -m 10 -o NUL -w '%{http_code}' "$IngestUrl/anything" 2>$null

    Write-Host "  POST /health (no HMAC signature) -> $health" -ForegroundColor White
    Write-Host "  GET  /anything                   -> $get" -ForegroundColor White

    $pass = $true
    if ($health -eq '200') { Write-Ok 'excluded path bypassed the whole pipeline' }
    else { Write-Err "expected 200, got $health"; $pass = $false }
    if ($get -eq '404') { Write-Ok 'GET reached routing (GETs are never rate-limited)' }
    else { Write-Err "expected 404, got $get"; $pass = $false }
    Record-Result '3.2 bypass rules' $pass
}

function Demo-11-ObserveOnly {
    Show-Briefing '3.3'

    $duration = Ask-Int 'duration (s)' 15
    Ensure-ServiceEnv 'ingest' @{ 'RateLimiting__Inbound__ObserveOnly' = 'true' }

    $body = @{ scenarioName = 'BurstSingleIp'; durationSeconds = $duration } | ConvertTo-Json -Compress
    $file = Write-TempJson 'inbound.json' $body

    $mark = Get-LogMark 'ingest'
    Write-Step "running BurstSingleIp for ${duration}s in observe-only mode"
    $raw = & curl.exe -s -m 900 -X POST "$SimUrl/simulator/run" -H 'Content-Type: application/json' --data "@$file" 2>$null

    $denied = 0
    if ($raw) {
        $result = $raw | ConvertFrom-Json
        Write-Host '  status counts:' -ForegroundColor White
        $result.statusCounts.PSObject.Properties | ForEach-Object {
            Write-Dim "HTTP $($_.Name) -> $($_.Value)"
            if ([int]$_.Name -ge 400) { $denied += [int]$_.Value }
        }
    }
    $wouldDeny = @(Get-LogSince 'ingest' $mark | Where-Object { $_ -match 'OBSERVE ONLY' }).Count

    $pass = $true
    if ($denied -eq 0) { Write-Ok 'nothing was rejected - all traffic passed' }
    else { Write-Err "$denied requests were denied - observe-only is not in force"; $pass = $false }
    if ($wouldDeny -gt 0) { Write-Ok "$wouldDeny 'would deny' lines recorded in logs\ingest.log" }
    else { Write-Err "no 'OBSERVE ONLY' lines were logged"; $pass = $false }
    Record-Result '3.3 observe-only' $pass

    Write-Step 'restoring enforce mode'
    Ensure-ServiceEnv 'ingest' @{}
}

# ------------------------------------------- menu --------------------------------------------

function Show-Status {
    $up = @()
    foreach ($key in $Services.Keys) {
        $state = 'DOWN'
        if (Test-Port $Services[$key].Port) { $state = 'UP' }
        $up += "$key :$($Services[$key].Port) $state"
    }
    $senderEnv = $script:ServiceEnvState['sender']
    if (-not $senderEnv) { $senderEnv = '(unknown)' }

    Write-Host ''
    Write-Host ('-' * 74) -ForegroundColor DarkGray
    Write-Host "  Graph $script:GraphVersion - LOCAL MOCK ONLY (no real Graph API calls possible)" -ForegroundColor DarkCyan
    Write-Host "  $($up -join '   ')" -ForegroundColor Gray
    Write-Host "  sender settings: $senderEnv    state rows: $(Get-RowCount)" -ForegroundColor Gray
    Write-Host ('-' * 74) -ForegroundColor DarkGray
}

function Show-Menu {
    Show-Status
    Write-Host '  Type a use case id to run just that one. OUTBOUND = we call Instagram.' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  OUTBOUND - InstagramSenderApi (ours) -> InstagramGraphMock (pretend Instagram)' -ForegroundColor Cyan
    $lastDir = ''
    foreach ($id in $DemoCatalog.Keys) {
        $d = $DemoCatalog[$id]
        if ($d.Dir -like 'INBOUND*' -and $lastDir -notlike 'INBOUND*') {
            Write-Host ''
            Write-Host '  INBOUND - WebhookTrafficSimulator (attacker/Meta) -> WebhookIngestApi (ours)' -ForegroundColor Cyan
        }
        Write-Host ("   {0}  {1}" -f $id, $d.Title)
        $lastDir = $d.Dir
    }
    Write-Host ''
    Write-Host '  A  run all outbound use cases with defaults    I  run all inbound use cases' -ForegroundColor Yellow
    Write-Host '  R  reset outbound state    S  status    L  live log windows'
    Write-Host '  B  stop + rebuild + restart    K  stop all services    Q  quit'
    Write-Host ''
}

function Open-LogWindow([string]$Key) {
    $path = Log-Path $Key
    if (-not (Test-Path -LiteralPath $path)) { Write-Warn "no log yet: $path"; return }
    $command = "`$host.UI.RawUI.WindowTitle='$Key log'; Get-Content -LiteralPath '$path' -Wait -Tail 40"
    Start-Process powershell -ArgumentList '-NoExit', '-NoProfile', '-Command', $command | Out-Null
    Write-Ok "live tail window opened for $Key"
}

# Unattended: every prompt takes its default and no "run the next one?" question is asked.
# A demo that throws (a service that would not restart, say) is recorded and the set continues.
function Invoke-DemoSet([string[]]$Ids) {
    $script:Auto    = $true
    $script:Results = @()
    foreach ($id in $Ids) {
        try { & $DemoCatalog[$id].Fn }
        catch {
            Write-Err "use case $id could not run: $($_.Exception.Message)"
            Record-Result "$id (aborted)" $false
        }
    }
    $script:Auto = $false
    Show-Summary
}

function Show-Summary {
    Write-Head 'Summary'
    if (-not $script:Results -or $script:Results.Count -eq 0) { Write-Dim 'nothing was asserted'; return }
    foreach ($r in $script:Results) {
        $colour = 'Red'
        if ($r.Result -eq 'PASS') { $colour = 'Green' }
        Write-Host ("  {0,-26} {1}" -f $r.Demo, $r.Result) -ForegroundColor $colour
    }
    $failed = @($script:Results | Where-Object { $_.Result -eq 'FAIL' }).Count
    Write-Host ''
    if ($failed -eq 0) { Write-Host "  all $($script:Results.Count) demos passed" -ForegroundColor Green }
    else { Write-Host "  $failed of $($script:Results.Count) demos failed" -ForegroundColor Red }
}

# ------------------------------------------- startup -----------------------------------------

function Initialize-Environment {
    Write-Head 'RateLimit demo console'

    if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'curl.exe not found on PATH.' }
    $script:HasSqlCmd = [bool](Get-Command sqlcmd -ErrorAction SilentlyContinue)
    if (-not $script:HasSqlCmd) { Write-Warn 'sqlcmd not found - SQL row checks and resets will be skipped' }

    Assert-MockOnly

    if ($SkipStart) {
        Write-Step 'attaching to already-running services (-SkipStart)'
        foreach ($key in $Services.Keys) {
            if (Test-Port $Services[$key].Port) { Write-Ok "$key on :$($Services[$key].Port)" }
            else { Write-Warn "$key is DOWN on :$($Services[$key].Port)" }
            $script:ServiceEnvState[$key] = '(defaults)'
        }
        return
    }

    Write-Step 'Starting SQL Server LocalDB'
    & cmd /c 'sqllocaldb start mssqllocaldb 2>&1' | ForEach-Object { Write-Dim $_ }

    # Frees the build outputs and clears orphans. Refusing to continue is deliberate: a surviving
    # process would serve the demos with old code and old queue state.
    if (-not (Stop-AllServices)) { throw 'Startup aborted - services from a previous run are still alive.' }
    Invoke-Build

    foreach ($key in $Services.Keys) {
        Start-OneService $key @{}
        $script:ServiceEnvState[$key] = '(defaults)'
    }

    Test-Wiring
    if (-not $NoLogWindow) { Open-LogWindow 'sender' }
}

# 2.4 is excluded from the unattended sets on purpose: it asserts nothing.
$OutboundSet = @('2.1', '2.2', '2.3', '2.5', '2.6', '2.7', '2.8')
$InboundSet  = @('3.1', '3.2', '3.3')

Initialize-Environment

if ($Run) {
    switch ($Run.ToLower()) {
        'outbound' { Invoke-DemoSet $OutboundSet; exit 0 }
        'inbound'  { Invoke-DemoSet $InboundSet;  exit 0 }
        'all'      { Invoke-DemoSet ($OutboundSet + $InboundSet); exit 0 }
    }

    $id = Resolve-DemoId $Run
    if (-not $id) {
        Write-Err "unknown -Run value '$Run' (use a use case id like 2.5, an ordinal 1-11, or outbound/inbound/all)"
        exit 1
    }
    # Parameters take their defaults, but the operator is still offered the next use case.
    $script:Auto = $true
    & $DemoCatalog[$id].Fn
    $script:Auto = $false
    $next = Prompt-NextDemo $id
    if ($next) { Invoke-DemoChain $next }
    exit 0
}

$emptyInputs = 0
while ($true) {
    Show-Menu
    $choice = Read-Host '  choose'

    # Guard against a closed stdin (non-interactive host): Read-Host returns empty forever and the
    # menu would spin. A few blank lines from a real user is fine; a run of them means no console.
    if ([string]::IsNullOrWhiteSpace($choice)) {
        $emptyInputs++
        if ($emptyInputs -ge 5) { Write-Warn 'no console input - exiting'; return }
        continue
    }
    $emptyInputs = 0

    # if/elseif rather than switch: 'continue' inside a PowerShell switch has ambiguous scoping
    # (switch is itself a loop construct), and this loop's control flow has to be unambiguous.
    $upper   = $choice.Trim().ToUpper()
    $handled = $true

    if     ($upper -eq 'A') { Invoke-DemoSet $OutboundSet; Pause-Menu }
    elseif ($upper -eq 'I') { Invoke-DemoSet $InboundSet;  Pause-Menu }
    elseif ($upper -eq 'R') { Reset-Outbound; Pause-Menu }
    elseif ($upper -eq 'S') { Show-Status;    Pause-Menu }
    elseif ($upper -eq 'L') { Open-LogWindow 'sender'; Open-LogWindow 'mock' }
    elseif ($upper -eq 'K') { Stop-AllServices | Out-Null; Pause-Menu }
    elseif ($upper -eq 'B') {
        if (Stop-AllServices) {
            Invoke-Build
            foreach ($key in $Services.Keys) {
                Start-OneService $key @{}
                $script:ServiceEnvState[$key] = '(defaults)'
            }
        } else {
            Write-Warn 'rebuild skipped - a running service would lock its own binary (MSB3021)'
        }
        Pause-Menu
    }
    elseif ($upper -eq 'Q') {
        $answer = Read-Host '  stop all services before exiting? [Y/n]'
        if ($answer -notmatch '^[nN]') { Stop-AllServices | Out-Null }
        return
    }
    else { $handled = $false }

    if (-not $handled) {
        $id = Resolve-DemoId $choice
        if (-not $id) {
            Write-Warn "'$choice' is not a use case id or a menu option"
        } else {
            Invoke-DemoChain $id
            if ($script:QuitRequested) {
                $answer = Read-Host '  stop all services before exiting? [Y/n]'
                if ($answer -notmatch '^[nN]') { Stop-AllServices | Out-Null }
                return
            }
        }
    }
}
