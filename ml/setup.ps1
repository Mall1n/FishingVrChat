[CmdletBinding()]
param(
    [string]$PythonCommand = "python",
    [ValidateSet("cu128", "cu126", "cpu")]
    [string]$TorchBuild = "cu128"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$venvPath = Join-Path $repositoryRoot ".venv"
$venvPython = Join-Path $venvPath "Scripts\python.exe"
$requirementsPath = Join-Path $PSScriptRoot "requirements.txt"

$versionOutput = & $PythonCommand --version 2>&1
if ($LASTEXITCODE -ne 0 -or "$versionOutput" -notmatch "^Python 3\.(1[0-4])") {
    throw "Python 3.10-3.14 was not found. Install Python 3.12 and reopen the terminal."
}

if (-not (Test-Path -LiteralPath $venvPython)) {
    Write-Host "Creating virtual environment: $venvPath"
    & $PythonCommand -m venv $venvPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create virtual environment."
    }
}
else {
    Write-Host "Reusing virtual environment: $venvPath"
}

& $venvPython -m pip install --upgrade pip
$torchReady = & $venvPython -c "import torch; raise SystemExit(0 if torch.cuda.is_available() else 1)" 2>$null
if ($LASTEXITCODE -ne 0) {
    $torchIndex = "https://download.pytorch.org/whl/$TorchBuild"
    & $venvPython -m pip install torch torchvision --index-url $torchIndex
}
else {
    Write-Host "Reusing the installed CUDA build of PyTorch."
}
& $venvPython -m pip install --requirement $requirementsPath

& $venvPython -c "import torch; print('PyTorch:', torch.__version__); print('CUDA available:', torch.cuda.is_available()); print('GPU:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'CPU')"
if ($LASTEXITCODE -ne 0) {
    throw "ML environment verification failed."
}

Write-Host "ML environment is ready. Python: $venvPython"
