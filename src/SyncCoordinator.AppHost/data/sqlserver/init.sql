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
    CaseId nvarchar(64) NULL,
    CaseNumber nvarchar(64) NULL,
    CustomerName nvarchar(200) NULL,
    Address nvarchar(500) NULL,
    Phone nvarchar(40) NULL,
    ProductName nvarchar(200) NULL,
    ProblemSummary nvarchar(500) NULL,
    ScheduledAt datetimeoffset(7) NULL,
    TechnicianName nvarchar(200) NULL,
    Status nvarchar(40) NOT NULL,
    WorkResult nvarchar(max) NULL,
    CompletedAt datetimeoffset(7) NULL,
    OriginSystem nvarchar(64) NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    PriorityCode tinyint NULL
);
GO
