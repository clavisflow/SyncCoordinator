/*
  SQL Server 用の同期補助テーブルと差し替え可能なサンプル Entity。
  SampleSyncEntity は業務テーブル確定後に削除し、システム別 Connector を実装すること。
*/
SET XACT_ABORT ON;

CREATE TABLE dbo.SyncChangeQueue
(
    QueueId       bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SyncChangeQueue PRIMARY KEY,
    MessageId     uniqueidentifier NOT NULL,
    EntityType    nvarchar(128) NOT NULL,
    EntityId      nvarchar(256) NOT NULL,
    Operation     varchar(16) NOT NULL,
    OccurredAtUtc datetimeoffset(7) NOT NULL CONSTRAINT DF_SyncChangeQueue_OccurredAtUtc DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_SyncChangeQueue_QueueId ON dbo.SyncChangeQueue(QueueId);
GO

CREATE TABLE dbo.SyncAppliedMessage
(
    MessageId    uniqueidentifier NOT NULL CONSTRAINT PK_SyncAppliedMessage PRIMARY KEY,
    AppliedAtUtc datetimeoffset(7) NOT NULL
);
GO

CREATE TABLE dbo.SyncEntityOrigin
(
    EntityType   nvarchar(128) NOT NULL,
    EntityId     nvarchar(256) NOT NULL,
    OriginSystem nvarchar(64) NOT NULL,
    CONSTRAINT PK_SyncEntityOrigin PRIMARY KEY(EntityType, EntityId)
);
GO

CREATE TABLE dbo.SyncDeleteTombstone
(
    MessageId    uniqueidentifier NOT NULL,
    EntityType   nvarchar(128) NOT NULL,
    EntityId     nvarchar(256) NOT NULL,
    OriginSystem nvarchar(64) NOT NULL,
    PayloadJson  nvarchar(max) NOT NULL,
    DeletedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SyncDeleteTombstone PRIMARY KEY(MessageId, EntityType, EntityId),
    CONSTRAINT CK_SyncDeleteTombstone_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
);
GO

CREATE TABLE dbo.SyncCoordinatorDeployment
(
    DeploymentKey nvarchar(128) NOT NULL CONSTRAINT PK_SyncCoordinatorDeployment PRIMARY KEY,
    DefinitionHash char(64) NOT NULL,
    AppliedAtUtc   datetimeoffset(7) NOT NULL
);
GO

CREATE TABLE dbo.SampleSyncEntity
(
    EntityType   nvarchar(128) NOT NULL,
    EntityId     nvarchar(256) NOT NULL,
    OriginSystem nvarchar(64) NOT NULL,
    PayloadJson  nvarchar(max) NOT NULL,
    IsDeleted    bit NOT NULL CONSTRAINT DF_SampleSyncEntity_IsDeleted DEFAULT 0,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SampleSyncEntity PRIMARY KEY(EntityType, EntityId),
    CONSTRAINT CK_SampleSyncEntity_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
);
GO

CREATE OR ALTER TRIGGER dbo.TR_SampleSyncEntity_SyncQueue
ON dbo.SampleSyncEntity
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ContextMessageId uniqueidentifier =
        TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'SyncMessageId'));

    INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    SELECT COALESCE(@ContextMessageId, NEWID()), i.EntityType, i.EntityId, 'Upsert', SYSUTCDATETIME()
    FROM inserted AS i
    WHERE i.IsDeleted = 0;

    DECLARE @Deleted TABLE
    (
        MessageId uniqueidentifier NOT NULL,
        EntityType nvarchar(128) NOT NULL,
        EntityId nvarchar(256) NOT NULL,
        OriginSystem nvarchar(64) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL
    );
    INSERT @Deleted(MessageId, EntityType, EntityId, OriginSystem, PayloadJson)
    SELECT COALESCE(@ContextMessageId, NEWID()), d.EntityType, d.EntityId, d.OriginSystem, d.PayloadJson
    FROM deleted AS d
    WHERE NOT EXISTS
        (SELECT 1 FROM inserted AS i WHERE i.EntityType=d.EntityType AND i.EntityId=d.EntityId);

    INSERT dbo.SyncDeleteTombstone(MessageId, EntityType, EntityId, OriginSystem, PayloadJson, DeletedAtUtc)
    SELECT MessageId, EntityType, EntityId, OriginSystem, PayloadJson, SYSUTCDATETIME()
    FROM @Deleted;

    INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    SELECT MessageId, EntityType, EntityId, 'Delete', SYSUTCDATETIME()
    FROM @Deleted;

    DELETE origin
    FROM dbo.SyncEntityOrigin AS origin
    INNER JOIN @Deleted AS item
        ON origin.EntityType = item.EntityType AND origin.EntityId = item.EntityId;
END;
GO

/* 論理削除を検知するTriggerは管理画面の論理削除列・削除値から生成する。 */
