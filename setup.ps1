# ================================
# Audiobooks - Automatic Setup (PowerShell)
# ================================
# - Ensures Python 3.12 (per-user, silent)
# - Creates .venv and installs deps
# - Prefers CUDA torch if NVIDIA GPU detected; else CPU torch
# - Pins NumPy 1.26.4 (binary wheel) for Kokoro compatibility
# - Installs GUI + text parsers + Kokoro
# - Verifies PySide6, Torch, Kokoro imports
# ================================

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "Running PowerShell setup..." -ForegroundColor Cyan

function Get-ProjectRoot {
  if ($PSScriptRoot) { return $PSScriptRoot }
  $p = $MyInvocation.MyCommand.Path
  if (-not $p) { $p = (Get-Location).Path }
  return Split-Path -Path $p -Parent
}
$Root = Get-ProjectRoot
Write-Host "Using setup path: $Root"

function Invoke-Download($Url, $OutFile) {
  Write-Host "Downloading: $Url"
  $wc = New-Object System.Net.WebClient
  $wc.Headers.Add("User-Agent","Mozilla/5.0")
  $wc.DownloadFile($Url, $OutFile)
}

function Assert-File($Path, $Msg) {
  if (-not (Test-Path $Path)) { throw $Msg }
}

function Ensure-Python312 {
  # Try py launcher
  try {
    $ok = & py -3.12 -c "import sys; print('OK')" 2>$null
    if ($LASTEXITCODE -eq 0) { return "py -3.12" }
  } catch {}

  # Try python in PATH
  try {
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd) {
      $v = & python -c "import sys; print(sys.version_info[:2])" 2>$null
      if ($LASTEXITCODE -eq 0 -and $v -match "^\(3, 12\)") { return "python" }
    }
  } catch {}

  Write-Host "No system Python 3.12 found. Installing per-user..." -ForegroundColor Yellow
  $tmp = Join-Path $env:TEMP "python-3.12.8-amd64.exe"
  $url = "https://www.python.org/ftp/python/3.12.8/python-3.12.8-amd64.exe"
  Invoke-Download $url $tmp
  $args = @(
    "/quiet","InstallAllUsers=0","Include_pip=1","PrependPath=1","Include_test=0","SimpleInstall=1","Shortcuts=0"
  )
  $p = Start-Process -FilePath $tmp -ArgumentList $args -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "Python installer failed with exit code $($p.ExitCode)" }

  # Re-check
  try {
    $ok = & py -3.12 -c "import sys; print('OK')" 2>$null
    if ($LASTEXITCODE -eq 0) { return "py -3.12" }
  } catch {}
  try {
    $ok = & python -c "import sys; print('OK')" 2>$null
    if ($LASTEXITCODE -eq 0) { return "python" }
  } catch {}
  throw "Python 3.12 installed but not found on PATH. Restart the console, then re-run."
}
$sysPyCmd = Ensure-Python312
Write-Host "Using system Python command: $sysPyCmd"

# -------- venv --------
$venvPath = Join-Path $Root ".venv"
$venvPy   = Join-Path $venvPath "Scripts\python.exe"
if (-not (Test-Path $venvPy)) {
  Write-Host "Creating virtual environment (.venv)..."
  & $sysPyCmd -m venv "$venvPath"
}
Assert-File $venvPy "Virtual environment creation failed."
& $venvPy -m pip install --upgrade pip setuptools wheel

# -------- GPU detection (no ternary) --------
function Has-NvidiaGPU {
  try {
    $nvsmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($nvsmi) { return $true }
  } catch {}
  try {
    $gpus = Get-WmiObject Win32_VideoController | Select-Object -ExpandProperty Name
    if ($gpus -match "NVIDIA") { return $true }
  } catch {}
  return $false
}
$hasCuda = Has-NvidiaGPU
$gpuMsg = if ($hasCuda) { "Yes" } else { "No" }
Write-Host "NVIDIA GPU detected: $gpuMsg"

# -------- Core deps --------
Write-Host "Installing NumPy 1.26.4 (binary wheel only)..."
& $venvPy -m pip install --only-binary=:all: --upgrade "numpy==1.26.4"

Write-Host "Installing GUI and text parsers..."
$guiDeps = @(
  "PySide6>=6.7,<7.0",
  "EbookLib==0.19",
  "beautifulsoup4",
  "pypdf>=4,<5",
  "python-docx>=1.1.2",
  "requests",
  "lxml"
)
& $venvPy -m pip install --upgrade @guiDeps

# -------- Torch (CUDA if possible) --------
function Install-Torch([bool]$PreferCUDA) {
  # remove any prior torch first to avoid conflicts
  & $venvPy -m pip uninstall -y torch torchvision torchaudio 2>$null | Out-Null

  if ($PreferCUDA) {
    Write-Host "Installing PyTorch (CUDA)..." -ForegroundColor Cyan
    try {
      & $venvPy -m pip install --upgrade --index-url https://download.pytorch.org/whl/cu121 torch torchvision torchaudio
      if ($LASTEXITCODE -eq 0) { return $true }
    } catch {}
    Write-Host "CUDA torch failed. Falling back to CPU..." -ForegroundColor Yellow
  }

  Write-Host "Installing PyTorch (CPU)..." -ForegroundColor Cyan
  & $venvPy -m pip install --upgrade --index-url https://download.pytorch.org/whl/cpu torch torchvision torchaudio
  return ($LASTEXITCODE -eq 0)
}
$okTorch = Install-Torch $hasCuda
if (-not $okTorch) { throw "PyTorch installation failed." }

# -------- Kokoro + deps --------
Write-Host "Installing Kokoro dependencies..."
$kDeps = @(
  "huggingface-hub>=0.22",
  "transformers>=4.40,<4.56",
  "loguru>=0.7,<1.0",
  "safetensors>=0.4,<1.0",
  "phonemizer-fork>=3.3"
)
& $venvPy -m pip install --upgrade @kDeps

$kokoroOk = $false
try {
  Write-Host "Installing Kokoro (PyPI)..."
  & $venvPy -m pip install --upgrade "kokoro==0.9.4"
  if ($LASTEXITCODE -eq 0) { $kokoroOk = $true }
} catch {}

if (-not $kokoroOk) {
  Write-Host "PyPI Kokoro failed; attempting Git fallback..." -ForegroundColor Yellow

  # PortableGit
  $gitRoot = Join-Path $Root "PortableGit"
  $gitExe  = Join-Path $gitRoot "cmd\git.exe"
  if (-not (Test-Path $gitExe)) {
    Write-Host "Fetching PortableGit..."
    New-Item -ItemType Directory -Force -Path $gitRoot | Out-Null
    $pkg = Join-Path $gitRoot "PortableGit-2.45.2-64-bit.7z.exe"
    $gitUrl = "https://github.com/git-for-windows/git/releases/download/v2.45.2.windows.1/PortableGit-2.45.2-64-bit.7z.exe"
    Invoke-Download $gitUrl $pkg
    $payload = Join-Path $gitRoot "payload"
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    Start-Process -FilePath $pkg -ArgumentList "-y","-o$payload" -Wait
    Get-ChildItem -Path $payload | ForEach-Object { Move-Item $_.FullName -Destination $gitRoot -Force }
    Remove-Item $payload -Recurse -Force
  }

  if (Test-Path $gitExe) {
    $env:GIT_ASKPASS = 'echo'
    $env:SSH_ASKPASS = 'echo'
    $env:PATH = (Split-Path $gitExe -Parent) + ";" + $env:PATH

    try {
      Write-Host "Installing Misaki (Git)..."
      & $venvPy -m pip install "git+https://github.com/hexgrad/Misaki.git@main#egg=misaki[en]"
    } catch {}

    Write-Host "Installing Kokoro (Git)..."
    & $venvPy -m pip install "git+https://github.com/hexgrad/Kokoro-TTS.git@main#egg=kokoro"
    if ($LASTEXITCODE -eq 0) { $kokoroOk = $true }
  } else {
    Write-Host "[WARN] PortableGit missing; skipping Git fallback." -ForegroundColor Yellow
  }
}

if (-not $kokoroOk) {
  Write-Host "[WARN] Kokoro could not be installed automatically. You can still run the GUI and install later with:"
  Write-Host "       .\.venv\Scripts\pip.exe install kokoro==0.9.4"
}

# -------- Simple verifiers (temp .py files) --------
function Py-Check($pycode, $label) {
  $tmp = Join-Path $env:TEMP ("verify_" + [guid]::NewGuid().ToString("N") + ".py")
  Set-Content -LiteralPath $tmp -Value $pycode -Encoding UTF8
  try {
    $out = & $venvPy $tmp *>&1
    if ($LASTEXITCODE -eq 0) {
      Write-Host "$label OK"
    } else {
      Write-Host "$label FAILED:" -ForegroundColor Yellow
      Write-Host ($out | Out-String)
    }
  } finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  }
}

Write-Host "Verifying PySide6 import..."
Py-Check 'from PySide6 import QtCore; print("PySide6 OK")' "PySide6"

Write-Host "Verifying Torch/CUDA..."
Py-Check @'
import torch
print("Torch:", getattr(torch, "__version__", "?"))
print("Built with CUDA:", getattr(torch.version, "cuda", None))
print("CUDA available:", torch.cuda.is_available())
if torch.cuda.is_available():
    print("Device:", torch.cuda.get_device_name(0))
'@ "Torch"

Write-Host "Verifying Kokoro import..."
Py-Check 'import kokoro; from kokoro.pipeline import KPipeline; print("Kokoro OK")' "Kokoro"

Write-Host ""
Write-Host "[OK] Setup finished. You can close this window and run start_audiobooks.bat." -ForegroundColor Green
