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

CREATE TABLE dbo.SampleSyncEntity
(
    EntityType   nvarchar(128) NOT NULL,
    EntityId     nvarchar(256) NOT NULL,
    OriginSystem nvarchar(64) NOT NULL,
    PayloadJson  nvarchar(max) NOT NULL,
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
    FROM inserted AS i;

    /*
      物理削除を同期する場合は次を有効化するだけでは不十分。
      ID キューから payload を復元できる tombstone/履歴表を Connector とセットで設計すること。

      INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
      SELECT COALESCE(@ContextMessageId, NEWID()), d.EntityType, d.EntityId, 'Delete', SYSUTCDATETIME()
      FROM deleted AS d
      WHERE NOT EXISTS
          (SELECT 1 FROM inserted AS i WHERE i.EntityType=d.EntityType AND i.EntityId=d.EntityId);
    */
END;
GO
