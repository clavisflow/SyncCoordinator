/*
  MySQL 8 用の同期補助テーブルと差し替え可能なサンプル Entity。
  接続文字列では GuidFormat=Char36 を指定する。
*/
CREATE TABLE SyncChangeQueue
(
    QueueId       BIGINT NOT NULL AUTO_INCREMENT,
    MessageId     CHAR(36) NOT NULL,
    EntityType    VARCHAR(128) NOT NULL,
    EntityId      VARCHAR(256) NOT NULL,
    Operation     VARCHAR(16) NOT NULL,
    OccurredAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    PRIMARY KEY (QueueId)
) ENGINE=InnoDB;

CREATE TABLE SyncAppliedMessage
(
    MessageId    CHAR(36) NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    PRIMARY KEY (MessageId)
) ENGINE=InnoDB;

CREATE TABLE SampleSyncEntity
(
    EntityType   VARCHAR(128) NOT NULL,
    EntityId     VARCHAR(256) NOT NULL,
    OriginSystem VARCHAR(64) NOT NULL,
    PayloadJson  JSON NOT NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL,
    PRIMARY KEY (EntityType, EntityId)
) ENGINE=InnoDB;

DELIMITER $$
CREATE TRIGGER TR_SampleSyncEntity_SyncQueue_Insert
AFTER INSERT ON SampleSyncEntity
FOR EACH ROW
BEGIN
    INSERT SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    VALUES (COALESCE(@sync_message_id, UUID()), NEW.EntityType, NEW.EntityId, 'Upsert', UTC_TIMESTAMP(6));
END$$

CREATE TRIGGER TR_SampleSyncEntity_SyncQueue_Update
AFTER UPDATE ON SampleSyncEntity
FOR EACH ROW
BEGIN
    INSERT SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    VALUES (COALESCE(@sync_message_id, UUID()), NEW.EntityType, NEW.EntityId, 'Upsert', UTC_TIMESTAMP(6));
END$$
DELIMITER ;

/* 物理削除は SQL Server 版と同様、tombstone/履歴表を決めてから DELETE Trigger を追加する。 */
