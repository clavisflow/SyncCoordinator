# Field Service (Next.js + PostgreSQL)

訪問担当者向けのデモアプリです。Next.js App RouterのServer Components / Server Actionsから、lower_snake_case列を持つPostgreSQLの`public.work_order`を読み書きします。画面内のモデル名は共通payload名へaliasし、CRMとは異なる小文字の状態コードを使用します。

## Runtime

- Node.js 24
- Next.js 16 / TypeScript
- PostgreSQL (`DATABASE_URL`)
- DDLやmigrationは実行しません。業務テーブルはデモAppHostの初期化SQLが作成し、同期補助テーブルとTriggerはCoordinator管理画面から配備します。

## Local verification

```powershell
npm install
npm run typecheck
npm run build
```

通常のデモ実行では`SyncCoordinator.Demo.AppHost`がDockerfileをビルドし、`DATABASE_URL`を注入します。
