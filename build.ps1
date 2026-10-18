# 多合一启动图字体UI替换器 - 一键构建脚本
#
# 用途：把 WinUI3 应用打包成"单一可执行文件"。
#
# 为什么不用 dotnet publish -p:PublishSingleFile=true：
#   Windows App SDK 自包含模式依赖 exe 同目录的 WinRT 激活清单（Microsoft.UI.Xaml 等
#   activatableClass 项）。.NET 单文件会把运行时解压到临时目录，但进程路径仍指向原始
#   exe，激活工厂找不到 DLL，启动即报 0x80040111（ClassFactory 无法响应）。
#   因此改用"完整目录 + 单文件自解压启动器"：
#     1) 发布完整应用目录           -> build\app
#     2) 把目录压缩为 payload.zip   -> src\MultiReplacer.Bootstrapper\payload.zip
#     3) 编译内嵌 payload 的启动器  -> build\dist\多合一启动图字体UI替换器.exe
#
# 用法：
#   build.cmd                                   （推荐，自动绕过执行策略）
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipAppPublish   （只重打包启动器）

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Platform = 'x64',
    [switch]$SkipAppPublish
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $root 'src\MultiReplacer.App\MultiReplacer.App.csproj'
$bootProj = Join-Path $root 'src\MultiReplacer.Bootstrapper\MultiReplacer.Bootstrapper.csproj'
$appOut = Join-Path $root 'build\app'
$distOut = Join-Path $root 'build\dist'
$payload = Join-Path $root 'src\MultiReplacer.Bootstrapper\payload.zip'
$exeName = '多合一启动图字体UI替换器.exe'

function Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# 目录 -> zip：优先用 Python（压缩率更好），没有则回退到 .NET 的 ZipArchive。
function Pack-Directory([string]$source, [string]$destination) {
    $python = $null
    foreach ($candidate in @('python', 'py')) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($cmd) {
            try {
                $ver = & $cmd.Source --version 2>&1
                if ($LASTEXITCODE -eq 0 -and $ver -match 'Python\s+3') { $python = $cmd.Source; break }
            } catch { }
        }
    }

    if ($python) {
        & $python (Join-Path $root 'tools\pack-payload.py') $source $destination
        if ($LASTEXITCODE -eq 0) { return }
        Write-Warning 'Python 打包失败，回退到 PowerShell 打包。'
    }

    if (Test-Path $destination) { Remove-Item $destination -Force }
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $base = (Resolve-Path $source).Path.TrimEnd('\')
    $zip = [System.IO.Compression.ZipFile]::Open($destination, 'Create')
    try {
        foreach ($file in (Get-ChildItem -Path $base -Recurse -File)) {
            $rel = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $rel,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
    Write-Host ("    打包完成：{0:N1} MB" -f ((Get-Item $destination).Length / 1MB))
}

# ---- 0. 检查 SDK ----
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    throw '未找到 dotnet。请先安装 .NET 8 SDK，或把 SDK 目录加入 PATH。'
}
$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^8\.')) {
    throw "需要 .NET 8 SDK，当前已安装：`n$sdks"
}

# ---- 1. 发布应用目录 ----
if (-not $SkipAppPublish) {
    Step "发布 WinUI3 应用（$Configuration / $Runtime）"
    if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force }
    & dotnet publish $appProj -c $Configuration -r $Runtime -p:Platform=$Platform -p:DebugType=none -o $appOut
    if ($LASTEXITCODE -ne 0) { throw '应用发布失败。' }
}

if (-not (Test-Path (Join-Path $appOut $exeName))) {
    throw "发布目录缺少主程序：$appOut\$exeName"
}

# ---- 2. 生成内嵌 payload ----
Step '生成 payload.zip'
Pack-Directory $appOut $payload

# ---- 3. 编译单文件启动器 ----
Step '编译单文件启动器（内嵌 payload）'
if (-not (Test-Path $distOut)) { New-Item -ItemType Directory -Path $distOut | Out-Null }
& dotnet publish $bootProj -c $Configuration -r $Runtime -o $distOut
if ($LASTEXITCODE -ne 0) { throw '启动器发布失败。' }

# ---- 4. 目录版压缩包（可选分发形态）----
Step '生成目录版压缩包'
$folderZip = Join-Path $distOut '多合一启动图字体UI替换器-目录版.zip'
Pack-Directory $appOut $folderZip

# ---- 汇总 ----
$exePath = Join-Path $distOut $exeName
Step '完成'
Write-Host ("单文件：{0}  ({1:N1} MB)" -f $exePath, ((Get-Item $exePath).Length / 1MB)) -ForegroundColor Green
Write-Host ("目录版：{0}  ({1:N1} MB)" -f $folderZip, ((Get-Item $folderZip).Length / 1MB))
