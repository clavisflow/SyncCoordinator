import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";

export const metadata: Metadata = {
  title: { default: "訪問作業管理", template: "%s | 訪問作業管理" },
  description: "現場担当者向けの訪問作業管理デモ",
  robots: { index: false, follow: false },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="ja">
      <body>
        <div className="field-tablet">
          <header className="tablet-topbar">
            <Link href="/" className="tablet-brand" aria-label="訪問作業管理 トップ">
              <span className="brand-badge">FS</span>
              <strong>訪問作業管理</strong>
            </Link>
            <strong className="topbar-title">本日の作業</strong>
            <span className="sync-state"><i aria-hidden="true" />同期済み</span>
          </header>
          <nav className="tablet-nav" aria-label="メインメニュー">
            <Link href="/" className="nav-current"><span className="clipboard-icon" aria-hidden="true" /><strong>作業指示</strong></Link>
            <div className="operator-panel"><span>担当者</span><strong>フィールド担当者</strong></div>
          </nav>
          <main className="tablet-content">{children}</main>
          <footer className="tablet-footer"><span>Field Service</span><span>PostgreSQL / SyncCoordinator</span></footer>
        </div>
      </body>
    </html>
  );
}
