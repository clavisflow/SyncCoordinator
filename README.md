# SyncCoordinator

<p align="right">
  <strong>English</strong> · <a href="./README.ja.md">日本語</a>
</p>

SyncCoordinator (SynCo) is a self-hosted coordinator that detects changes across business databases and synchronizes data with value transformation, conflict detection, retries, and loop prevention.

For product concepts and usage guidance, see the [SyncCoordinator product documentation](https://synco.clavisflow.net/en/).

- [Overview](https://synco.clavisflow.net/en/overview): Product scope, key capabilities, and use cases
- [Architecture](https://synco.clavisflow.net/en/architecture): System topology, components, and reliability model
- [Workflow](https://synco.clavisflow.net/en/workflow): From change detection and conflict resolution to completed synchronization
- [Getting Started](https://synco.clavisflow.net/en/getting-started): Run the demo, complete initial setup, and connect real business databases

## What's included

- Mapping-driven relational database connectors for SQL Server, MySQL, and PostgreSQL
- Trigger-based change detection through `SyncChangeQueue`, with no full-table polling of business data
- Per-field three-way conflict detection that compares the previous value, incoming value, and current destination value
- Update and delete conflict history, manual resolution, and reevaluation of subsequent conflicts
- At-least-once delivery, deterministic delivery IDs, idempotent destination writes, and synchronization-loop prevention
- A Blazor Web UI for managing systems, database connections, sync rules, column mappings, and value transformations
- Inbox and checkpoint state, operational events, audit history, Webhook notifications, and retention management
- A runnable three-system demo using Customer Portal, CRM, and Field Service applications

Synchronization support tables and triggers are reviewed as SQL in the management UI, applied to each target database, and verified before a rule is enabled. Existing business applications and business-table definitions do not need to be changed.

## Quick start

The demo environment requires:

- Verified on Windows 10
- .NET SDK 10.0.301 or a compatible later SDK (`global.json` rolls forward to `latestFeature`)
- Aspire CLI 13.4.x
- Docker Desktop

```powershell
dotnet tool restore
dotnet restore SyncCoordinator.sln
dotnet build SyncCoordinator.sln --no-restore
dotnet test SyncCoordinator.sln --no-build
aspire run --apphost src/SyncCoordinator.AppHost/SyncCoordinator.AppHost.csproj
```

The default `Demo` mode starts the following resources together from the Aspire Dashboard:

- SyncCoordinator Web, Worker, and coordinator database
- Customer Portal and MySQL
- CRM and SQL Server
- Field Service and PostgreSQL

On first use, set the administrator password at `/account/setup` in SyncCoordinator Web. See the [demo environment](demos/README.md) for demo-route activation, conflict scenarios, and verification steps. This supporting document is currently in Japanese.

The standard `dotnet test` command skips E2E tests that require Docker and Chromium. See [E2E testing](docs/e2e-testing.md) for explicit execution instructions. This supporting document is currently in Japanese.

## Run modes

Set `RunMode` in `src/SyncCoordinator.AppHost/appsettings.Development.json` to switch configurations.

| Mode | Purpose | Resources |
| --- | --- | --- |
| `Demo` | Product evaluation, demo capture, and development | Web, Worker, coordinator database, three business applications, and their databases; also loads demo configuration and conflict scenarios |
| `Core` | Connect to real business databases | Web, Worker, and either an external or containerized coordinator database; systems and rules are registered through the management UI |
| `E2E` | Automated end-to-end testing | Starts temporary databases and demo applications on dynamic ports, then removes them after the tests finish |

Docker Desktop is not required in `Core` mode when an external SQL Server is used for the coordinator database. For connection strings, coordinator-database migrations, and production-readiness checks, see [Getting Started](https://synco.clavisflow.net/en/getting-started) and the [technical specification](docs/technical-specification.md). The technical specification is currently in Japanese.

## Solution structure

| Path | Responsibility |
| --- | --- |
| `src/SyncCoordinator.Contracts` | Shared contracts for payloads, change queues, and apply requests |
| `src/SyncCoordinator.Core` | Use cases and abstractions for synchronization decisions, conflict resolution, and management services |
| `src/SyncCoordinator.Infrastructure` | Coordinator database, relational database connectors, database deployment, notifications, and authentication |
| `src/SyncCoordinator.Worker` | Queue reads, delivery, retries, and conflict-resolution requests |
| `src/SyncCoordinator.Web` | Management UI built with Blazor Interactive Server |
| `src/SyncCoordinator.AppHost` | Aspire configurations for `Demo`, `Core`, and `E2E` modes |
| `src/SyncCoordinator.ServiceDefaults` | OpenTelemetry, service discovery, resilience, and health checks |
| `tests/SyncCoordinator.Tests` | Unit and integration tests focused on Core and Infrastructure |
| `tests/SyncCoordinator.E2ETests` | End-to-end tests covering real databases, the Worker, and the management UI |
| `demos` | Three business applications, seed data, and demo capture/reset tools |

## Documentation map

| Document | Contents |
| --- | --- |
| [Product documentation](https://synco.clavisflow.net/en/) | Product overview, architecture, workflow, and setup guidance |
| [User guide and in-app help](docs/user-guide.en.md) ([Japanese](docs/user-guide.md)) | Management-UI procedures and the source for the in-app `/help` page |
| [Technical specification](docs/technical-specification.md) (Japanese) | Synchronization processing, state transitions, persistence, security, and deployment constraints |
| [Webhook integration guide](docs/webhooks.md) (Japanese) | Events, payloads, signature verification, retries, and receiver contracts |
| [Demo environment](demos/README.md) (Japanese) | Three-system topology, demo seed data, and verification/reset procedures |
| [E2E testing](docs/e2e-testing.md) (Japanese) | Prerequisites, execution, and failure investigation |
| [Architecture decisions](docs/decisions) (Japanese) | ADRs recording major design decisions and their rationale |

## Implementation boundaries

- No distributed transaction spans the coordinator database and a business database.
- At the destination, the business update and `SyncAppliedMessage` are stored in the same database-local transaction.
- The Worker coalesces notifications for the same record and converges on the latest state at processing time.
- Database connection details are encrypted with ASP.NET Core Data Protection before being stored in the coordinator database.
- Direct database deployment from the management UI is available only when `DatabaseDeployment:AllowDirectApply=true` is explicitly configured.
- Production environments require HTTPS, a shared key ring, least-privilege database accounts, and an approved migration process.

For details, see [Architecture](https://synco.clavisflow.net/en/architecture), [Workflow](https://synco.clavisflow.net/en/workflow), and the [technical specification](docs/technical-specification.md).

## License

SyncCoordinator is available under the [Apache License 2.0](LICENSE). Copyright 2026 ClavisFlow.

Third-party components included in the distribution remain subject to their respective licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [licenses](licenses) for copyright notices and license terms. In particular, Microsoft.Data.SqlClient.SNI, bundled for SQL Server connectivity on Windows, is distributed under Microsoft's own redistribution terms rather than the MIT License. Also review [BINARY-DISTRIBUTION-NOTICE.md](BINARY-DISTRIBUTION-NOTICE.md) for binary use and redistribution conditions.
