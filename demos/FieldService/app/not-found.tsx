import Link from "next/link";

export default function NotFound() {
  return <div className="tablet-empty"><strong>作業指示が見つかりません</strong><span>削除されたか、作業番号が変更された可能性があります。</span><Link href="/" className="tablet-button secondary-button">作業一覧へ戻る</Link></div>;
}
