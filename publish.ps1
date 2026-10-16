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

$projDir = Join-Path $tool 'src\JieLi.Tui'
$proj = Join-Path $projDir 'JieLi.Tui.csproj'

# 守卫：同一目录若存在第二个 .csproj/.slnx，`dotnet build <目录>` 会报
# MSB1050「文件夹中包含多个项目或解决方案文件」。本脚本因为显式指定 .csproj 不受影响，
# 但为了不让后来者踩坑，这里直接把重复文件点名报出来。
$dupes = Get-ChildItem $projDir -File -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -ne 'JieLi.Tui.csproj' -and $_.Extension -in '.csproj', '.slnx', '.sln' }
if ($dupes) {
    Write-Warning ("src\JieLi.Tui 下存在多余的工程文件：{0}" -f (($dupes | ForEach-Object Name) -join ', '))
    Write-Warning "  它们会让 'dotnet build src\JieLi.Tui' 报 MSB1050（多项目歧义）。"
    Write-Warning "  本脚本显式指定了 JieLi.Tui.csproj，所以仍能正常发布；建议删除多余文件。"
}

Write-Host "==> 发布 relay-tool（net10.0 / win-x64 / 自包含单文件）"
dotnet publish $proj `
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
