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

$envMap = Get-EnvMap

$providers = @(
    [pscustomobject]@{ Name = 'deepseek';   Key = $envMap['PROVIDER_DEEPSEEK_API_KEY'];   Base = $(if ($envMap['PROVIDER_DEEPSEEK_BASE_URL']) { $envMap['PROVIDER_DEEPSEEK_BASE_URL'] } else { 'https://api.deepseek.com' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'openai';     Key = $envMap['PROVIDER_OPENAI_API_KEY'];     Base = $(if ($envMap['PROVIDER_OPENAI_BASE_URL']) { $envMap['PROVIDER_OPENAI_BASE_URL'] } else { 'https://api.openai.com' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'google';     Key = $envMap['PROVIDER_GOOGLE_API_KEY'];     Base = $(if ($envMap['PROVIDER_GOOGLE_BASE_URL']) { $envMap['PROVIDER_GOOGLE_BASE_URL'] } else { 'https://generativelanguage.googleapis.com' }); ModelsPath = '/v1beta/openai/models'; ChatPath = '/v1beta/openai/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'zai';        Key = $envMap['PROVIDER_ZAI_API_KEY'];        Base = $(if ($envMap['PROVIDER_ZAI_BASE_URL']) { $envMap['PROVIDER_ZAI_BASE_URL'] } else { 'https://api.z.ai/api/paas/v4' }); ModelsPath = '/models'; ChatPath = '/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'nvidia';     Key = $envMap['PROVIDER_NVIDIA_API_KEY'];     Base = $(if ($envMap['PROVIDER_NVIDIA_BASE_URL']) { $envMap['PROVIDER_NVIDIA_BASE_URL'] } else { 'https://integrate.api.nvidia.com' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'openrouter'; Key = $envMap['PROVIDER_OPENROUTER_API_KEY']; Base = $(if ($envMap['PROVIDER_OPENROUTER_BASE_URL']) { $envMap['PROVIDER_OPENROUTER_BASE_URL'] } else { 'https://openrouter.ai/api' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'groq';       Key = $envMap['PROVIDER_GROQ_API_KEY'];       Base = $(if ($envMap['PROVIDER_GROQ_BASE_URL']) { $envMap['PROVIDER_GROQ_BASE_URL'] } else { 'https://api.groq.com/openai' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'ollama';     Key = $(if ($envMap['PROVIDER_OLLAMACLOUD_API_KEY']) { $envMap['PROVIDER_OLLAMACLOUD_API_KEY'] } else { $envMap['PROVIDER_OLLAMA_API_KEY'] }); Base = $(if ($envMap['PROVIDER_OLLAMA_BASE_URL']) { $envMap['PROVIDER_OLLAMA_BASE_URL'] } else { 'https://ollama.com' }); ModelsPath = '/api/tags'; ChatPath = '/api/chat'; IsOllama = $true },
    [pscustomobject]@{ Name = 'moonshot';   Key = $envMap['PROVIDER_MOONSHOT_API_KEY'];   Base = $(if ($envMap['PROVIDER_MOONSHOT_BASE_URL']) { $envMap['PROVIDER_MOONSHOT_BASE_URL'] } else { 'https://api.moonshot.ai' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'cerebras';   Key = $envMap['PROVIDER_CEREBRAS_API_KEY'];   Base = $(if ($envMap['PROVIDER_CEREBRAS_BASE_URL']) { $envMap['PROVIDER_CEREBRAS_BASE_URL'] } else { 'https://api.cerebras.ai' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false },
    [pscustomobject]@{ Name = 'zenmux';     Key = $envMap['PROVIDER_ZENMUX_API_KEY'];     Base = $(if ($envMap['PROVIDER_ZENMUX_BASE_URL']) { $envMap['PROVIDER_ZENMUX_BASE_URL'] } else { 'https://zenmux.ai/api' }); ModelsPath = '/v1/models'; ChatPath = '/v1/chat/completions'; IsOllama = $false }
)

$outDir = 'docs/testing'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$allData = @{}

foreach ($p in $providers) {
    Write-Host "=========================================" -ForegroundColor Cyan
    Write-Host "Checking $($p.Name) at $($p.Base)" -ForegroundColor Cyan
    if ([string]::IsNullOrWhiteSpace($p.Key)) {
        Write-Host "  -> NO API KEY" -ForegroundColor Yellow
        continue
    }

    $headers = @{ Authorization = "Bearer $($p.Key)" }
    $uri = $p.Base.TrimEnd('/') + $p.ModelsPath

    try {
        $resp = Invoke-RestMethod -Uri $uri -Method Get -Headers $headers -TimeoutSec 30
        $modelIds = @()
        if ($resp.data) {
            $modelIds = @($resp.data | ForEach-Object { if ($_.id) { $_.id } elseif ($_.name) { $_.name } elseif ($_ -is [string]) { $_ } })
        } elseif ($resp.models) {
            $modelIds = @($resp.models | ForEach-Object { if ($_.id) { $_.id } elseif ($_.name) { $_.name } elseif ($_.model) { $_.model } elseif ($_ -is [string]) { $_ } })
        }
        $modelIds = @($modelIds | Where-Object { $_ } | Sort-Object -Unique)
        Write-Host "  -> SUCCESS: Found $($modelIds.Count) models" -ForegroundColor Green
        $allData[$p.Name] = $modelIds

        # Show first 15 models
        $sample = $modelIds | Select-Object -First 15
        foreach ($m in $sample) {
            Write-Host "     - $m"
        }
        if ($modelIds.Count -gt 15) {
            Write-Host "     ... and $($modelIds.Count - 15) more" -ForegroundColor Gray
        }
    }
    catch {
        Write-Host "  -> ERROR: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.Exception.Response) {
            try {
                $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                $body = $sr.ReadToEnd()
                $sr.Close()
                Write-Host "     Details: $body" -ForegroundColor DarkRed
            } catch {}
        }
    }
}

$allData | ConvertTo-Json -Depth 5 | Set-Content -Path "$outDir/live-discovered-models.json" -Encoding UTF8
Write-Host "`nSaved all live discovered models to $outDir/live-discovered-models.json" -ForegroundColor Green
