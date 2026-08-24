param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $Framework = 'net11.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$patcherProject = Join-Path $repoRoot 'OTAPI.Patcher/OTAPI.Patcher.csproj'
$patcherOutput = Join-Path $repoRoot "OTAPI.Patcher/bin/$Configuration/$Framework"
$launcherOutput = Join-Path $repoRoot "OTAPI.Server.Launcher/bin/$Configuration/$Framework"

Push-Location $repoRoot
try {
    dotnet restore OTAPI.Mods.slnf
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet restore OTAPI.Server.Launcher.slnf
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build OTAPI.Mods.slnf --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-Path -LiteralPath $patcherOutput -PathType Container)) {
        throw "Patcher output directory was not created: $patcherOutput"
    }

    Push-Location $patcherOutput
    try {
        dotnet run --project $patcherProject --configuration $Configuration --no-build -- -patchTarget=p -latest=n -framework=$Framework
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally {
        Pop-Location
    }

    dotnet build OTAPI.Server.Launcher.slnf --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-Path -LiteralPath (Join-Path $launcherOutput 'OTAPI.Server.Launcher.dll') -PathType Leaf)) {
        throw "Server launcher was not built: $launcherOutput"
    }

    Push-Location $launcherOutput
    try {
        dotnet OTAPI.Server.Launcher.dll -test-init
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally {
        Pop-Location
    }
}
finally {
    Pop-Location
}
