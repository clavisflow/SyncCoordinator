IF DB_ID(N'DemoCrm') IS NULL
BEGIN
    CREATE DATABASE DemoCrm;
END;
GO

USE DemoCrm;
GO

CREATE TABLE dbo.SupportCase
(
    CaseRef nvarchar(64) NOT NULL CONSTRAINT PK_SupportCase PRIMARY KEY,
    ContactName nvarchar(100) NULL,
    ContactEmail nvarchar(200) NULL,
    ContactPhone nvarchar(30) NULL,
    ProductLabel nvarchar(150) NULL,
    DeviceSerial nvarchar(100) NULL,
    CaseTitle nvarchar(200) NULL,
    CaseDetails nvarchar(max) NULL,
    RequestedVisitOn date NULL,
    WorkflowState nvarchar(32) NOT NULL,
    AgentReply nvarchar(max) NULL,
    SourceCode nvarchar(64) NOT NULL,
    ModifiedAtUtc datetimeoffset(7) NOT NULL,
    OwnerTeam nvarchar(80) NULL
);

CREATE TABLE dbo.WorkOrder
(
    WorkOrderNumber nvarchar(64) NOT NULL CONSTRAINT PK_WorkOrder PRIMARY KEY,
    CaseRef nvarchar(64) NOT NULL,
    ServiceAddress nvarchar(500) NULL,
    ProblemSummary nvarchar(500) NULL,
    ScheduledAt datetimeoffset(7) NULL,
    TechnicianName nvarchar(200) NULL,
    Status nvarchar(40) NOT NULL,
    WorkResult nvarchar(max) NULL,
    CompletedAt datetimeoffset(7) NULL,
    EstimatedMinutes int NULL,
    EstimatedCost decimal(12,4) NULL,
    RequiresParts bit NULL,
    WorkNote nvarchar(1000) NULL,
    ExternalTrackingId uniqueidentifier NULL,
    OriginSystem nvarchar(64) NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    PriorityCode tinyint NULL,
    CONSTRAINT FK_WorkOrder_SupportCase FOREIGN KEY (CaseRef)
        REFERENCES dbo.SupportCase(CaseRef)
);

CREATE INDEX IX_WorkOrder_CaseRef ON dbo.WorkOrder(CaseRef);

CREATE TABLE dbo.WorkOrderAssignment
(
    AssignmentId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_WorkOrderAssignment PRIMARY KEY,
    WorkOrderNumber nvarchar(64) NOT NULL,
    StaffNo nvarchar(32) NULL,
    AssignmentType nvarchar(32) NOT NULL CONSTRAINT DF_WorkOrderAssignment_AssignmentType DEFAULT N'Primary',
    AssignedAtUtc datetimeoffset(7) NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT FK_WorkOrderAssignment_WorkOrder FOREIGN KEY (WorkOrderNumber)
        REFERENCES dbo.WorkOrder(WorkOrderNumber) ON DELETE CASCADE
);

CREATE INDEX IX_WorkOrderAssignment_WorkOrderNumber
    ON dbo.WorkOrderAssignment(WorkOrderNumber);
GO

INSERT dbo.SupportCase
    (CaseRef, ContactName, ContactEmail, ContactPhone, ProductLabel, DeviceSerial,
     CaseTitle, CaseDetails, RequestedVisitOn, WorkflowState, AgentReply,
     SourceCode, ModifiedAtUtc, OwnerTeam)
VALUES
    (N'CASE-1001', N'山田 太郎', N'taro.yamada@example.com', N'090-1234-5678', N'AirCool X200', N'ACX-1001',
     N'冷風が出ない', N'運転しても送風のみで、冷たい風が出ません。', '2026-07-24', N'New', NULL,
     N'PORTAL', '2026-07-22T00:00:00+00:00', N'第一受付'),
    (N'CASE-REL-1001', N'佐々木 美咲', N'misaki.sasaki@example.com', N'080-1111-2222', N'HeatPump Z5', N'HPZ-5012',
     N'複数箇所の点検依頼', N'同一受付から室内機2台の作業依頼を作成するケースです。', '2026-07-25', N'InProgress', NULL,
     N'CRM', '2026-07-22T00:10:00+00:00', N'訪問修理'),
    (N'CASE-TYPES-1001', N'型別テスト 顧客', N'type.demo@example.com', N'070-3333-4444', N'Demo Device Pro', N'DEMO-TYPE-01',
     N'型別競合と変換エラー', N'文字列、数値、真偽値、日時、NULL、コード、UUIDを確認します。', '2026-07-26', N'InProgress', NULL,
     N'CRM', '2026-07-22T00:20:00+00:00', N'デモ検証'),
    (N'CASE-UPDATE-1001', N'山田 太郎', N'taro.yamada@example.com', N'090-1234-5678', N'AirCool X200', N'ACX-1002',
     N'日時競合デモ', N'Workerが作成する競合デモの親受付です。', '2026-07-27', N'InProgress', NULL,
     N'CRM', '2026-07-22T00:30:00+00:00', N'デモ検証');

INSERT dbo.WorkOrder
    (WorkOrderNumber, CaseRef, ServiceAddress, ProblemSummary, ScheduledAt, TechnicianName,
     Status, WorkResult, CompletedAt, EstimatedMinutes, EstimatedCost, RequiresParts,
     WorkNote, ExternalTrackingId, OriginSystem, UpdatedAtUtc, PriorityCode)
VALUES
    (N'WO-FANOUT-1001', N'CASE-REL-1001', N'東京都中央区銀座1-1-1', N'1階室内機の冷媒系統を点検', '2026-07-25T01:00:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 90, 18000.0000, 1, N'親受付更新の波及対象1', '11111111-1111-1111-1111-111111111101', N'CRM', '2026-07-22T01:00:00+00:00', 1),
    (N'WO-FANOUT-1002', N'CASE-REL-1001', N'東京都中央区銀座1-1-1', N'2階室内機の排水系統を点検', '2026-07-25T03:00:00+00:00', N'鈴木 葵', N'Scheduled', NULL, NULL, 60, 12000.0000, 0, N'親受付更新の波及対象2', '11111111-1111-1111-1111-111111111102', N'CRM', '2026-07-22T01:01:00+00:00', 2),
    (N'WO-WAITING-1001', N'CASE-REL-1001', N'東京都中央区銀座1-1-1', N'担当者未割当のため同期対象外', NULL, NULL, N'Draft', NULL, NULL, 45, 8000.0000, NULL, N'スタッフNoを登録すると同期開始', '11111111-1111-1111-1111-111111111103', N'CRM', '2026-07-22T01:02:00+00:00', 3),
    (N'WO-MULTI-STAFF-1001', N'CASE-REL-1001', N'東京都中央区銀座1-1-1', N'複数担当でも同期先は1件', '2026-07-26T01:00:00+00:00', N'高橋 翔', N'Assigned', NULL, NULL, 120, 30000.0000, 1, N'EXISTSによる重複防止確認', '11111111-1111-1111-1111-111111111104', N'CRM', '2026-07-22T01:03:00+00:00', 1),
    (N'WO-UNASSIGN-1001', N'CASE-REL-1001', N'東京都中央区銀座1-1-1', N'全担当解除で同期先を削除', '2026-07-26T04:00:00+00:00', N'伊藤 凛', N'Assigned', NULL, NULL, 75, 15000.0000, 0, N'再割当すると再作成されます', '11111111-1111-1111-1111-111111111105', N'CRM', '2026-07-22T01:04:00+00:00', 2),
    (N'WO-NEVER-ELIGIBLE-1001', N'CASE-REL-1001', N'東京都中央区銀座1-1-1', N'一度も同期条件を満たしていないケース', NULL, NULL, N'Draft', NULL, NULL, 30, 5000.0000, NULL, N'FIELDの同一キーを誤削除しない確認', '11111111-1111-1111-1111-111111111106', N'CRM', '2026-07-22T01:05:00+00:00', 4),
    (N'WO-CONFLICT-TEXT', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'文字列競合の基準値', '2026-07-27T01:00:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'基準メモ', '22222222-2222-2222-2222-222222222201', N'CRM', '2026-07-22T02:00:00+00:00', 1),
    (N'WO-CONFLICT-INT', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'整数競合', '2026-07-27T01:30:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'整数競合', '22222222-2222-2222-2222-222222222202', N'CRM', '2026-07-22T02:01:00+00:00', 1),
    (N'WO-CONFLICT-DECIMAL', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'小数競合', '2026-07-27T02:00:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'小数競合', '22222222-2222-2222-2222-222222222203', N'CRM', '2026-07-22T02:02:00+00:00', 1),
    (N'WO-CONFLICT-BOOL', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'真偽値競合', '2026-07-27T02:30:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, NULL, N'真偽値競合', '22222222-2222-2222-2222-222222222204', N'CRM', '2026-07-22T02:03:00+00:00', 1),
    (N'WO-DATETIME-CONFLICT-1001', N'CASE-UPDATE-1001', N'東京都品川区東品川1-2-3', N'日時競合', '2026-07-27T03:00:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'日時競合', '22222222-2222-2222-2222-222222222205', N'CRM', '2026-07-22T02:04:00+00:00', 1),
    (N'WO-CONFLICT-NULL', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'NULL競合', '2026-07-27T03:30:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'基準メモ', '22222222-2222-2222-2222-222222222206', N'CRM', '2026-07-22T02:05:00+00:00', 1),
    (N'WO-CONFLICT-STATUS', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'コード変換競合', '2026-07-27T04:00:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'コード変換競合', '22222222-2222-2222-2222-222222222207', N'CRM', '2026-07-22T02:06:00+00:00', 1),
    (N'WO-CONFLICT-GUID', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'UUID競合', '2026-07-27T04:30:00+00:00', N'佐藤 健', N'Scheduled', NULL, NULL, 60, 10000.0000, 0, N'UUID競合', '22222222-2222-2222-2222-222222222208', N'CRM', '2026-07-22T02:07:00+00:00', 1),
    (N'WO-ERROR-STRING', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'文字数超過', '2026-07-28T01:00:00+00:00', N'佐藤 健', N'Assigned', NULL, NULL, 60, 10000.0000, 0, REPLICATE(N'長い作業メモ', 40), '33333333-3333-3333-3333-333333333301', N'CRM', '2026-07-22T03:00:00+00:00', 1),
    (N'WO-ERROR-INT', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'smallint範囲超過', '2026-07-28T01:30:00+00:00', N'佐藤 健', N'Assigned', NULL, NULL, 40000, 10000.0000, 0, N'整数範囲超過', '33333333-3333-3333-3333-333333333302', N'CRM', '2026-07-22T03:01:00+00:00', 1),
    (N'WO-ERROR-DECIMAL-PRECISION', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'数値precision超過', '2026-07-28T02:00:00+00:00', N'佐藤 健', N'Assigned', NULL, NULL, 60, 12345678.1200, 0, N'整数部桁数超過', '33333333-3333-3333-3333-333333333303', N'CRM', '2026-07-22T03:02:00+00:00', 1),
    (N'WO-ERROR-DECIMAL-SCALE', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'数値scale超過', '2026-07-28T02:30:00+00:00', N'佐藤 健', N'Assigned', NULL, NULL, 60, 1234.5678, 0, N'小数部桁数超過', '33333333-3333-3333-3333-333333333304', N'CRM', '2026-07-22T03:03:00+00:00', 1),
    (N'WO-ERROR-STATUS', N'CASE-TYPES-1001', N'東京都品川区東品川1-2-3', N'未定義コード', '2026-07-28T03:00:00+00:00', N'佐藤 健', N'Escalated', NULL, NULL, 60, 10000.0000, 0, N'コード変換未定義', '33333333-3333-3333-3333-333333333305', N'CRM', '2026-07-22T03:04:00+00:00', 1);

INSERT dbo.WorkOrderAssignment
    (WorkOrderNumber, StaffNo, AssignmentType, AssignedAtUtc, UpdatedAtUtc)
SELECT WorkOrderNumber,
       CASE WHEN WorkOrderNumber IN (N'WO-WAITING-1001', N'WO-NEVER-ELIGIBLE-1001') THEN NULL ELSE N'STAFF-001' END,
       N'Primary', '2026-07-22T04:00:00+00:00', '2026-07-22T04:00:00+00:00'
FROM dbo.WorkOrder;

INSERT dbo.WorkOrderAssignment
    (WorkOrderNumber, StaffNo, AssignmentType, AssignedAtUtc, UpdatedAtUtc)
VALUES
    (N'WO-MULTI-STAFF-1001', N'STAFF-002', N'Assistant', '2026-07-22T04:01:00+00:00', '2026-07-22T04:01:00+00:00'),
    (N'WO-MULTI-STAFF-1001', NULL, N'Observer', '2026-07-22T04:02:00+00:00', '2026-07-22T04:02:00+00:00');
GO
