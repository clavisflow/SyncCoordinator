CREATE TABLE public.work_order
(
    work_order_no varchar(64) PRIMARY KEY,
    source_case_id varchar(64) NULL,
    case_ref varchar(64) NULL,
    customer_display_name varchar(120) NULL,
    service_address varchar(300) NULL,
    contact_phone varchar(30) NULL,
    product_label varchar(120) NULL,
    problem_summary varchar(120) NULL,
    scheduled_at timestamptz NULL,
    technician_name varchar(80) NULL,
    job_status varchar(20) NOT NULL,
    work_result varchar(1000) NULL,
    completed_at timestamptz NULL,
    estimated_minutes smallint NULL,
    estimated_cost numeric(9,2) NULL,
    requires_parts boolean NULL,
    work_note varchar(200) NULL,
    external_tracking_id uuid NULL,
    source_code varchar(64) NOT NULL,
    modified_at timestamptz NOT NULL,
    mobile_sync_note varchar(200) NULL
);

-- 一度もEligibilityを満たしていない同一キーのCRM行が、このFIELD固有行を削除しないことを確認する。
INSERT INTO public.work_order
    (work_order_no, source_case_id, case_ref, customer_display_name, service_address,
     problem_summary, job_status, work_note, source_code, modified_at)
VALUES
    ('WO-NEVER-ELIGIBLE-1001', 'FIELD-LOCAL-CASE', 'FIELD-LOCAL-CASE', 'FIELD固有 顧客',
     '東京都江東区青海1-1-1', 'FIELDで独立管理している既存レコード', 'draft',
     'CRM側が未割当のまま更新されても削除されません。', 'FIELD', '2026-07-22T01:05:00+00:00');
