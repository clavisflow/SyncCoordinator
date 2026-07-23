"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { updateWorkOrder, FieldServiceDataError } from "@/lib/db";
import { WORK_ORDER_STATUSES } from "@/lib/types";

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === "string" ? value.trim() : "";
}

function nullable(value: string): string | null {
  return value.length === 0 ? null : value;
}

function japanDateTime(value: string): string | null {
  if (!value) return null;
  return `${value}:00+09:00`;
}

function editUrl(id: string, error: string): string {
  return `/work-orders/${encodeURIComponent(id)}/edit?error=${encodeURIComponent(error)}`;
}

export async function saveWorkOrder(formData: FormData): Promise<never> {
  const id = text(formData, "id");
  const status = text(formData, "status");
  const technicianName = text(formData, "technicianName");
  const scheduledAt = text(formData, "scheduledAt");
  const workResult = text(formData, "workResult");
  const completedAt = text(formData, "completedAt");
  const estimatedMinutes = text(formData, "estimatedMinutes");
  const estimatedCost = text(formData, "estimatedCost");
  const requiresParts = text(formData, "requiresParts");
  const workNote = text(formData, "workNote");
  const externalTrackingId = text(formData, "externalTrackingId");

  if (!id || id.length > 256) redirect("/?error=" + encodeURIComponent("作業番号が正しくありません。"));
  if (!WORK_ORDER_STATUSES.some((item) => item.value === status)) redirect(editUrl(id, "状態を選択してください。"));
  if (technicianName.length > 80) redirect(editUrl(id, "担当者名は80文字以内で入力してください。"));
  if (workResult.length > 1000) redirect(editUrl(id, "作業結果は1000文字以内で入力してください。"));
  if (workNote.length > 200) redirect(editUrl(id, "作業メモは200文字以内で入力してください。"));
  if (estimatedMinutes && (!/^\d+$/.test(estimatedMinutes) || Number(estimatedMinutes) > 32767)) redirect(editUrl(id, "見積作業時間は0～32767分で入力してください。"));
  if (estimatedCost && (!/^\d+(\.\d{1,2})?$/.test(estimatedCost) || Number(estimatedCost) >= 10_000_000)) redirect(editUrl(id, "見積金額は整数部7桁、小数部2桁以内で入力してください。"));
  if (requiresParts && !["true", "false"].includes(requiresParts)) redirect(editUrl(id, "部品要否を選択してください。"));
  if (externalTrackingId && !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(externalTrackingId)) redirect(editUrl(id, "外部追跡IDはUUID形式で入力してください。"));
  if (scheduledAt && Number.isNaN(Date.parse(`${scheduledAt}:00+09:00`))) redirect(editUrl(id, "訪問予定日時が正しくありません。"));
  if (completedAt && Number.isNaN(Date.parse(`${completedAt}:00+09:00`))) redirect(editUrl(id, "完了日時が正しくありません。"));

  try {
    const updated = await updateWorkOrder(id, {
      ScheduledAt: japanDateTime(scheduledAt),
      TechnicianName: nullable(technicianName),
      Status: status,
      WorkResult: nullable(workResult),
      CompletedAt: japanDateTime(completedAt),
      EstimatedMinutes: nullable(estimatedMinutes),
      EstimatedCost: nullable(estimatedCost),
      RequiresParts: nullable(requiresParts),
      WorkNote: nullable(workNote),
      ExternalTrackingId: nullable(externalTrackingId),
    });
    if (!updated) redirect(editUrl(id, "対象の作業指示が見つかりません。"));
  } catch (error) {
    const message = error instanceof FieldServiceDataError
      ? error.message
      : "作業内容を更新できません。しばらくしてからもう一度お試しください。";
    redirect(editUrl(id, message));
  }

  revalidatePath("/");
  revalidatePath(`/work-orders/${id}`);
  redirect(`/work-orders/${encodeURIComponent(id)}?updated=1`);
}
