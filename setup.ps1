# ============================================
# Audiobooks - setup.ps1
# Fix: keep a matched CUDA PyTorch trio and prevent later downgrades
# Safe to re-run.
# ============================================
$ErrorActionPreference = 'Stop'
Write-Host "Running PowerShell setup..."

function Get-SetupRoot {
  try {
    if ($PSCommandPath) { return Split-Path -LiteralPath $PSCommandPath -Parent }
    if ($MyInvocation.MyCommand.Path) { return Split-Path -LiteralPath $MyInvocation.MyCommand.Path -Parent }
  } catch {}
  return (Get-Location).Path
}
$Root = Get-SetupRoot
Write-Host "Using setup path: $Root"

# ---- Locate a system Python (3.12/3.13 preferred) ----
function Get-SystemPython {
  $candidates = @("python", "py -3.12", "py -3.13")
  foreach ($cmd in $candidates) {
    try {
      $v = & $cmd -V 2>$null
      if ($LASTEXITCODE -eq 0) { return $cmd }
    } catch {}
  }
  return $null
}
$sysPyCmd = Get-SystemPython
if (-not $sysPyCmd) {
  Write-Host "[X] No system Python found. Please install Python 3.12 or 3.13 from python.org first."
  throw "Python not available."
}
try {
  $ver = & $sysPyCmd -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')"
  Write-Host "Python $ver"
} catch {}
Write-Host "Using system Python command: $sysPyCmd"

# ---- Ensure venv ----
$venvPath = Join-Path $Root ".venv"
$venvPy   = Join-Path $venvPath "Scripts\python.exe"
if (-not (Test-Path $venvPy)) {
  Write-Host "Creating virtual environment (.venv)..."
  & $sysPyCmd -m venv "$venvPath"
}

# ---- Base packaging tools ----
& $venvPy -m pip install --upgrade pip setuptools wheel

# ---- Detect NVIDIA (CUDA) ----
function Test-NvidiaAvailable {
  try {
    $nvsmi = (Get-Command "nvidia-smi" -ErrorAction SilentlyContinue)
    if ($nvsmi) { return $true }
  } catch {}
  return $false
}
$hasCuda = Test-NvidiaAvailable
if ($hasCuda) { Write-Host "NVIDIA GPU detected." } else { Write-Host "No NVIDIA GPU detected. CPU-only PyTorch will be used." }

# ---- Helper: uninstall-if-present; clean any stuck ~orch dirs ----
function Uninstall-IfPresent {
  param([string]$Pkg)
  try {
    & $venvPy -m pip show $Pkg 1>$null 2>$null
    if ($LASTEXITCODE -eq 0) { & $venvPy -m pip uninstall -y $Pkg | Out-Null }
  } catch { }
}
function Remove-StuckTorch {
  param([string]$SitePkgs)
  if (-not (Test-Path $SitePkgs)) { return }
  Get-ChildItem -LiteralPath $SitePkgs -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.PSIsContainer -and $_.Name -like "~orch*" } |
    ForEach-Object {
      try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    }
}
# site-packages path
$sitePkgs = ""
try { $sitePkgs = & $venvPy -c "import sysconfig; print(sysconfig.get_paths().get('purelib',''))" } catch {}
if ($sitePkgs) { Remove-StuckTorch -SitePkgs $sitePkgs }

# ======================================================
# 1) Install a MATCHED PyTorch trio (torch/vision/audio)
#    Pick CUDA 12.1 wheels when NVIDIA is present.
#    Install AS A LOCKED SET to avoid resolver downgrades.
# ======================================================
$TORCH_VER   = "2.5.1"
$VISION_VER  = "0.20.1"
$AUDIO_VER   = "2.5.1"
if ($hasCuda) {
  $TAG = "cu121"
  $INDEX = "https://download.pytorch.org/whl/cu121"
} else {
  $TAG = "cpu"
  $INDEX = "https://download.pytorch.org/whl/cpu"
}

# Remove any existing trio first
Uninstall-IfPresent -Pkg "torchaudio"
Uninstall-IfPresent -Pkg "torchvision"
Uninstall-IfPresent -Pkg "torch"
Start-Sleep -Seconds 1
if ($sitePkgs) { Remove-StuckTorch -SitePkgs $sitePkgs }

Write-Host "Installing PyTorch set ($TORCH_VER+$TAG) as a locked trio…"
& $venvPy -m pip install --index-url $INDEX --force-reinstall --no-deps `
  "torch==$TORCH_VER+$TAG" `
  "torchvision==$VISION_VER+$TAG" `
  "torchaudio==$AUDIO_VER+$TAG"

# ======================================================
# 2) Pin Transformers/Tokenizers/HF Hub compatible with Kokoro
# ======================================================
& $venvPy -m pip install --upgrade --no-cache-dir `
  "transformers==4.55.0" `
  "tokenizers==0.21.4" `
  "huggingface-hub==0.34.4"

# ======================================================
# 3) Install the rest of requirements, but FILTER OUT packages that would fight our pins:
#    torch, torchvision, torchaudio, transformers, tokenizers, huggingface-hub, numpy
# ======================================================
$reqTxt = Join-Path $Root "requirements.txt"
if (Test-Path $reqTxt) {
  $raw = Get-Content -LiteralPath $reqTxt -ErrorAction SilentlyContinue
  $filtered = @()
  foreach ($line in $raw) {
    $t = $line.Trim()
    if ($t -eq "" -or $t.StartsWith("#")) { $filtered += $line; continue }
    $lower = $t.ToLowerInvariant()
    if ($lower -match "^(torch|torchvision|torchaudio)\b") { continue }
    if ($lower -match "^(transformers|tokenizers|huggingface-hub)\b") { continue }
    if ($lower -match "^numpy\b") { continue } # avoid forcing a build-from-source downgrade
    $filtered += $line
  }
  $tmpReq = Join-Path $Root "requirements.filtered.txt"
  Set-Content -LiteralPath $tmpReq -Value $filtered -Encoding UTF8
  Write-Host "Installing filtered requirements (skipping torch/vision/audio/transformers/tokenizers/hf-hub/numpy)…"
  & $venvPy -m pip install --upgrade --no-cache-dir -r "$tmpReq"
} else {
  # Minimal extras
  & $venvPy -m pip install --upgrade --no-cache-dir EbookLib beautifulsoup4 pypdf python-docx requests PySide6
}

# ======================================================
# 4) Final verification (don’t use heredocs; run simple -c blocks)
# ======================================================
try {
  $torchReport = & $venvPy -c "import json,torch; print(json.dumps({'torch':getattr(torch,'__version__','?'),'build_cuda':getattr(getattr(torch,'version',None),'cuda',None),'cuda_available':bool(getattr(torch,'cuda',None) and torch.cuda.is_available())}))"
  Write-Host "Torch report: $torchReport"
} catch { Write-Host "Torch report: (unavailable)" }

try {
  & $venvPy -c "import transformers; from transformers import AlbertModel; print('Transformers OK:', transformers.__version__)"
} catch {
  Write-Host "WARNING: Transformers import check failed. Reinstalling compatible pins…"
  & $venvPy -m pip install --force-reinstall --no-deps "transformers==4.55.0" "tokenizers==0.21.4" "huggingface-hub==0.34.4"
}

Write-Host "[OK] Setup finished. You can close this window and run start_audiobooks.bat."
