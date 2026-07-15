import Link from "next/link";
import { FieldServiceDataError, listWorkOrders } from "@/lib/db";
import { displayVisitDate, displayVisitTime } from "@/lib/format";
import { statusClass, statusLabel } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function WorkOrderListPage() {
  let workOrders = [] as Awaited<ReturnType<typeof listWorkOrders>>;
  let error: string | null = null;
  try {
    workOrders = await listWorkOrders();
  } catch (caught) {
    error = caught instanceof FieldServiceDataError ? caught.message : "作業データを取得できません。";
  }

  return (
    <>
      <header className="screen-header">
        <div><h1>本日の訪問作業</h1><p>訪問予定と作業内容を確認してください。</p></div>
        <div className="assignment-total"><strong>{workOrders.length}</strong><span>件</span></div>
      </header>

      {error && <div className="tablet-alert alert-error" role="alert"><strong>作業データを取得できません</strong><span>{error}</span></div>}
      {!error && workOrders.length === 0 && <div className="tablet-empty"><strong>現在、作業指示はありません</strong><span>新しい作業指示が届くとここに表示されます。</span></div>}

      {workOrders.length > 0 && (
        <section className="order-list" aria-label="作業指示一覧">
          <header className="list-heading"><h2>訪問予定</h2><span>予定時刻が近い順</span></header>
          {workOrders.map((order) => (
            <Link href={`/work-orders/${encodeURIComponent(order.entityId)}`} className="order-card" key={order.entityId}>
              <div className="visit-time"><span>{displayVisitDate(order.payload.ScheduledAt)}</span><strong>{displayVisitTime(order.payload.ScheduledAt)}</strong></div>
              <div className="order-content">
                <div className="order-number"><span>{order.payload.WorkOrderNumber ?? order.entityId}</span><span className={statusClass(order.payload.Status)}>{statusLabel(order.payload.Status)}</span></div>
                <h3>{order.payload.CustomerName ?? "お客様名未設定"}</h3>
                <p className="address-line">{order.payload.Address ?? "住所未設定"}</p>
                <div className="problem-line"><strong>{order.payload.ProductName ?? "製品未設定"}</strong><span>{order.payload.ProblemSummary ?? "依頼内容未設定"}</span></div>
                <div className="technician-line"><span>担当者</span><strong>{order.payload.TechnicianName ?? "未定"}</strong></div>
              </div>
              <span className="open-indicator" aria-hidden="true">›</span>
            </Link>
          ))}
        </section>
      )}
    </>
  );
}
