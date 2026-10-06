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
            if (-not [string]::IsNullOrWhiteSpace($msg.reasoning)) { return $msg.reasoning.Trim() }
        }
    }
    if ($resp.message) {
        if (-not [string]::IsNullOrWhiteSpace($resp.message.content)) { return $resp.message.content.Trim() }
        if (-not [string]::IsNullOrWhiteSpace($resp.message.reasoning)) { return $resp.message.reasoning.Trim() }
    }
    return ''
}

function Test-ModelChat($provider, $baseUrl, $apiKey, $chatPath, $modelId, $isOllama) {
    $headers = @{
        Authorization = "Bearer $apiKey"
        'Content-Type' = 'application/json'
    }
    if ($provider -eq 'openrouter') {
        $headers['HTTP-Referer'] = 'https://github.com/rodrigo714-gmail/ai-proxy-hub'
        $headers['X-Title'] = 'AI Proxy Hub'
    }

    $bodyObj = if ($isOllama) {
        @{
            model = $modelId
            stream = $false
            messages = @(@{ role = 'user'; content = 'Ping. Respond with PONG' })
        }
    } else {
        @{
            model = $modelId
            stream = $false
            max_tokens = 16
            messages = @(@{ role = 'user'; content = 'Ping. Respond with PONG' })
        }
    }
    $body = $bodyObj | ConvertTo-Json -Depth 5

    $uri = $baseUrl.TrimEnd('/') + '/' + $chatPath.TrimStart('/')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $body -TimeoutSec 35
        $sw.Stop()
        $txt = Extract-Text $resp
        return [pscustomobject]@{
            Status = 'OK'
            StatusCode = 200
            LatencyMs = [int]$sw.Elapsed.TotalMilliseconds
            Text = if ($txt.Length -gt 60) { $txt.Substring(0, 60) } else { $txt }
            Error = ''
        }
    } catch {
        $sw.Stop()
        $code = 0
        $errBody = $_.Exception.Message
        if ($_.Exception.Response) {
            try {
                $code = [int]$_.Exception.Response.StatusCode
                $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                $errBody = $sr.ReadToEnd()
                $sr.Close()
            } catch {}
        }
        return [pscustomobject]@{
            Status = 'FAIL'
            StatusCode = $code
            LatencyMs = [int]$sw.Elapsed.TotalMilliseconds
            Text = ''
            Error = if ($errBody.Length -gt 120) { $errBody.Substring(0, 120) } else { $errBody }
        }
    }
}

$envMap = Get-EnvMap
$discovered = Get-Content 'docs/testing/live-discovered-models.json' -Raw | ConvertFrom-Json

$providerMeta = @{
    'deepseek'   = @{ Base = $(if ($envMap['PROVIDER_DEEPSEEK_BASE_URL']) { $envMap['PROVIDER_DEEPSEEK_BASE_URL'] } else { 'https://api.deepseek.com' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_DEEPSEEK_API_KEY']; Ollama = $false }
    'openai'     = @{ Base = $(if ($envMap['PROVIDER_OPENAI_BASE_URL']) { $envMap['PROVIDER_OPENAI_BASE_URL'] } else { 'https://api.openai.com' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_OPENAI_API_KEY']; Ollama = $false }
    'google'     = @{ Base = $(if ($envMap['PROVIDER_GOOGLE_BASE_URL']) { $envMap['PROVIDER_GOOGLE_BASE_URL'] } else { 'https://generativelanguage.googleapis.com' }); Chat = '/v1beta/openai/chat/completions'; Key = $envMap['PROVIDER_GOOGLE_API_KEY']; Ollama = $false }
    'zai'        = @{ Base = $(if ($envMap['PROVIDER_ZAI_BASE_URL']) { $envMap['PROVIDER_ZAI_BASE_URL'] } else { 'https://api.z.ai/api/paas/v4' }); Chat = '/chat/completions'; Key = $envMap['PROVIDER_ZAI_API_KEY']; Ollama = $false }
    'nvidia'     = @{ Base = $(if ($envMap['PROVIDER_NVIDIA_BASE_URL']) { $envMap['PROVIDER_NVIDIA_BASE_URL'] } else { 'https://integrate.api.nvidia.com' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_NVIDIA_API_KEY']; Ollama = $false }
    'openrouter' = @{ Base = $(if ($envMap['PROVIDER_OPENROUTER_BASE_URL']) { $envMap['PROVIDER_OPENROUTER_BASE_URL'] } else { 'https://openrouter.ai/api' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_OPENROUTER_API_KEY']; Ollama = $false }
    'groq'       = @{ Base = $(if ($envMap['PROVIDER_GROQ_BASE_URL']) { $envMap['PROVIDER_GROQ_BASE_URL'] } else { 'https://api.groq.com/openai' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_GROQ_API_KEY']; Ollama = $false }
    'ollama'     = @{ Base = $(if ($envMap['PROVIDER_OLLAMA_BASE_URL']) { $envMap['PROVIDER_OLLAMA_BASE_URL'] } else { 'https://ollama.com' }); Chat = '/api/chat'; Key = $(if ($envMap['PROVIDER_OLLAMACLOUD_API_KEY']) { $envMap['PROVIDER_OLLAMACLOUD_API_KEY'] } else { $envMap['PROVIDER_OLLAMA_API_KEY'] }); Ollama = $true }
    'moonshot'   = @{ Base = $(if ($envMap['PROVIDER_MOONSHOT_BASE_URL']) { $envMap['PROVIDER_MOONSHOT_BASE_URL'] } else { 'https://api.moonshot.ai' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_MOONSHOT_API_KEY']; Ollama = $false }
    'cerebras'   = @{ Base = $(if ($envMap['PROVIDER_CEREBRAS_BASE_URL']) { $envMap['PROVIDER_CEREBRAS_BASE_URL'] } else { 'https://api.cerebras.ai' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_CEREBRAS_API_KEY']; Ollama = $false }
    'zenmux'     = @{ Base = $(if ($envMap['PROVIDER_ZENMUX_BASE_URL']) { $envMap['PROVIDER_ZENMUX_BASE_URL'] } else { 'https://zenmux.ai/api' }); Chat = '/v1/chat/completions'; Key = $envMap['PROVIDER_ZENMUX_API_KEY']; Ollama = $false }
}

$results = @()

foreach ($prov in ($providerMeta.Keys | Sort-Object)) {
    $meta = $providerMeta[$prov]
    if ([string]::IsNullOrWhiteSpace($meta.Key)) {
        Write-Host "[$prov] SKIPPED - No key" -ForegroundColor DarkGray
        continue
    }

    # Load config file
    $cfgFile = "config/model-selection/$prov.json"
    if ($prov -eq 'ollama') { $cfgFile = 'config/model-selection/ollama.json' }
    
    $configModels = @()
    if (Test-Path $cfgFile) {
        $j = Get-Content $cfgFile -Raw | ConvertFrom-Json
        $configModels = @($j.models | ForEach-Object { [pscustomobject]@{ Match = $_.match; Enabled = ($_.enabled -ne $false); KeepEnabled = [bool]$_._keep_enabled } })
    }

    Write-Host "`n========================================================" -ForegroundColor Cyan
    Write-Host " Testing Provider: $prov" -ForegroundColor Cyan
    Write-Host "========================================================" -ForegroundColor Cyan

    # 1. Test all enabled models in config
    $enabledMatches = $configModels | Where-Object { $_.Enabled }
    Write-Host "--- Testing $($enabledMatches.Count) ENABLED models in $cfgFile ---" -ForegroundColor Yellow
    foreach ($m in $enabledMatches) {
        $res = Test-ModelChat -provider $prov -baseUrl $meta.Base -apiKey $meta.Key -chatPath $meta.Chat -modelId $m.Match -isOllama $meta.Ollama
        $color = if ($res.Status -eq 'OK') { 'Green' } else { 'Red' }
        Write-Host ("  [{0}] {1} (HTTP {2}, {3}ms): {4} {5}" -f $res.Status, $m.Match, $res.StatusCode, $res.LatencyMs, $res.Text, $res.Error) -ForegroundColor $color
        $results += [pscustomobject]@{
            Provider = $prov
            Model = $m.Match
            Type = 'ConfigEnabled'
            Status = $res.Status
            StatusCode = $res.StatusCode
            LatencyMs = $res.LatencyMs
            Text = $res.Text
            Error = $res.Error
        }
    }

    # 2. Test any discovered models not enabled in config (up to 15 candidates)
    if ($discovered.$prov) {
        $discList = @($discovered.$prov)
        $notEnabled = $discList | Where-Object { $mId = $_; -not ($enabledMatches | Where-Object { $mId -like "*$($_.Match)*" -or $_.Match -like "*$mId*" }) }
        $candidates = $notEnabled | Select-Object -First 10
        if ($candidates.Count -gt 0) {
            Write-Host "--- Testing $($candidates.Count) DISCOVERED (non-enabled) candidates ---" -ForegroundColor Yellow
            foreach ($cand in $candidates) {
                $res = Test-ModelChat -provider $prov -baseUrl $meta.Base -apiKey $meta.Key -chatPath $meta.Chat -modelId $cand -isOllama $meta.Ollama
                $color = if ($res.Status -eq 'OK') { 'Green' } else { 'DarkYellow' }
                Write-Host ("  [{0}] {1} (HTTP {2}, {3}ms): {4} {5}" -f $res.Status, $cand, $res.StatusCode, $res.LatencyMs, $res.Text, $res.Error) -ForegroundColor $color
                $results += [pscustomobject]@{
                    Provider = $prov
                    Model = $cand
                    Type = 'DiscoveredCandidate'
                    Status = $res.Status
                    StatusCode = $res.StatusCode
                    LatencyMs = $res.LatencyMs
                    Text = $res.Text
                    Error = $res.Error
                }
            }
        }
    }
}

$results | Export-Clixml -Path 'docs/testing/live-chat-validation.xml'
Write-Host "`nValidation complete! Results saved to docs/testing/live-chat-validation.xml" -ForegroundColor Green
