# SyncCoordinator

A（SQL Server）または B（MySQL）で発生した作業依頼を C（SQL Server）へ同期し、C の処理結果を `OriginSystem` の A または B へ返す独立ソリューションです。A と B を直接結ぶルートは初期データに含めません。システムとルートは追加可能な構成です。

実際の業務テーブルは未確定なので、このリポジトリは業務項目を定義していません。`EntityPayload.Fields`、`ISyncConnector`、`SampleSyncEntity` を差し替え境界として用意しています。

## 確認済みの開発環境

- OS: Windows 10 (win-x64)
- .NET SDK: 9.0.315、10.0.300、10.0.301（`global.json` は 10.0.301）
- Aspire CLI: 13.4.4
- Aspire project packages/templates: 13.4.6
- .NET workload: 追加 workload なし（このソリューションには不要）
- 作成時点で既存 `AGENTS.md`、既存ファイル、Git repository はなし

## プロジェクトと依存関係

```text
SyncCoordinator.Contracts
          ↑
SyncCoordinator.Core
          ↑
SyncCoordinator.Infrastructure
       ↗      ↖
Worker          Web  ← SyncCoordinator.ServiceDefaults
   ↖            ↗
   SyncCoordinator.AppHost

SyncCoordinator.Tests → Core / Infrastructure / Contracts
```

| プロジェクト | 責務 |
|---|---|
| `SyncCoordinator.Contracts` | 共通 payload、変更キュー、適用要求の契約 |
| `SyncCoordinator.Core` | ルート処理、3-way 競合判定、Inbox/Checkpoint 抽象化 |
| `SyncCoordinator.Infrastructure` | 管理DB EF Core、SQL Server/MySQL Dapper Connector、DI |
| `SyncCoordinator.Worker` | 差分キューのポーリングと同期ユースケース実行、Windows Service host |
| `SyncCoordinator.Web` | Blazor Interactive Server の管理画面。同期ロジックは持たず、Coreの管理サービスを使用する |
| `SyncCoordinator.AppHost` | SQL Server 管理DB、Worker、Web の Aspire 開発構成 |
| `SyncCoordinator.ServiceDefaults` | OpenTelemetry、service discovery、resilience、Health Check |
| `SyncCoordinator.Tests` | 競合、配送ID、ループ防止の unit test |

## 同期フロー

1. 各業務DBの Trigger が同じDBの `SyncChangeQueue` に `MessageId` と変更IDを追加する。別DBや Coordinator は呼ばない。
2. Worker が `QueueId > Checkpoint` を batch 取得する。業務テーブルの全件ポーリングはしない。
3. Source Connector が業務レコードを共通 `EntityPayload` に変換し、管理DBの有効ルートを解決する。
4. Inbox を取得し、前回採用 snapshot・incoming・宛先 current を項目単位で比較する。
5. 非競合項目を自動マージし、競合項目には route/field policy を適用する。
6. Destination Connector が業務更新と `SyncAppliedMessage` を単一DBローカルトランザクションで保存する。
7. Coordinator 管理DBへ採用 snapshot、競合前後と採用値、Inbox 状態を保存する。
8. 全ルート完了後にだけ Checkpoint を進める。

管理DBと業務DBの分散トランザクションは使用しません。障害時は同じ決定的 `DeliveryMessageId` で再実行され、宛先 `SyncAppliedMessage` によって二重適用を防ぐ at-least-once 処理です。Coordinator 適用時の MessageId は SQL Server `SESSION_CONTEXT` / MySQL connection user variable を経由して Trigger のキューへ渡り、Worker が適用済みメッセージを読み飛ばして同期ループを止めます。

## 起動

前提として Docker Desktop など Aspire が利用できるコンテナランタイムを起動します。

```powershell
dotnet tool restore
dotnet restore SyncCoordinator.sln
dotnet build SyncCoordinator.sln --no-restore
dotnet test SyncCoordinator.sln --no-build
aspire run --apphost src/SyncCoordinator.AppHost/SyncCoordinator.AppHost.csproj
```

AppHost は開発用 SQL Server に `coordinator-db` を作り、Worker と Web に接続文字列を注入します。Development では EF Core migration とサンプル route seed を自動適用します。本番の `CoordinatorDatabase:ApplyMigrations` は既定で `false` です。

## 管理画面

Blazor Interactive Serverで次の画面を提供します。

UIコンポーネントはMITライセンスの`Radzen.Blazor` 11.1.0を使用し、無償の`Software`テーマを採用しています。Radzen Blazor Studio、Premiumテーマ、有償サポートには依存しません。第三者ライセンス全文は`THIRD-PARTY-NOTICES.md`に記録しています。

| URL | 機能 |
|---|---|
| `/` | 同期概要、件数、最近のルートと競合 |
| `/systems` | システムの登録・編集・有効化、暗号化したDB接続情報の保存、接続テスト |
| `/routes` | 同期ルート一覧、新規作成、編集 |
| `/routes/{id}` | 固定宛先／OriginSystem、Field／Record scope、既定・項目別競合ポリシー |
| `/mappings` | 実DBからテーブル／列を取得し、ルート・宛先別に列とキーをマッピング |
| `/conflicts` | 競合履歴一覧 |
| `/conflicts/{id}` | base、incoming、current、adopted、適用ポリシーの比較 |
| `/operations` | Queue Checkpoint、Inbox、エラー、設定変更監査 |

設定更新は`ICoordinatorAdminService`経由で行い、Blazorコンポーネントには同期判断を実装しません。A⇔Bの固定ルート、同一システム間ルート、重複項目ポリシーはCoreで拒否します。設定保存時は`ConfigurationAudit`へ変更履歴を追加します。

DB接続文字列はASP.NET Core Data Protectionで暗号化してCoordinator管理DBへ保存します。パスワードは画面へ再表示せず、監査履歴にも保存しません。本番でWebとWorkerを別サービスアカウントにする場合は、両者が読み書きできる保護済みフォルダーを`DataProtection:KeyRingPath`に設定し、鍵のアクセス制御とバックアップを行ってください。暗号鍵を失うと保存済み接続情報は復号できません。

テーブル／列一覧の取得には対象DBの`INFORMATION_SCHEMA`を使用します。管理画面はTriggerや業務テーブルを自動作成・変更しません。保存したマッピングは実業務Connectorを実装するときの設定境界であり、現在の`SampleJsonRelationalConnector`は従来どおりサンプルテーブル専用です。

競合詳細と失敗Inboxは現時点では参照専用です。手動再適用は実業務Connector、権限、監査要件が決まってから追加します。

## 業務DB Connector の差し替え

`SampleJsonRelationalConnector` は構造未確定期間だけ使う executable sample です。実テーブル確定後はシステムごとに `ISyncConnector` を実装します。

- `ReadChangesAsync`: `SyncChangeQueue` を Checkpoint の次から読む。
- `WasAppliedMessageAsync`: `SyncAppliedMessage` で Coordinator 自身の更新を判定する。
- `ReadMessageAsync`: 業務行を共通 payload へ変換し、C の結果では保存済み `OriginSystem` を返す。
- `ReadCurrentAsync`: 競合比較に使う宛先現在値を同じ共通項目名へ変換する。
- `ApplyAsync`: `SyncAppliedMessage` と業務更新を同じローカルトランザクションで冪等適用する。

サンプル Connector を有効にする場合は Worker 設定へ次を追加し、対応する connection string を指定します。本番ではこの設定をそのまま使わず、業務 Connector 登録へ置き換えてください。

```json
{
  "SyncCoordinator": {
    "Connectors": {
      "Systems": [
        { "SystemCode": "A", "Provider": "SqlServer", "ConnectionStringName": "system-a", "Enabled": true },
        { "SystemCode": "B", "Provider": "MySql", "ConnectionStringName": "system-b", "Enabled": true },
        { "SystemCode": "C", "Provider": "SqlServer", "ConnectionStringName": "system-c", "Enabled": true }
      ]
    }
  }
}
```

MySQL のサンプルでは connection string に `GuidFormat=Char36` を設定します。

`EntityPayload.Fields` は Connector が管理する同期対象項目を毎回同じ集合で返す契約です。項目省略を削除として扱うため、部分 payload は渡しません。`MergeAndNotify` で業務固有マージが必要なら `IConflictValueMerger` を差し替えます。既定実装は値を推測せず保留します。

## SQL テンプレート

- SQL Server: `database/sqlserver/001_sync_support_sample.sql`、`database/sqlserver/trigger_template.sql`
- MySQL: `database/mysql/001_sync_support_sample.sql`、`database/mysql/trigger_template.sql`

Trigger template は置換トークンを含むため、そのまま実行しません。物理削除は、IDだけの変更キューでは削除後 payload を復元できません。soft-delete または tombstone/履歴表のどちらを使うか業務DBごとに決め、Connector と DELETE Trigger をセットで実装します。

キューと適用済みメッセージには保持期間を定め、すべての稼働 Worker の Checkpoint より古い `SyncChangeQueue` と、最大再送期間を過ぎた `SyncAppliedMessage` を別ジョブで削除してください。

## 管理DB migration

初期 migration は `SyncCoordinator.Infrastructure/Persistence/Migrations` に含まれます。

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update `
  --project src/SyncCoordinator.Infrastructure/SyncCoordinator.Infrastructure.csproj `
  --startup-project src/SyncCoordinator.Worker/SyncCoordinator.Worker.csproj
```

設計時接続先を変える場合は `SYNC_COORDINATOR_DESIGN_CONNECTION` 環境変数を使います。本番では migration bundle または承認済み SQL script として配備する運用を推奨します。

## Windows Service 配備

```powershell
dotnet publish src/SyncCoordinator.Worker/SyncCoordinator.Worker.csproj `
  -c Release -r win-x64 --self-contained false -o artifacts/worker

sc.exe create SyncCoordinatorWorker `
  binPath= "C:\Services\SyncCoordinator\SyncCoordinator.Worker.exe" `
  start= auto
```

接続文字列は `ConnectionStrings__coordinator-db`、`ConnectionStrings__system-a` などの環境変数または Windows Service 用の保護された構成ソースから渡します。サービスアカウントには各業務DBで必要最小限の queue read、対象行 read/write、`SyncAppliedMessage` read/write 権限を付与します。

## 設計判断

- `docs/decisions/0001-solution-boundaries.md`
- `docs/decisions/0002-delivery-and-idempotency.md`
- `docs/decisions/0003-conflict-resolution.md`
- `docs/decisions/0004-management-ui-boundary.md`
- `docs/decisions/0005-radzen-ui-components.md`
- `docs/decisions/0006-managed-connections-and-schema-mapping.md`

現時点の意図的な未実装は、保存したmappingを使用する実業務Connector、削除 semantics、通知先（メール/Teams等）、管理画面からの競合解決操作、認証・認可です。いずれも業務仕様や運用先を推測せず、Coreの履歴とinterfaceを先に用意しています。
