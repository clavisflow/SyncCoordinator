/*
  {{SCHEMA}}, {{TABLE}}, {{TRIGGER}}, {{ENTITY_TYPE}}, {{ID_EXPRESSION}} を置換する。
  Trigger は同一DB内の SyncChangeQueue に ID を積むだけで、別DBや Coordinator を呼ばない。
*/
CREATE OR ALTER TRIGGER [{{SCHEMA}}].[{{TRIGGER}}]
ON [{{SCHEMA}}].[{{TABLE}}]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ContextMessageId uniqueidentifier =
        TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'SyncMessageId'));

    INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
    SELECT
        COALESCE(@ContextMessageId, NEWID()),
        N'{{ENTITY_TYPE}}',
        CONVERT(nvarchar(256), {{ID_EXPRESSION}}),
        'Upsert',
        SYSUTCDATETIME()
    FROM inserted AS i;
END;
GO
