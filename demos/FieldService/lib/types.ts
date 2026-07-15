export const WORK_ORDER_STATUSES = [
  { value: "draft", label: "下書き" },
  { value: "assigned", label: "担当決定" },
  { value: "scheduled", label: "訪問予定" },
  { value: "in_progress", label: "作業中" },
  { value: "done", label: "完了" },
  { value: "cancelled", label: "キャンセル" },
] as const;

export type WorkOrderStatus = (typeof WORK_ORDER_STATUSES)[number]["value"];

export type WorkOrderPayload = {
  WorkOrderNumber: string | null;
  CaseId: string | null;
  CaseNumber: string | null;
  CustomerName: string | null;
  Address: string | null;
  Phone: string | null;
  ProductName: string | null;
  ProblemSummary: string | null;
  ScheduledAt: string | null;
  TechnicianName: string | null;
  Status: string | null;
  WorkResult: string | null;
  CompletedAt: string | null;
};

export type WorkOrder = {
  entityId: string;
  originSystem: string;
  payload: WorkOrderPayload;
  updatedAtUtc: Date;
};

export function statusLabel(value: string | null): string {
  return WORK_ORDER_STATUSES.find((status) => status.value === value)?.label ?? value ?? "未設定";
}

export function statusClass(value: string | null): string {
  if (value === "done") return "status status-completed";
  if (value === "in_progress") return "status status-progress";
  if (value === "scheduled") return "status status-scheduled";
  if (value === "assigned") return "status status-assigned";
  if (value === "cancelled") return "status status-cancelled";
  return "status status-draft";
}
