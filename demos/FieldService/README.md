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

通常のデモ実行では`SyncCoordinator.AppHost`がDockerfileをビルドし、`DATABASE_URL`を注入します。

## Distribution license

Field Serviceのソースはリポジトリ直下のApache License 2.0で提供します。`npm run build`後の処理は、未使用のsharp/libvipsバイナリをstandalone成果物から除外し、実際に残るruntimeパッケージのライセンス原文と`THIRD-PARTY-NOTICES.txt`を成果物へ収集します。Dockerイメージにはこれらの通知が`/app`以下に含まれます。
