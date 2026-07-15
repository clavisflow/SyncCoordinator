import Link from "next/link";
import { notFound } from "next/navigation";
import { FieldServiceDataError, findWorkOrder } from "@/lib/db";
import { displayDateTime } from "@/lib/format";
import { statusClass, statusLabel } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function WorkOrderDetailsPage({ params, searchParams }: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ updated?: string }>;
}) {
  const { id } = await params;
  const { updated } = await searchParams;
  let order;
  try {
    order = await findWorkOrder(id);
  } catch (caught) {
    const message = caught instanceof FieldServiceDataError ? caught.message : "作業データを取得できません。";
    return <><Link href="/" className="screen-back">‹ 作業一覧へ</Link><div className="tablet-alert alert-error" role="alert"><strong>作業データを取得できません</strong><span>{message}</span></div></>;
  }
  if (!order) notFound();

  return (
    <>
      <Link href="/" className="screen-back">‹ 作業一覧へ</Link>
      <header className="record-header">
        <div><span>作業指示番号</span><h1>{order.payload.WorkOrderNumber ?? order.entityId}</h1><p>案件番号　{order.payload.CaseNumber ?? "未設定"}</p></div>
        <span className={statusClass(order.payload.Status)}>{statusLabel(order.payload.Status)}</span>
      </header>
      {updated === "1" && <div className="tablet-alert alert-success" role="status"><strong>作業内容を更新しました</strong></div>}

      <section className="visit-banner">
        <div><span>次の訪問予定</span><strong>{displayDateTime(order.payload.ScheduledAt)}</strong><p>担当者　{order.payload.TechnicianName ?? "未定"}</p></div>
        <Link href={`/work-orders/${encodeURIComponent(order.entityId)}/edit`} className="tablet-button primary-button">作業内容を更新</Link>
      </section>

      <div className="detail-grid">
        <section className="detail-card"><h2>お客様・訪問先</h2><dl>
          <div><dt>お客様名</dt><dd>{order.payload.CustomerName ?? "未設定"}</dd></div>
          <div><dt>住所</dt><dd>{order.payload.Address ?? "未設定"}</dd></div>
          <div><dt>電話番号</dt><dd>{order.payload.Phone ? <a href={`tel:${order.payload.Phone}`}>{order.payload.Phone}</a> : "未設定"}</dd></div>
        </dl></section>
        <section className="detail-card"><h2>修理内容</h2><dl>
          <div><dt>製品</dt><dd>{order.payload.ProductName ?? "未設定"}</dd></div>
          <div><dt>症状・依頼内容</dt><dd>{order.payload.ProblemSummary ?? "未設定"}</dd></div>
        </dl></section>
      </div>
      <section className="detail-card result-card"><h2>作業結果</h2><dl>
        <div><dt>状態</dt><dd>{statusLabel(order.payload.Status)}</dd></div>
        <div><dt>完了日時</dt><dd>{displayDateTime(order.payload.CompletedAt)}</dd></div>
        <div><dt>作業結果</dt><dd className="preserve-lines">{order.payload.WorkResult ?? "未入力"}</dd></div>
      </dl></section>
      <p className="sync-note">最終更新 {displayDateTime(order.updatedAtUtc)}　連携元 {order.originSystem}</p>
    </>
  );
}
