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
    source_code varchar(64) NOT NULL,
    modified_at timestamptz NOT NULL,
    mobile_sync_note varchar(200) NULL
);
