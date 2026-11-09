<#
  Builds the signed Google Play release of DartTA.

  Output (PlayStore\release):
    DartTA-<version>-<code>.aab   -> upload this to Google Play Console
    DartTA-<version>-<code>.apk   -> universal signed APK for sideloading / testing

  The upload key lives in PlayStore\signing (git-ignored). It is created on the first run.
  BACK UP PlayStore\signing (keystore + password file). Without it you can no longer ship updates
  unless you request an upload-key reset in Play Console.

  Usage:
    ./build-release.ps1                              # version from the csproj
    ./build-release.ps1 -VersionName 1.0.1 -VersionCode 2
#>
param(
    [string] $VersionName,
    [int]    $VersionCode = 0
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$project   = Join-Path $root 'DartForge.App\DartForge.App.csproj'
$signDir   = Join-Path $root 'PlayStore\signing'
$outDir    = Join-Path $root 'PlayStore\release'
$keystore  = Join-Path $signDir 'dartta-upload.p12'
$passFile  = Join-Path $signDir 'upload-key-password.txt'
$alias     = 'dartta-upload'

New-Item -ItemType Directory -Force -Path $signDir, $outDir | Out-Null

# ---- locate keytool (JDK that ships with Visual Studio / Android tooling) -------------------
function Find-Keytool {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += Join-Path $env:JAVA_HOME 'bin\keytool.exe' }
    $candidates += Get-ChildItem 'C:\Program Files\Microsoft\jdk-*\bin\keytool.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | ForEach-Object FullName
    $candidates += Get-ChildItem 'C:\Program Files\Android\openjdk\*\bin\keytool.exe' -ErrorAction SilentlyContinue | ForEach-Object FullName
    $candidates += Get-ChildItem 'C:\Program Files\Eclipse Adoptium\*\bin\keytool.exe' -ErrorAction SilentlyContinue | ForEach-Object FullName
    $cmd = Get-Command keytool -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

# ---- create the upload key on first run ------------------------------------------------------
if (-not (Test-Path $keystore)) {
    $keytool = Find-Keytool
    if (-not $keytool) { throw 'keytool.exe not found. Install the Microsoft OpenJDK 17 (comes with Visual Studio Android tools) or set JAVA_HOME.' }
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'.ToCharArray()
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $pass  = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    Set-Content -Path $passFile -Value $pass -NoNewline -Encoding ascii
    Write-Host "Creating upload keystore $keystore" -ForegroundColor Cyan
    # Run keytool non-interactively (stdin = NUL) so it can never wait for input, and keep its
    # stderr chatter from being treated as a PowerShell error.
    $ktArgs = "-genkeypair -noprompt -keystore `"$keystore`" -storetype PKCS12 -alias $alias -keyalg RSA -keysize 4096 -validity 10000 -storepass $pass -keypass $pass -dname `"CN=DartTA, OU=Mobile, O=DartTA, L=Vienna, C=AT`""
    $empty = Join-Path $signDir 'stdin-empty.txt'; Set-Content -Path $empty -Value '' -NoNewline
    $p = Start-Process -FilePath $keytool -ArgumentList $ktArgs -NoNewWindow -Wait -PassThru `
        -RedirectStandardInput $empty `
        -RedirectStandardOutput (Join-Path $signDir 'keytool.out.txt') -RedirectStandardError (Join-Path $signDir 'keytool.err.txt')
    if ($p.ExitCode -ne 0 -or -not (Test-Path $keystore)) { Get-Content (Join-Path $signDir 'keytool.err.txt'); throw 'keytool failed.' }
    Remove-Item (Join-Path $signDir 'keytool.*.txt'), $empty -ErrorAction SilentlyContinue
    @"
DartTA upload key
=================
Keystore : dartta-upload.p12 (PKCS12)
Alias    : $alias
Password : see upload-key-password.txt (store + key password are identical)
Created  : $(Get-Date -Format 'yyyy-MM-dd HH:mm')

BACK UP THIS FOLDER (e.g. password manager + offline copy). Never commit it to git.
With Play App Signing, Google holds the app signing key; this is only the UPLOAD key.
If it is lost, request an upload key reset in Play Console > Test and release > App integrity.
"@ | Set-Content -Path (Join-Path $signDir 'README.txt') -Encoding utf8
}
if (-not (Test-Path $passFile)) { throw "Missing $passFile (password for the existing keystore)." }
$env:DARTTA_KEY_PASS = (Get-Content $passFile -Raw).Trim()

# ---- toolchain: target API 36 needs platforms\android-36 + JDK 17 ------------------------------
# Android build tools (aapt2, mono-aot-cross) fail on non-ASCII paths (e.g. a user name with an umlaut),
# so the SDK, NuGet cache, temp and obj folders used for the release build all live in an ASCII path.
$buildRoot = Join-Path (Split-Path $root -Parent) '.release-build'
$sdkCandidates = @((Join-Path (Split-Path $root -Parent) 'AndroidSdk'), "${env:ProgramFiles(x86)}\Android\android-sdk", "$env:ProgramFiles\Android\android-sdk", "$env:LOCALAPPDATA\Android\Sdk")
$androidSdk = $sdkCandidates | Where-Object { $_ -match '^[\x00-\x7F]+$' -and (Test-Path (Join-Path $_ 'platforms\android-36\android.jar')) } | Select-Object -First 1
if (-not $androidSdk) { throw 'Android SDK platform 36 not found in an ASCII path. Install it: sdkmanager --sdk_root=C:\DartForge\AndroidSdk "platforms;android-36" "build-tools;35.0.0" "platform-tools"' }
$jdk = @($env:JAVA_HOME) + (Get-ChildItem 'C:\Program Files (x86)\Android\openjdk\jdk-17*', 'C:\Program Files\Android\jdk\*', 'C:\Program Files\Microsoft\jdk-17*', 'C:\Program Files\Microsoft\jdk-21*', 'C:\Program Files\Android\Android Studio\jbr' -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\javac.exe')) -and ($_ -notmatch 'jdk-11|jdk-1\.8') } | Select-Object -First 1
Write-Host "Android SDK: $androidSdk"
Write-Host "JDK        : $jdk"

# ---- build -----------------------------------------------------------------------------------
$intermediateRoot = Join-Path $buildRoot 'obj'
New-Item -ItemType Directory -Force -Path $intermediateRoot, (Join-Path $buildRoot 'tmp'), (Join-Path $buildRoot 'nuget') | Out-Null
$env:TEMP = $env:TMP = Join-Path $buildRoot 'tmp'
$env:NUGET_PACKAGES = Join-Path $buildRoot 'nuget'

$buildArgs = @(
    'publish', $project,
    '-f', 'net9.0-android',
    '-c', 'Release',
    '-m:1', '--disable-build-servers',
    "-p:DartForgeIntermediateRoot=$intermediateRoot",
    "-p:DartTAKeyStore=$keystore",
    "-p:DartTAKeyAlias=$alias",
    "-p:AndroidSdkDirectory=$androidSdk"
)
if ($jdk) { $buildArgs += "-p:JavaSdkDirectory=$jdk" }
if ($VersionName) { $buildArgs += "-p:ApplicationDisplayVersion=$VersionName" }
if ($VersionCode -gt 0) { $buildArgs += "-p:ApplicationVersion=$VersionCode" }

$ErrorActionPreference = 'Continue'
dotnet @buildArgs 2>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
Remove-Item Env:\DARTTA_KEY_PASS -ErrorAction SilentlyContinue
if ($code -ne 0) { exit $code }

# ---- collect artefacts -----------------------------------------------------------------------
[xml] $csproj = Get-Content $project
$vName = if ($VersionName) { $VersionName } else { ($csproj.Project.PropertyGroup.ApplicationDisplayVersion | Where-Object { $_ } | Select-Object -First 1) }
$vCode = if ($VersionCode -gt 0) { $VersionCode } else { ($csproj.Project.PropertyGroup.ApplicationVersion | Where-Object { $_ } | Select-Object -First 1) }

$publish = Join-Path $root 'DartForge.App\bin\Release\net9.0-android\publish'
$aab = Get-ChildItem $publish -Filter '*-Signed.aab' | Select-Object -First 1
$apk = Get-ChildItem $publish -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $aab) { throw "No signed .aab found in $publish" }

$base = "DartTA-$vName-$vCode"
Copy-Item $aab.FullName (Join-Path $outDir "$base.aab") -Force
if ($apk) { Copy-Item $apk.FullName (Join-Path $outDir "$base.apk") -Force }

Write-Host ''
Write-Host "Release ready in $outDir" -ForegroundColor Green
Get-ChildItem $outDir | Format-Table Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
