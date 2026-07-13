/*
  {{TABLE}}, {{TRIGGER}}, {{ENTITY_TYPE}}, {{NEW_ID_EXPRESSION}} を置換する。
  INSERT 用。UPDATE 用も同じ本体で別 Trigger として作成する。
*/
DELIMITER $$
CREATE TRIGGER `{{TRIGGER}}`
AFTER INSERT ON `{{TABLE}}`
FOR EACH ROW
BEGIN
    INSERT SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    VALUES
    (
        COALESCE(@sync_message_id, UUID()),
        '{{ENTITY_TYPE}}',
        CAST({{NEW_ID_EXPRESSION}} AS CHAR(256)),
        'Upsert',
        UTC_TIMESTAMP(6)
    );
END$$
DELIMITER ;
