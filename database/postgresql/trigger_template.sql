/*
  {{SCHEMA}}, {{TABLE}}, {{TRIGGER}}, {{ENTITY_TYPE}}, {{NEW_ID_EXPRESSION}} を置換する。
  実運用では管理画面が列マッピングと削除方式を含む完全なSQLを生成する。
*/
CREATE OR REPLACE FUNCTION "{{SCHEMA}}"."{{TRIGGER}}_fn"()
RETURNS trigger
LANGUAGE plpgsql
AS $sync_coordinator$
DECLARE
    v_message_id uuid;
BEGIN
    v_message_id := COALESCE(
        NULLIF(current_setting('synccoordinator.message_id', true), '')::uuid,
        gen_random_uuid());
    INSERT INTO public."SyncChangeQueue"
        ("MessageId", "EntityType", "EntityId", "Operation", "OccurredAtUtc")
    VALUES
        (v_message_id, '{{ENTITY_TYPE}}', CAST({{NEW_ID_EXPRESSION}} AS varchar(256)), 'Upsert', CURRENT_TIMESTAMP);
    RETURN NEW;
END;
$sync_coordinator$;

CREATE TRIGGER "{{TRIGGER}}"
AFTER INSERT OR UPDATE ON "{{SCHEMA}}"."{{TABLE}}"
FOR EACH ROW EXECUTE FUNCTION "{{SCHEMA}}"."{{TRIGGER}}_fn"();
