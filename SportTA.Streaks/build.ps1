# Builds the solution, runs all tests and the sample. Works with the .NET 8 or a newer SDK (e.g. .NET 9).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet --version
dotnet restore SportTA.Streaks.sln
dotnet build SportTA.Streaks.sln -c Release --no-restore
dotnet test tests/SportTA.Streaks.Tests/SportTA.Streaks.Tests.csproj -c Release --no-build --logger "console;verbosity=normal"
dotnet run --project samples/SportTA.Streaks.Samples.ConsoleApp/SportTA.Streaks.Samples.ConsoleApp.csproj -c Release --no-build
