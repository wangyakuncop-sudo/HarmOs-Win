# validate_memory.ps1 — 治理文件自检
# 用法：powershell -ExecutionPolicy Bypass -File scripts\validate_memory.ps1
# 退出码：0 全部通过；1 存在错误

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$errors   = New-Object System.Collections.ArrayList
$warnings = New-Object System.Collections.ArrayList
$passed   = New-Object System.Collections.ArrayList

function Add-Err($m)  { [void]$errors.Add($m) }
function Add-Warn($m) { [void]$warnings.Add($m) }
function Add-Ok($m)   { [void]$passed.Add($m) }

function Read-JsonFile($path) {
    if (-not (Test-Path $path)) { return $null }
    try {
        $raw = Get-Content -Path $path -Raw -Encoding UTF8
        return ($raw | ConvertFrom-Json)
    } catch {
        Add-Err "JSON 解析失败: $path -> $($_.Exception.Message)"
        return $null
    }
}

Write-Host "== HarmOs-Win 治理文件自检 ==" -ForegroundColor Cyan
Write-Host "根目录: $root`n"

# --- 1. JSON 合法性 ---
$jsonFiles = @()
$jsonFiles += Join-Path $root 'AGENTS.json'
$jsonFiles += Join-Path $root 'INDEX.json'
$jsonFiles += Join-Path $root 'PROGRESS.json'
$sessDir = Join-Path $root 'SESSIONS'
if (Test-Path $sessDir) {
    $jsonFiles += (Get-ChildItem -Path $sessDir -Filter '*.json' -File | ForEach-Object { $_.FullName })
}

$docs = @{}
foreach ($f in $jsonFiles) {
    $name = Split-Path $f -Leaf
    $obj = Read-JsonFile $f
    if ($null -ne $obj) {
        $docs[$name] = $obj
        Add-Ok "JSON 合法: $name"
    }
}

# --- 2. 必需文件存在 ---
$required = @('AGENTS.json','INDEX.json','PROGRESS.json','README.md','KZJ.md','.gitignore','SESSIONS/TEMPLATE.json','scripts/hdcw.sh','scripts/validate_memory.ps1')
foreach ($r in $required) {
    $p = Join-Path $root $r
    if (Test-Path $p) { Add-Ok "存在: $r" } else { Add-Err "缺失必需文件: $r" }
}

# --- 3. INDEX 结构与路径 ---
if ($docs.ContainsKey('INDEX.json')) {
    $idx = $docs['INDEX.json']
    $ids = @{}
    $allKeys = New-Object System.Collections.ArrayList

    foreach ($e in $idx.entries) {
        if ([string]::IsNullOrWhiteSpace($e.id)) { Add-Err "INDEX 条目缺少 id"; continue }
        if ($ids.ContainsKey($e.id)) { Add-Err "INDEX id 重复: $($e.id)" } else { $ids[$e.id] = $true }

        foreach ($k in $e.keys) { [void]$allKeys.Add($k) }

        if (-not [string]::IsNullOrWhiteSpace($e.path)) {
            $target = Join-Path $root $e.path
            if (-not (Test-Path $target)) {
                Add-Err "INDEX 路径不存在: [$($e.id)] -> $($e.path)"
            }
        }
    }

    $dupKeys = $allKeys | Group-Object | Where-Object { $_.Count -gt 1 }
    if ($dupKeys) {
        foreach ($d in $dupKeys) { Add-Warn "INDEX 关键词重复: '$($d.Name)' 出现 $($d.Count) 次" }
    } else {
        Add-Ok "INDEX 关键词唯一（共 $($allKeys.Count) 个）"
    }

    # README 关键词覆盖
    $readmePath = Join-Path $root 'README.md'
    if (Test-Path $readmePath) {
        $readme = Get-Content -Path $readmePath -Raw -Encoding UTF8
        $missing = @()
        foreach ($k in ($allKeys | Select-Object -Unique)) {
            if ($readme -notlike "*$k*") { $missing += $k }
        }
        if ($missing.Count -gt 0) {
            Add-Err ("README 关键词表缺失: " + ($missing -join ', '))
        } else {
            Add-Ok "README 覆盖全部 INDEX 关键词"
        }
    }
}

# --- 4. AGENTS 一致性 ---
if ($docs.ContainsKey('AGENTS.json')) {
    $ag = $docs['AGENTS.json']

    $invIds = @{}
    foreach ($i in $ag.invariants) {
        if ($invIds.ContainsKey($i.id)) { Add-Err "AGENTS invariant id 重复: $($i.id)" } else { $invIds[$i.id] = $true }
        if ([string]::IsNullOrWhiteSpace($i.rule)) { Add-Err "AGENTS invariant $($i.id) 缺少 rule" }
    }
    Add-Ok "AGENTS invariant 共 $($ag.invariants.Count) 条"

    if ($docs.ContainsKey('PROGRESS.json')) {
        $pr = $docs['PROGRESS.json']
        if ($ag.doc.version -ne $pr.current_state.doc_version) {
            Add-Err "版本不一致: AGENTS.doc.version=$($ag.doc.version) / PROGRESS.current_state.doc_version=$($pr.current_state.doc_version)"
        } else {
            Add-Ok "AGENTS 与 PROGRESS 版本一致 ($($ag.doc.version))"
        }
    }

    # 第一优先级待办必须存在
    if ($docs.ContainsKey('PROGRESS.json')) {
        $pr = $docs['PROGRESS.json']
        $p0 = $pr.pending_actions | Where-Object { $_.priority -eq 0 }
        if ($null -eq $p0) { Add-Warn "PROGRESS 没有 priority=0 的待办项" }
        else { Add-Ok "头号待办: $($p0.id) $($p0.task)" }
    }

    # 阻塞项提示
    foreach ($pa in $ag.verification_gates.PSObject.Properties) {
        Write-Host "  验证门 $($pa.Name): $($pa.Value.name)" -ForegroundColor DarkGray
    }
}

# --- 5. 敏感文件未被误建 ---
$secretPatterns = @('*.p12','*.cer','*.p7b','*.jks','*.keystore')
foreach ($sp in $secretPatterns) {
    $found = Get-ChildItem -Path $root -Filter $sp -Recurse -File -ErrorAction SilentlyContinue
    foreach ($f in $found) { Add-Err "发现签名文件，不应入库: $($f.FullName)" }
}

# --- 输出 ---
Write-Host ""
foreach ($m in $passed)   { Write-Host "  [OK]   $m" -ForegroundColor Green }
foreach ($m in $warnings) { Write-Host "  [WARN] $m" -ForegroundColor Yellow }
foreach ($m in $errors)   { Write-Host "  [FAIL] $m" -ForegroundColor Red }

Write-Host ""
if ($errors.Count -gt 0) {
    Write-Host "结果: $($errors.Count) 个错误 / $($warnings.Count) 个警告" -ForegroundColor Red
    exit 1
} else {
    Write-Host "结果: 全部通过（$($passed.Count) 项），$($warnings.Count) 个警告" -ForegroundColor Green
    exit 0
}
