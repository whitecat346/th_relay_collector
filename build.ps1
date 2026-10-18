# 构建/运行助手：把 dotnet 需要的临时目录全部重定向进仓库，
# 避免沙箱拒绝写 %LOCALAPPDATA%\Temp 与 %USERPROFILE%\.dotnet 导致的构建失败。
#
#   .\build.ps1                # 普通构建（bin/Debug、obj/）
#   .\build.ps1 -Name reader   # 独立中间目录（.build/<name>/），多个 agent 并行时互不干扰
#   .\build.ps1 -Run --link https://www.bilibili.com/opus/123  # 构建后直接运行
#
# 注意：-Name 模式把自己的 obj/bin **放到工程目录之外**（.build/<name>/）。
# 早先放进 src\JieLi.Tui\obj\<name>\ 会被 SDK 的 **/*.cs 通配当成源码，
# 两个 agent 先后构建就撞出 CS0579「特性重复」。
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
$buildArgs = @('build', $proj, '-v', 'q', '--nologo')
if ($Release) { $buildArgs += @('-c', 'Release') }

if ($Name) {
    $objDir = Join-Path $root ".build\obj\$Name\"
    $outDir = Join-Path $root ".build\bin\$Name\"
    New-Item -ItemType Directory -Force -Path $objDir, $outDir | Out-Null
    $buildArgs += @(
        "-p:BaseIntermediateOutputPath=$objDir",
        "-p:MSBuildProjectExtensionsPath=$objDir",
        "-p:BaseOutputPath=$outDir"
    )
    # 覆盖 BaseIntermediateOutputPath 后，SDK 的默认排除项只认新路径，
    # 工程内遗留的 obj\ / bin\ 会被 **/*.cs 当源码编进去 → CS0579「特性重复」。
    # 必须用**环境变量**设置 DefaultItemExcludes（普通属性）：命令行 -p: 是全局属性，
    # 不可再被 SDK 的 props 追加，实测无效；环境变量则会被 SDK 正常追加，实测有效。
    $env:DefaultItemExcludes = 'obj\**;bin\**;.build\**'
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Run.Count -gt 0) {
    $cfg = if ($Release) { 'Release' } else { 'Debug' }
    $dll = if ($Name) { Join-Path $root ".build\bin\$Name\$cfg\net10.0\jieli.dll" }
           else { Join-Path $root "src\JieLi.Tui\bin\$cfg\net10.0\jieli.dll" }
    & dotnet $dll @Run
    exit $LASTEXITCODE
}
