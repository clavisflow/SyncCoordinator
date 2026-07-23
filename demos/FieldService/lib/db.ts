import "server-only";

import { Pool, type PoolClient, type PoolConfig, type QueryResultRow } from "pg";
import type { WorkOrder, WorkOrderPayload } from "@/lib/types";

const CONTRACT_FIELDS = [
  "WorkOrderNumber", "CaseId", "CaseNumber", "CustomerName", "Address", "Phone",
  "ProductName", "ProblemSummary", "ScheduledAt", "TechnicianName", "Status",
  "WorkResult", "CompletedAt",
  "EstimatedMinutes", "EstimatedCost", "RequiresParts", "WorkNote", "ExternalTrackingId",
] as const;

type Row = QueryResultRow & {
  WorkOrderNumber: string;
  CaseId: string | null;
  CaseNumber: string | null;
  CustomerName: string | null;
  Address: string | null;
  Phone: string | null;
  ProductName: string | null;
  ProblemSummary: string | null;
  ScheduledAt: Date | string | null;
  TechnicianName: string | null;
  Status: string;
  WorkResult: string | null;
  CompletedAt: Date | string | null;
  EstimatedMinutes: number | string | null;
  EstimatedCost: number | string | null;
  RequiresParts: boolean | string | null;
  WorkNote: string | null;
  ExternalTrackingId: string | null;
  OriginSystem: string;
  UpdatedAtUtc: Date | string;
};

declare global {
  // eslint-disable-next-line no-var
  var fieldServicePool: Pool | undefined;
}

export class FieldServiceDataError extends Error {
  constructor(message: string, options?: ErrorOptions) {
    super(message, options);
    this.name = "FieldServiceDataError";
  }
}

function connectionConfig(): PoolConfig {
  const raw = process.env["ConnectionStrings__demo-field-service-db"] ?? process.env.DATABASE_URL;
  if (!raw?.trim()) {
    throw new FieldServiceDataError(
      "PostgreSQLの接続情報が設定されていません。demo-field-service-db または DATABASE_URL を設定してください。",
    );
  }
  if (/^postgres(?:ql)?:\/\//i.test(raw)) return { connectionString: raw };

  const values = new Map<string, string>();
  for (const part of raw.split(";")) {
    const separator = part.indexOf("=");
    if (separator > 0) values.set(part.slice(0, separator).trim().toLowerCase(), part.slice(separator + 1).trim());
  }
  const host = values.get("host") ?? values.get("server");
  const database = values.get("database") ?? values.get("initial catalog");
  const user = values.get("username") ?? values.get("user id") ?? values.get("user");
  if (!host || !database || !user) {
    throw new FieldServiceDataError("PostgreSQLの接続文字列の形式を読み取れません。");
  }
  const sslMode = values.get("ssl mode")?.toLowerCase();
  return {
    host,
    port: Number(values.get("port") ?? "5432"),
    database,
    user,
    password: values.get("password"),
    ssl: sslMode && !["disable", "prefer", "allow"].includes(sslMode)
      ? { rejectUnauthorized: sslMode === "verify-full" }
      : undefined,
  };
}

function pool(): Pool {
  if (!global.fieldServicePool) {
    global.fieldServicePool = new Pool({ ...connectionConfig(), max: 10, idleTimeoutMillis: 30_000 });
  }
  return global.fieldServicePool;
}

function scalar(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (value instanceof Date) return value.toISOString();
  return String(value);
}

function mapRow(row: Row): WorkOrder {
  const payload = {} as WorkOrderPayload;
  for (const field of CONTRACT_FIELDS) payload[field] = scalar(row[field]);
  return {
    entityId: row.WorkOrderNumber,
    originSystem: row.OriginSystem,
    payload,
    updatedAtUtc: new Date(row.UpdatedAtUtc),
  };
}

function dataError(error: unknown): never {
  if (error instanceof FieldServiceDataError) throw error;
  console.error("Field Service database operation failed", error);
  throw new FieldServiceDataError("作業データを取得できません。しばらくしてからもう一度お試しください。", { cause: error });
}

const selectColumns = `
  work_order_no AS "WorkOrderNumber", source_case_id AS "CaseId", case_ref AS "CaseNumber",
  customer_display_name AS "CustomerName", service_address AS "Address", contact_phone AS "Phone",
  product_label AS "ProductName", problem_summary AS "ProblemSummary", scheduled_at AS "ScheduledAt",
  technician_name AS "TechnicianName", job_status AS "Status", work_result AS "WorkResult",
  completed_at AS "CompletedAt", estimated_minutes AS "EstimatedMinutes", estimated_cost AS "EstimatedCost",
  requires_parts AS "RequiresParts", work_note AS "WorkNote", external_tracking_id AS "ExternalTrackingId",
  source_code AS "OriginSystem", modified_at AS "UpdatedAtUtc"`;

export async function listWorkOrders(): Promise<WorkOrder[]> {
  try {
    const result = await pool().query<Row>(`
      SELECT ${selectColumns}
      FROM public.work_order
      ORDER BY scheduled_at NULLS LAST, modified_at DESC`);
    return result.rows.map(mapRow);
  } catch (error) {
    return dataError(error);
  }
}

async function findWithClient(client: Pool | PoolClient, entityId: string, lock = false): Promise<WorkOrder | null> {
  const result = await client.query<Row>(`
    SELECT ${selectColumns}
    FROM public.work_order
    WHERE work_order_no = $1
    ${lock ? "FOR UPDATE" : ""}`, [entityId]);
  return result.rows[0] ? mapRow(result.rows[0]) : null;
}

export async function findWorkOrder(entityId: string): Promise<WorkOrder | null> {
  try {
    return await findWithClient(pool(), entityId);
  } catch (error) {
    return dataError(error);
  }
}

export type WorkOrderUpdate = Pick<WorkOrderPayload,
  "ScheduledAt" | "TechnicianName" | "Status" | "WorkResult" | "CompletedAt" |
  "EstimatedMinutes" | "EstimatedCost" | "RequiresParts" | "WorkNote" | "ExternalTrackingId">;

export async function updateWorkOrder(entityId: string, update: WorkOrderUpdate): Promise<boolean> {
  const client = await pool().connect();
  try {
    await client.query("BEGIN");
    const current = await findWithClient(client, entityId, true);
    if (!current) {
      await client.query("ROLLBACK");
      return false;
    }
    const result = await client.query(`
      UPDATE public.work_order
      SET scheduled_at=$1, technician_name=$2, job_status=$3,
          work_result=$4, completed_at=$5, estimated_minutes=$6, estimated_cost=$7,
          requires_parts=$8, work_note=$9, external_tracking_id=$10,
          modified_at=CURRENT_TIMESTAMP
      WHERE work_order_no=$11`,
    [update.ScheduledAt, update.TechnicianName, update.Status, update.WorkResult, update.CompletedAt,
      update.EstimatedMinutes, update.EstimatedCost, update.RequiresParts, update.WorkNote,
      update.ExternalTrackingId, entityId]);
    await client.query("COMMIT");
    return result.rowCount === 1;
  } catch (error) {
    await client.query("ROLLBACK").catch(() => undefined);
    if (error instanceof FieldServiceDataError) throw error;
    console.error("Field Service work order update failed", error);
    throw new FieldServiceDataError("作業内容を更新できません。通信状態を確認して、もう一度お試しください。", { cause: error });
  } finally {
    client.release();
  }
}
