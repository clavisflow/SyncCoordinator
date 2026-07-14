using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCoordinatorSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAccount",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SessionVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAccount", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigurationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigurationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessage",
                columns: table => new
                {
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessage", x => new { x.SourceMessageId, x.RouteId, x.DestinationSystem });
                });

            migrationBuilder.CreateTable(
                name: "QueueCheckpoint",
                columns: table => new
                {
                    SystemCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastQueueId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueCheckpoint", x => x.SystemCode);
                });

            migrationBuilder.CreateTable(
                name: "SyncSnapshot",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourcePayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DestinationPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSnapshot", x => new { x.RouteId, x.DestinationSystem, x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateTable(
                name: "SystemDefinition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    PausedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProtectedConnectionString = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConnectionUpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemDefinition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEndpoint",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SignatureEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventTypesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoint", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRoute",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DestinationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ConflictScope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DefaultConflictPolicy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DeploymentState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRoute", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRoute_SystemDefinition_DestinationSystemId",
                        column: x => x.DestinationSystemId,
                        principalTable: "SystemDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncRoute_SystemDefinition_SourceSystemId",
                        column: x => x.SourceSystemId,
                        principalTable: "SystemDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDelivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDelivery_WebhookEndpoint_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "WebhookEndpoint",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebhookDelivery_WebhookEvent_EventId",
                        column: x => x.EventId,
                        principalTable: "WebhookEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RouteTableMapping",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceTable = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DestinationSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DestinationTable = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SyncDeletes = table.Column<bool>(type: "bit", nullable: false),
                    SourceDeletionMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceLogicalDeleteColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SourceLogicalDeleteValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DestinationDeletionMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DestinationLogicalDeleteColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DestinationLogicalDeleteValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteTableMapping", x => x.RouteId);
                    table.ForeignKey(
                        name: "FK_RouteTableMapping_SyncRoute_RouteId",
                        column: x => x.RouteId,
                        principalTable: "SyncRoute",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncConflict",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflict", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncConflict_SyncRoute_RouteId",
                        column: x => x.RouteId,
                        principalTable: "SyncRoute",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RouteColumnMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DestinationColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsKey = table.Column<bool>(type: "bit", nullable: false),
                    ConflictPolicy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteColumnMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteColumnMapping_RouteTableMapping_TableMappingId",
                        column: x => x.TableMappingId,
                        principalTable: "RouteTableMapping",
                        principalColumn: "RouteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RouteFixedValueMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TargetColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteFixedValueMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteFixedValueMapping_RouteTableMapping_TableMappingId",
                        column: x => x.TableMappingId,
                        principalTable: "RouteTableMapping",
                        principalColumn: "RouteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccount_UserName",
                table: "AdminAccount",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationAudit_ChangedAtUtc",
                table: "ConfigurationAudit",
                column: "ChangedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessage_State",
                table: "InboxMessage",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_RouteColumnMapping_TableMappingId_DestinationColumn",
                table: "RouteColumnMapping",
                columns: new[] { "TableMappingId", "DestinationColumn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteColumnMapping_TableMappingId_SourceColumn",
                table: "RouteColumnMapping",
                columns: new[] { "TableMappingId", "SourceColumn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteFixedValueMapping_TableMappingId_Direction_TargetColumn",
                table: "RouteFixedValueMapping",
                columns: new[] { "TableMappingId", "Direction", "TargetColumn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_ResolvedAtUtc_DetectedAtUtc",
                table: "SyncConflict",
                columns: new[] { "ResolvedAtUtc", "DetectedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_RouteId",
                table: "SyncConflict",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRoute_DestinationSystemId_EntityType_Direction_Enabled",
                table: "SyncRoute",
                columns: new[] { "DestinationSystemId", "EntityType", "Direction", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRoute_SourceSystemId_EntityType_Enabled",
                table: "SyncRoute",
                columns: new[] { "SourceSystemId", "EntityType", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemDefinition_Code",
                table: "SystemDefinition",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDelivery_EndpointId",
                table: "WebhookDelivery",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDelivery_EventId_EndpointId",
                table: "WebhookDelivery",
                columns: new[] { "EventId", "EndpointId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDelivery_State_NextAttemptAtUtc",
                table: "WebhookDelivery",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvent_OccurredAtUtc",
                table: "WebhookEvent",
                column: "OccurredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAccount");

            migrationBuilder.DropTable(
                name: "ConfigurationAudit");

            migrationBuilder.DropTable(
                name: "InboxMessage");

            migrationBuilder.DropTable(
                name: "QueueCheckpoint");

            migrationBuilder.DropTable(
                name: "RouteColumnMapping");

            migrationBuilder.DropTable(
                name: "RouteFixedValueMapping");

            migrationBuilder.DropTable(
                name: "SyncConflict");

            migrationBuilder.DropTable(
                name: "SyncSnapshot");

            migrationBuilder.DropTable(
                name: "WebhookDelivery");

            migrationBuilder.DropTable(
                name: "RouteTableMapping");

            migrationBuilder.DropTable(
                name: "WebhookEndpoint");

            migrationBuilder.DropTable(
                name: "WebhookEvent");

            migrationBuilder.DropTable(
                name: "SyncRoute");

            migrationBuilder.DropTable(
                name: "SystemDefinition");
        }
    }
}
