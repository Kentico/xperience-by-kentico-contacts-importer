<#
.SYNOPSIS
    Creates the DancingGoat database with kentico-xperience-dbmanager.

.DESCRIPTION
    Defaults come from the CMSConnectionString in examples/DancingGoat/appsettings.json.
    dbmanager writes the connection string and the CMSHashStringSalt to that file; -SaveTo decides
    where they end up:
      UserSecrets  (default) DancingGoat user secrets; appsettings.json is restored, so nothing local is committed
      Salt         only the salt, to DancingGoat user secrets (the Aspire AppHost supplies the connection string)
      AppSettings  (default in CI) appsettings.json keeps them; user secrets only load in Development
      None         appsettings.json is restored and nothing is saved

    dbmanager owns the salt: it generates one unless -HashStringSalt is given. When saving to user
    secrets and a salt is already stored there, that salt is reused, so a machine keeps one salt.

    Without a license file the database is still created; add the license on first sign-in
    to the administration (/admin, user "administrator").

.EXAMPLE
    ./scripts/Initialize-Database.ps1
    ./scripts/Initialize-Database.ps1 -LicenseFile ~/xperience/license.txt -Recreate
    ./scripts/Initialize-Database.ps1 -ServerName "localhost\SQLEXPRESS" -Username "" # Windows authentication
#>
[CmdletBinding()]
param (
    # SQL Server, e.g. "localhost,1433" (Docker) or "localhost\SQLEXPRESS". Defaults to the connection string's Data Source
    [string]$ServerName,

    # Defaults to the connection string's Initial Catalog
    [string]$DatabaseName,

    # SQL authentication. Defaults to the connection string's User ID / Password (or XPERIENCE_SQL_PASSWORD); empty Username uses Windows authentication
    [string]$Username,
    [string]$Password,

    # Password for the "administrator" account
    [string]$AdminPassword = $(if ($env:XPERIENCE_ADMIN_PASSWORD) { $env:XPERIENCE_ADMIN_PASSWORD } else { 'Pass@12345' }),

    # Xperience license file; optional
    [string]$LicenseFile = $env:XPERIENCE_LICENSE_FILE,

    # Salt for macro signatures; defaults to the one in DancingGoat user secrets, otherwise dbmanager generates one
    [string]$HashStringSalt,

    # Drop and recreate the database if it exists
    [switch]$Recreate,

    # Do nothing when the database is ready; apply the Xperience update when it is behind the application
    [switch]$IfNotReady,

    [ValidateSet('UserSecrets', 'Salt', 'AppSettings', 'None')]
    [string]$SaveTo = $(if ($env:CI) { 'AppSettings' } else { 'UserSecrets' })
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'examples/DancingGoat'
$appsettingsPath = Join-Path $project 'appsettings.json'
$appsettingsOriginal = [IO.File]::ReadAllText($appsettingsPath)
$appsettings = $appsettingsOriginal | ConvertFrom-Json

$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
# set_ConnectionString: PowerShell treats the builder as a dictionary, so property assignment would add a key instead
$connection.set_ConnectionString($appsettings.ConnectionStrings.CMSConnectionString)

function Get-ConnectionValue([string]$key) {
    if ($connection.ContainsKey($key)) { return [string]$connection[$key] }
    return ''
}

if (-not $PSBoundParameters.ContainsKey('ServerName')) { $ServerName = Get-ConnectionValue 'Data Source' }
if (-not $PSBoundParameters.ContainsKey('DatabaseName')) { $DatabaseName = Get-ConnectionValue 'Initial Catalog' }
if (-not $PSBoundParameters.ContainsKey('Username')) {
    $Username = if ((Get-ConnectionValue 'Integrated Security') -ieq 'True') { '' } else { Get-ConnectionValue 'User ID' }
}
if (-not $PSBoundParameters.ContainsKey('Password')) {
    $Password = if ($env:XPERIENCE_SQL_PASSWORD) { $env:XPERIENCE_SQL_PASSWORD } else { Get-ConnectionValue 'Password' }
}

$useUserSecrets = $SaveTo -in 'UserSecrets', 'Salt'
if (-not $HashStringSalt -and $useUserSecrets) {
    Push-Location $project
    try {
        $stored = dotnet user-secrets list 2>$null | Where-Object { $_ -like 'CMSHashStringSalt = *' } | Select-Object -First 1
        if ($stored) { $HashStringSalt = $stored.Substring('CMSHashStringSalt = '.Length) }
    }
    finally {
        Pop-Location
    }
}

if (-not $ServerName) { throw 'No SQL Server: pass -ServerName or set Data Source in examples/DancingGoat/appsettings.json.' }
if (-not $DatabaseName) { throw 'No database name: pass -DatabaseName or set Initial Catalog in examples/DancingGoat/appsettings.json.' }

# Runs DancingGoat's Xperience CLI against the target database (DancingGoat must be built)
function Invoke-DancingGoatCli([string[]]$cliArguments) {
    $authentication = if ($Username) { "User ID=$Username;Password=$Password" } else { 'Integrated Security=True' }
    $env:ConnectionStrings__CMSConnectionString = "Data Source=$ServerName;Initial Catalog=$DatabaseName;$authentication;TrustServerCertificate=True;Encrypt=False"
    if ($HashStringSalt) { $env:CMSHashStringSalt = $HashStringSalt }
    try {
        dotnet run --project $project --no-build --no-launch-profile -- @cliArguments
    }
    finally {
        Remove-Item Env:ConnectionStrings__CMSConnectionString -ErrorAction SilentlyContinue
    }
}

if ($IfNotReady) {
    $statusJson = Invoke-DancingGoatCli @('--kxp-db-status', '--format', 'json') | Select-Object -Last 1
    $status = $statusJson | ConvertFrom-Json
    switch ($status.state) {
        'Ready' {
            Write-Host "Database '$DatabaseName' is ready ($($status.databaseVersion))."
            return
        }
        'UpdateRequired' {
            Write-Host $status.message
            Invoke-DancingGoatCli @('--kxp-update', '--skip-confirmation')
            if ($LASTEXITCODE -ne 0) { throw "Xperience database update failed ($LASTEXITCODE)" }
            return
        }
        default {
            # NotAvailable: no database yet (the server itself is up for callers that wait on it)
            Write-Host $status.message
        }
    }
}

$arguments = @('--server-name', $ServerName, '--database-name', $DatabaseName, '--admin-password', $AdminPassword)
if ($Username) { $arguments += @('--username', $Username, '--password', $Password) }
if ($HashStringSalt) { $arguments += @('--hash-string-salt', $HashStringSalt) }
if ($Recreate) { $arguments += '--recreate-existing-database' }

if ($LicenseFile) {
    if (Test-Path $LicenseFile -PathType Leaf) {
        $arguments += @('--license-file', (Resolve-Path $LicenseFile).Path)
    }
    else {
        Write-Warning "License file not found: $LicenseFile. Creating the database without a license."
    }
}

Write-Host "Creating database '$DatabaseName' on '$ServerName' ($(if ($Username) { "SQL login $Username" } else { 'Windows authentication' }))"

Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

    Push-Location $project
    try {
        dotnet kentico-xperience-dbmanager -- @arguments
        if ($LASTEXITCODE -ne 0) { throw "kentico-xperience-dbmanager failed ($LASTEXITCODE)" }

        switch ($SaveTo) {
            'UserSecrets' {
                $written = [IO.File]::ReadAllText($appsettingsPath) | ConvertFrom-Json
                dotnet user-secrets set 'ConnectionStrings:CMSConnectionString' $written.ConnectionStrings.CMSConnectionString | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
                dotnet user-secrets set 'CMSHashStringSalt' $written.CMSHashStringSalt | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
                [IO.File]::WriteAllText($appsettingsPath, $appsettingsOriginal)
                Write-Host 'Moved the connection string and CMSHashStringSalt to DancingGoat user secrets.'
            }
            'Salt' {
                $written = [IO.File]::ReadAllText($appsettingsPath) | ConvertFrom-Json
                dotnet user-secrets set 'CMSHashStringSalt' $written.CMSHashStringSalt | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
                [IO.File]::WriteAllText($appsettingsPath, $appsettingsOriginal)
                Write-Host 'Saved CMSHashStringSalt to DancingGoat user secrets.'
            }
            'None' {
                [IO.File]::WriteAllText($appsettingsPath, $appsettingsOriginal)
            }
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    Pop-Location
}

Write-Host "Database ready. Sign in at /admin as 'administrator'."
