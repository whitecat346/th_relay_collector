# 构建/运行助手：把 dotnet 需要的临时目录全部重定向进仓库，
# 避免沙箱拒绝写 %LOCALAPPDATA%\Temp 与 %USERPROFILE%\.dotnet 导致的构建失败。
#
#   .\build.ps1                # 普通构建
#   .\build.ps1 -Name reader   # 用独立中间目录构建（多个 agent 并行时避免 obj 打架）
#   .\build.ps1 -Run --link https://www.bilibili.com/opus/123  # 构建后直接运行
param(
    [string]$Name = "",
    [switch]$Release,
    [string[]]$Run = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:TMP = Join-Path $root '.dotnet\tmp'
$env:TEMP = $env:TMP
New-Item -ItemType Directory -Force -Path $env:TMP | Out-Null

$proj = Join-Path $root 'src\JieLi.Tui\JieLi.Tui.csproj'
$args = @('build', $proj, '-v', 'q', '--nologo')
if ($Release) { $args += @('-c', 'Release') }
if ($Name) {
    $args += @("-p:BaseIntermediateOutputPath=obj\$Name\", "-p:BaseOutputPath=bin\$Name\")
}
& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Run.Count -gt 0) {
    $cfg = if ($Release) { 'Release' } else { 'Debug' }
    $bin = if ($Name) { Join-Path $root "src\JieLi.Tui\bin\$Name\$cfg\net10.0\jieli.dll" }
           else { Join-Path $root "src\JieLi.Tui\bin\$cfg\net10.0\jieli.dll" }
    & dotnet $bin @Run
    exit $LASTEXITCODE
}
