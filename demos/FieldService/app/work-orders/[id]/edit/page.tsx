import Link from "next/link";
import { notFound } from "next/navigation";
import { saveWorkOrder } from "@/app/actions";
import { FieldServiceDataError, findWorkOrder } from "@/lib/db";
import { toDateTimeLocal } from "@/lib/format";
import { WORK_ORDER_STATUSES } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function WorkOrderEditPage({ params, searchParams }: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ error?: string }>;
}) {
  const { id } = await params;
  const { error } = await searchParams;
  let order;
  try {
    order = await findWorkOrder(id);
  } catch (caught) {
    const message = caught instanceof FieldServiceDataError ? caught.message : "作業データを取得できません。";
    return <><Link href="/" className="screen-back">‹ 作業一覧へ</Link><div className="tablet-alert alert-error" role="alert"><strong>編集画面を開けません</strong><span>{message}</span></div></>;
  }
  if (!order) notFound();

  return (
    <>
      <Link href={`/work-orders/${encodeURIComponent(id)}`} className="screen-back">‹ 作業詳細へ戻る</Link>
      <header className="edit-header"><span>作業指示　{order.payload.WorkOrderNumber ?? order.entityId}</span><h1>作業内容を更新</h1></header>
      {error && <div className="tablet-alert alert-error" role="alert"><strong>更新できません</strong><span>{error}</span></div>}

      <form action={saveWorkOrder} className="tablet-form">
        <input type="hidden" name="id" value={order.entityId} />
        <section className="form-card"><h2>訪問・担当</h2><div className="field-grid">
          <label><span>訪問予定日時</span><input name="scheduledAt" type="datetime-local" defaultValue={toDateTimeLocal(order.payload.ScheduledAt)} /></label>
          <label><span>担当者名</span><input name="technicianName" maxLength={80} defaultValue={order.payload.TechnicianName ?? ""} placeholder="例：山田 太郎" /></label>
        </div></section>
        <section className="form-card"><h2>進捗・作業結果</h2><div className="field-grid">
          <label><span>状態</span><select name="status" defaultValue={order.payload.Status ?? "draft"} required>{WORK_ORDER_STATUSES.map((status) => <option value={status.value} key={status.value}>{status.label}</option>)}</select></label>
          <label><span>完了日時</span><input name="completedAt" type="datetime-local" defaultValue={toDateTimeLocal(order.payload.CompletedAt)} /></label>
          <label className="wide-field"><span>作業結果</span><textarea name="workResult" rows={8} maxLength={1000} defaultValue={order.payload.WorkResult ?? ""} placeholder="診断内容、交換部品、お客様への説明などを入力" /></label>
        </div></section>
        <div className="tablet-actions"><Link href={`/work-orders/${encodeURIComponent(id)}`} className="tablet-button secondary-button">キャンセル</Link><button type="submit" className="tablet-button primary-button">更新を保存</button></div>
      </form>
    </>
  );
}
