$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

function Get-EnvMap {
    $map = @{}
    Get-Content .env |
        Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } |
        ForEach-Object {
            $k, $v = $_ -split '=', 2
            $map[$k] = $v.Trim()
        }
    return $map
}

function Extract-Text($resp) {
    if ($resp.choices -and $resp.choices.Count -gt 0) {
        $msg = $resp.choices[0].message
        if ($msg) {
            if (-not [string]::IsNullOrWhiteSpace($msg.content)) { return $msg.content.Trim() }
            if (-not [string]::IsNullOrWhiteSpace($msg.reasoning_content)) { return $msg.reasoning_content.Trim() }
        }
    }
    if ($resp.result -and $resp.result.response) {
        return $resp.result.response.Trim()
    }
    return ''
}

$envMap = Get-EnvMap

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  PROBANDO MISTRAL AI Y CLOUDFLARE WORKERS AI" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# ── 1. MISTRAL ──────────────────────────────────────────────────────────
$mistralKey = $envMap['PROVIDER_MISTRAL_API_KEY']
$mistralBase = if ($envMap['PROVIDER_MISTRAL_BASE_URL']) { $envMap['PROVIDER_MISTRAL_BASE_URL'] } else { 'https://api.mistral.ai' }

Write-Host "`n[MISTRAL] Verificando en $mistralBase..." -ForegroundColor Yellow
if ([string]::IsNullOrWhiteSpace($mistralKey)) {
    Write-Host "  -> ERROR: PROVIDER_MISTRAL_API_KEY no encontrada en .env" -ForegroundColor Red
} else {
    $headers = @{
        Authorization = "Bearer $mistralKey"
        'Content-Type' = 'application/json'
    }

    # Discovery
    try {
        $modelsUri = $mistralBase.TrimEnd('/') + '/v1/models'
        $resp = Invoke-RestMethod -Uri $modelsUri -Method Get -Headers $headers -TimeoutSec 20
        $discovered = @($resp.data | ForEach-Object { $_.id }) | Sort-Object -Unique
        Write-Host "  -> DISCOVERY OK: $($discovered.Count) modelos encontrados" -ForegroundColor Green
        $sample = $discovered | Select-Object -First 10
        foreach ($m in $sample) { Write-Host "     - $m" -ForegroundColor Gray }
        if ($discovered.Count -gt 10) { Write-Host "     ... y $($discovered.Count - 10) mas" -ForegroundColor DarkGray }
    } catch {
        Write-Host "  -> DISCOVERY ERROR: $($_.Exception.Message)" -ForegroundColor Red
        $discovered = @()
    }

    # Test Chat
    $testCandidates = @('codestral-latest', 'mistral-large-latest', 'mistral-small-latest', 'magistral-medium-latest', 'devstral-medium-latest', 'open-mistral-nemo')
    Write-Host "`n  Probando llamadas de Chat en Mistral:" -ForegroundColor Yellow
    foreach ($cand in $testCandidates) {
        Start-Sleep -Seconds 2
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $body = @{
            model = $cand
            stream = $false
            max_tokens = 16
            messages = @(@{ role = 'user'; content = 'Ping. Respond with PONG' })
        } | ConvertTo-Json -Depth 5

        try {
            $chatUri = $mistralBase.TrimEnd('/') + '/v1/chat/completions'
            $cResp = Invoke-RestMethod -Uri $chatUri -Method Post -Headers $headers -Body $body -TimeoutSec 30
            $sw.Stop()
            $txt = Extract-Text $cResp
            Write-Host ("  [OK]   {0,-28} (HTTP 200, {1,5}ms): {2}" -f $cand, [int]$sw.Elapsed.TotalMilliseconds, $txt) -ForegroundColor Green
        } catch {
            $sw.Stop()
            $code = 0
            $err = $_.Exception.Message
            if ($_.Exception.Response) {
                try {
                    $code = [int]$_.Exception.Response.StatusCode
                    $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                    $err = $sr.ReadToEnd()
                    $sr.Close()
                } catch {}
            }
            if ($err.Length -gt 100) { $err = $err.Substring(0, 100) }
            Write-Host ("  [FAIL] {0,-28} (HTTP {1}, {2,5}ms): {3}" -f $cand, $code, [int]$sw.Elapsed.TotalMilliseconds, $err) -ForegroundColor Red
        }
    }
}

# ── 2. CLOUDFLARE ────────────────────────────────────────────────────────
$cfKey = $envMap['PROVIDER_CLOUDFLARE_API_KEY']
$cfBase = $envMap['PROVIDER_CLOUDFLARE_BASE_URL']

Write-Host "`n[CLOUDFLARE] Verificando en $cfBase..." -ForegroundColor Yellow
if ([string]::IsNullOrWhiteSpace($cfKey)) {
    Write-Host "  -> ERROR: PROVIDER_CLOUDFLARE_API_KEY no encontrada en .env" -ForegroundColor Red
} elseif ([string]::IsNullOrWhiteSpace($cfBase)) {
    Write-Host "  -> ERROR: PROVIDER_CLOUDFLARE_BASE_URL no encontrada en .env" -ForegroundColor Red
} else {
    $headers = @{
        Authorization = "Bearer $cfKey"
        'Content-Type' = 'application/json'
    }

    # Ensure trailing slash logic for Cloudflare
    # Note: If base ends with /v1, /chat/completions or /models can be called directly
    $cfBaseNorm = $cfBase.TrimEnd('/')

    # Test Discovery or Chat directly
    # Cloudflare OpenAI-compatible endpoint: $cfBaseNorm/chat/completions
    Write-Host "  Probando modelos de Cloudflare:" -ForegroundColor Yellow
    $cfCandidates = @(
        '@cf/meta/llama-3.3-70b-instruct-fp8-fast',
        '@cf/meta/llama-3.1-8b-instruct-fast',
        '@cf/qwen/qwen2.5-coder-32b-instruct'
    )

    foreach ($cand in $cfCandidates) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $body = @{
            model = $cand
            stream = $false
            max_tokens = 16
            messages = @(@{ role = 'user'; content = 'Ping. Respond with PONG' })
        } | ConvertTo-Json -Depth 5

        try {
            $chatUri = "$cfBaseNorm/chat/completions"
            $cResp = Invoke-RestMethod -Uri $chatUri -Method Post -Headers $headers -Body $body -TimeoutSec 30
            $sw.Stop()
            $txt = Extract-Text $cResp
            Write-Host ("  [OK]   {0,-42} (HTTP 200, {1,5}ms): {2}" -f $cand, [int]$sw.Elapsed.TotalMilliseconds, $txt) -ForegroundColor Green
        } catch {
            $sw.Stop()
            $code = 0
            $err = $_.Exception.Message
            if ($_.Exception.Response) {
                try {
                    $code = [int]$_.Exception.Response.StatusCode
                    $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                    $err = $sr.ReadToEnd()
                    $sr.Close()
                } catch {}
            }
            if ($err.Length -gt 120) { $err = $err.Substring(0, 120) }
            Write-Host ("  [FAIL] {0,-42} (HTTP {1}, {2,5}ms): {3}" -f $cand, $code, [int]$sw.Elapsed.TotalMilliseconds, $err) -ForegroundColor Red
        }
    }
}
