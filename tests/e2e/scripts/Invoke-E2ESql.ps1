<#
.SYNOPSIS
    Runs one SQL statement against the database DancingGoat uses in the e2e tests and writes the rows as JSON.

.DESCRIPTION
    Called by tests/e2e/shared/database.ts for the few things the administration UI cannot do (for example
    unsubscribing a contact from a recipient list). Not for use against a database you want to keep.

    The statement and its parameters come from environment variables, so no quoting rules apply:
      E2E_SQL_QUERY       SQL text; parameters as @name
      E2E_SQL_PARAMETERS  JSON object: parameter name -> string or number

    Connection string, first one that is set:
      1. E2E_SQL_CONNECTION_STRING
      2. ConnectionStrings__CMSConnectionString (how CI recipes and local scripts point DancingGoat at a database)
      3. ConnectionStrings:CMSConnectionString in the DancingGoat user secrets (local default; not read in CI)
      4. CMSConnectionString in examples/DancingGoat/appsettings.json (CI: Initialize-Database.ps1 writes it there)

    Output: a JSON array of row objects (column name -> value) on stdout. Exit code 2 = no connection string.
#>
$ErrorActionPreference = 'Stop'

function Get-ConnectionString {
    if ($env:E2E_SQL_CONNECTION_STRING) { return $env:E2E_SQL_CONNECTION_STRING }
    if ($env:ConnectionStrings__CMSConnectionString) { return $env:ConnectionStrings__CMSConnectionString }

    $project = Join-Path $PSScriptRoot '../../../examples/DancingGoat'
    if (-not $env:CI) {
        # User secrets override appsettings.json when DancingGoat runs in Development.
        $prefix = 'ConnectionStrings:CMSConnectionString = '
        $secret = dotnet user-secrets list --project $project 2>$null | Where-Object { $_.StartsWith($prefix) } | Select-Object -First 1
        if ($secret) { return $secret.Substring($prefix.Length) }
    }

    $appsettings = Get-Content (Join-Path $project 'appsettings.json') -Raw | ConvertFrom-Json
    $fromFile = $appsettings.ConnectionStrings.CMSConnectionString
    if ($fromFile -and $fromFile -match 'Data Source=[^;]+') { return $fromFile }

    return $null
}

$connectionString = Get-ConnectionString
if (-not $connectionString) {
    [Console]::Error.WriteLine('No connection string: set E2E_SQL_CONNECTION_STRING.')
    exit 2
}

if (-not $env:E2E_SQL_QUERY) { throw 'E2E_SQL_QUERY is not set.' }

$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = $env:E2E_SQL_QUERY
    $command.CommandTimeout = 60
    if ($env:E2E_SQL_PARAMETERS) {
        foreach ($parameter in ($env:E2E_SQL_PARAMETERS | ConvertFrom-Json).PSObject.Properties) {
            [void]$command.Parameters.AddWithValue("@$($parameter.Name)", $parameter.Value)
        }
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $reader = $command.ExecuteReader()
    try {
        while ($reader.Read()) {
            $row = [ordered]@{}
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
            }
            $rows.Add([pscustomobject]$row)
        }
    }
    finally {
        $reader.Dispose()
    }

    # -InputObject: one row is still an array, no row an empty one
    ConvertTo-Json -InputObject $rows.ToArray() -Compress -Depth 3
}
finally {
    $connection.Dispose()
}
