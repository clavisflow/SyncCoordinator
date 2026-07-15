SET NAMES utf8mb4 COLLATE utf8mb4_0900_ai_ci;
SET CHARACTER SET utf8mb4;

CREATE TABLE SupportCase
(
    CaseNumber VARCHAR(64) NOT NULL PRIMARY KEY,
    CustomerName VARCHAR(200) NULL,
    Email VARCHAR(320) NULL,
    Phone VARCHAR(40) NULL,
    ProductName VARCHAR(200) NULL,
    SerialNumber VARCHAR(100) NULL,
    Subject VARCHAR(300) NULL,
    Description TEXT NULL,
    PreferredVisitDate DATE NULL,
    Status VARCHAR(40) NOT NULL,
    ResponseMessage TEXT NULL,
    OriginSystem VARCHAR(64) NOT NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

INSERT INTO SupportCase
    (CaseNumber, CustomerName, Email, Phone, ProductName, SerialNumber, Subject, Description,
     PreferredVisitDate, Status, ResponseMessage, OriginSystem, UpdatedAtUtc)
VALUES
    ('CASE-1001', '山田 太郎', 'taro.yamada@example.com', '090-1234-5678', 'エアコン AC-200',
     'AC200-2026-00125', '冷風が出ない', '運転を開始しても送風のみで、冷たい風が出ません。',
     DATE_ADD(UTC_DATE(), INTERVAL 3 DAY), 'New', NULL, 'PORTAL', UTC_TIMESTAMP(6));
