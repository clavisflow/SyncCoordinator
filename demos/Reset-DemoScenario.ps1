[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateRange(10, 180)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

$databasePassword = 'SyncDemo123!'
$caseNumber = 'CASE-1001'

function Invoke-DockerCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed:`n$($output -join "`n")"
    }

    return ($output -join "`n").Trim()
}

function Get-DemoContainer {
    param(
        [Parameter(Mandatory)]
        [string]$Prefix
    )

    $names = @(Invoke-DockerCommand -Arguments @('ps', '--format', '{{.Names}}') -split "`r?`n") |
        Where-Object { $_ -and $_.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase) }

    if ($names.Count -ne 1) {
        throw "Expected exactly one running container named '$Prefix*', but found $($names.Count). Start the Demo AppHost and try again."
    }

    return $names[0]
}

function Invoke-MySql {
    param(
        [Parameter(Mandatory)]
        [string]$Container,
        [Parameter(Mandatory)]
        [string]$Sql
    )

    return Invoke-DockerCommand -Arguments @(
        'exec', '-e', "MYSQL_PWD=$databasePassword", $Container,
        'mysql', '--default-character-set=utf8mb4', '--batch', '--skip-column-names',
        '--user=root', 'DemoCustomerPortal', '--execute', $Sql)
}

function Invoke-SqlServer {
    param(
        [Parameter(Mandatory)]
        [string]$Container,
        [Parameter(Mandatory)]
        [string]$Sql
    )

    return Invoke-DockerCommand -Arguments @(
        'exec', '-e', "SQLCMDPASSWORD=$databasePassword", $Container,
        '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C',
        '-d', 'DemoCrm', '-h', '-1', '-W', '-Q', $Sql)
}

function Invoke-Postgres {
    param(
        [Parameter(Mandatory)]
        [string]$Container,
        [Parameter(Mandatory)]
        [string]$Sql
    )

    return Invoke-DockerCommand -Arguments @(
        'exec', '-e', "PGPASSWORD=$databasePassword", $Container,
        'psql', '--username=postgres', '--dbname=DemoFieldService',
        '--tuples-only', '--no-align', '--command', $Sql)
}

$target = "the '$caseNumber' business record and its work orders in the local Demo databases"
if (-not $PSCmdlet.ShouldProcess($target, 'Reset for a new video recording take')) {
    return
}

$mysqlContainer = Get-DemoContainer -Prefix 'mysql-'
$sqlServerContainer = Get-DemoContainer -Prefix 'sqlserver-'
$postgresContainer = Get-DemoContainer -Prefix 'postgres-'

$portalResetSql = @"
UPDATE SupportCase
SET CustomerName = '山田 太郎',
    Email = 'taro.yamada@example.com',
    Phone = '090-1234-5678',
    ProductName = 'エアコン AC-200',
    SerialNumber = 'AC200-2026-00125',
    Subject = '冷風が出ない',
    Description = '運転を開始しても送風のみで、冷たい風が出ません。',
    PreferredVisitDate = DATE_ADD(UTC_DATE(), INTERVAL 3 DAY),
    Status = 'New',
    ResponseMessage = NULL,
    OriginSystem = 'PORTAL',
    UpdatedAtUtc = UTC_TIMESTAMP(6)
WHERE CaseNumber = '$caseNumber';
SELECT ROW_COUNT();
"@

$updatedRows = Invoke-MySql -Container $mysqlContainer -Sql $portalResetSql
if ($updatedRows -ne '1') {
    throw "The Portal record '$caseNumber' was not found. Reset the Demo volumes to restore the seed data."
}

$deleteWorkOrdersSql = @"
SET NOCOUNT ON;
DELETE FROM dbo.WorkOrder
WHERE CaseNumber = N'$caseNumber' OR CaseId = N'$caseNumber';
SELECT @@ROWCOUNT;
"@
$deletedWorkOrders = Invoke-SqlServer -Container $sqlServerContainer -Sql $deleteWorkOrdersSql

Write-Host "Portal reset requested; CRM work orders deleted: $deletedWorkOrders"
Write-Host 'Waiting for the configured sync routes to deliver the reset...'

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    $crmState = Invoke-SqlServer -Container $sqlServerContainer -Sql @"
SET NOCOUNT ON;
SELECT CONCAT(
    WorkflowState,
    '|',
    CASE WHEN AgentReply IS NULL THEN 'NULL' ELSE 'VALUE' END,
    '|',
    (SELECT COUNT(*) FROM dbo.WorkOrder WHERE CaseNumber = N'$caseNumber' OR CaseId = N'$caseNumber'))
FROM dbo.SupportCase
WHERE CaseRef = N'$caseNumber';
"@

    $fieldCount = Invoke-Postgres -Container $postgresContainer -Sql @"
SELECT COUNT(*)
FROM public.work_order
WHERE case_ref = '$caseNumber' OR source_case_id = '$caseNumber';
"@

    if ($crmState -eq 'New|NULL|0' -and $fieldCount -eq '0') {
        Write-Host "Demo scenario reset complete: $caseNumber is New and no related work orders remain."
        return
    }

    Start-Sleep -Seconds 2
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw @"
The business records were changed, but synchronization did not settle within $TimeoutSeconds seconds.
Current CRM state: '$crmState'; Field work-order count: '$fieldCount'.
Confirm both routes are deployed, verified, enabled, and that the Worker is healthy.
"@
