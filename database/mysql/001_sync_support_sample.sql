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

CREATE TABLE SyncEntityOrigin
(
    EntityType   VARCHAR(128) NOT NULL,
    EntityId     VARCHAR(256) NOT NULL,
    OriginSystem VARCHAR(64) NOT NULL,
    PRIMARY KEY (EntityType, EntityId)
) ENGINE=InnoDB;

CREATE TABLE SyncDeleteTombstone
(
    MessageId    CHAR(36) NOT NULL,
    EntityType   VARCHAR(128) NOT NULL,
    EntityId     VARCHAR(256) NOT NULL,
    OriginSystem VARCHAR(64) NOT NULL,
    PayloadJson  JSON NOT NULL,
    DeletedAtUtc DATETIME(6) NOT NULL,
    PRIMARY KEY (MessageId, EntityType, EntityId)
) ENGINE=InnoDB;

CREATE TABLE SyncCoordinatorDeployment
(
    DeploymentKey VARCHAR(128) NOT NULL,
    DefinitionHash CHAR(64) NOT NULL,
    AppliedAtUtc   DATETIME(6) NOT NULL,
    PRIMARY KEY (DeploymentKey)
) ENGINE=InnoDB;

CREATE TABLE SampleSyncEntity
(
    EntityType   VARCHAR(128) NOT NULL,
    EntityId     VARCHAR(256) NOT NULL,
    OriginSystem VARCHAR(64) NOT NULL,
    PayloadJson  JSON NOT NULL,
    IsDeleted    BOOLEAN NOT NULL DEFAULT FALSE,
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

CREATE TRIGGER TR_SampleSyncEntity_SyncQueue_Delete
AFTER DELETE ON SampleSyncEntity
FOR EACH ROW
BEGIN
    SET @sample_delete_message_id = COALESCE(@sync_message_id, UUID());
    INSERT SyncDeleteTombstone(MessageId, EntityType, EntityId, OriginSystem, PayloadJson, DeletedAtUtc)
    VALUES (@sample_delete_message_id, OLD.EntityType, OLD.EntityId, OLD.OriginSystem, OLD.PayloadJson, UTC_TIMESTAMP(6));
    INSERT SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    VALUES (@sample_delete_message_id, OLD.EntityType, OLD.EntityId, 'Delete', UTC_TIMESTAMP(6));
    DELETE FROM SyncEntityOrigin WHERE EntityType = OLD.EntityType AND EntityId = OLD.EntityId;
END$$
DELIMITER ;

/* 論理削除を検知するTriggerは管理画面の論理削除列・削除値から生成する。 */
