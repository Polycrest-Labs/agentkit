param(
    [string] $Configuration = "Release",
    [string] $OutputDirectory = "artifacts/packages"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src/AgentKit/AgentKit.csproj"
$output = Join-Path $repoRoot $OutputDirectory

dotnet pack $project `
    --configuration $Configuration `
    --output $output
