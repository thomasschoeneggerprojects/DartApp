param(
    [switch] $Run,
    [string] $Device,
    [ValidateSet('android-arm64', 'android-arm', 'android-x64', 'android-x86')]
    [string] $RuntimeIdentifier = 'android-arm64'
)

$ErrorActionPreference = 'Stop'

$shortTemp = (& $env:ComSpec /d /c "for %I in (`"$env:TEMP`") do @echo %~sI").Trim()
if (-not $shortTemp) {
    throw 'Could not resolve an Android-safe temporary build path.'
}

$project = Join-Path $PSScriptRoot 'DartForge.App\DartForge.App.csproj'

if ($Run) {
    $adb = Get-Command adb -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $adb) {
        $adb = Join-Path ${env:ProgramFiles(x86)} 'Android\android-sdk\platform-tools\adb.exe'
    }
    if (-not (Test-Path -LiteralPath $adb)) {
        throw 'adb was not found. Install Android platform-tools or add adb to PATH.'
    }

    $adbArguments = if ($Device) { @('-s', $Device) } else { @() }
    $abiOutput = & $adb @adbArguments shell getprop ro.product.cpu.abi
    if ($LASTEXITCODE -ne 0 -or -not $abiOutput) {
        throw 'The Android device is unavailable or unauthorized. Connect it, enable USB debugging, and accept the authorization prompt.'
    }
    $abi = ($abiOutput | Out-String).Trim()

    $RuntimeIdentifier = switch ($abi) {
        'arm64-v8a'   { 'android-arm64' }
        'armeabi-v7a' { 'android-arm' }
        'x86_64'      { 'android-x64' }
        'x86'         { 'android-x86' }
        default       { throw "Unsupported Android ABI: $abi" }
    }
}

$intermediateRoot = Join-Path $shortTemp "DartForge\$RuntimeIdentifier\obj"
$buildArguments = @(
    'build'
    $project
    '-f', 'net9.0-android'
    '-c', 'Debug'
    '-m:1'
    "-p:DartForgeIntermediateRoot=$intermediateRoot"
)
$buildArguments += "-p:RuntimeIdentifier=$RuntimeIdentifier"

dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not $Run) {
    exit 0
}

$apk = Join-Path $PSScriptRoot "DartForge.App\bin\Debug\net9.0-android\$RuntimeIdentifier\com.dartforge.app-Signed.apk"
& $adb @adbArguments install -r --no-incremental $apk
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $adb @adbArguments shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1
exit $LASTEXITCODE
