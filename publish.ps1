# 发布 relay-tool 为自包含单文件 exe（Windows x64）
#
# 为什么需要重定向 DOTNET_CLI_HOME：
#   本机沙箱不允许写 C:\Users\<用户>\.templateengine，dotnet CLI 需要一个可写的 home。
# 为什么需要 --self-contained：
#   目标机器可能没装 .NET 10 运行时；自包含后这个 exe 直接双击/命令行可用（约 70 MB）。
#
# 用法：pwsh -File publish.ps1
#       （若在受限沙箱内，需为本次命令放开对 Schannel 凭证目录的写入，
#         否则 NuGet 会报 "安全包中没有可用的凭证" SEC_E_NO_CREDENTIALS）

$ErrorActionPreference = 'Stop'
$tool = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_HOME = Join-Path $tool '.dotnet'
$env:DOTNET_NOLOGO = '1'

Write-Host "==> 发布 relay-tool（net10.0 / win-x64 / 自包含单文件）"
dotnet publish (Join-Path $tool 'src\JieLi.Tui\JieLi.Tui.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $tool 'publish') -v q --nologo

$exe = Join-Path $tool 'publish\jieli.exe'
if (-not (Test-Path $exe)) { throw "发布失败：未生成 $exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 完成：$exe （$size MB）"
Write-Host "==> 自检（离线只读审阅）"
& $exe --root (Split-Path -Parent $tool) --plain | Select-Object -First 5
