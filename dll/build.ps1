# Builds dll\build\Release\dcb.dll with the VS 2022 Build Tools' bundled CMake.
param([string]$Config = 'Release')

$cmake = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$src   = $PSScriptRoot
$build = Join-Path $src 'build'

& $cmake -S $src -B $build -G 'Visual Studio 17 2022' -A x64 | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
& $cmake --build $build --config $Config -- /nologo /v:minimal
if ($LASTEXITCODE) { throw 'cmake build failed' }
Join-Path $build "$Config\dcb.dll"
