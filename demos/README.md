# SyncCoordinator demo systems

SyncCoordinator の異種DB同期を、次の3つの独立した業務システムで確認するデモです。

| System code | Application | Technology | Database | Synchronized entity |
|---|---|---|---|---|
| `PORTAL` | Customer Portal | Laravel 12 / Blade | MySQL | `SupportCase` |
| `CRM` | CRM | ASP.NET Core | SQL Server | `SupportCase`, `WorkOrder` |
| `FIELD` | Field Service | Next.js 16 / TypeScript | PostgreSQL | `WorkOrder` |

同期経路は `Customer Portal ⇄ CRM ⇄ Field Service` です。Customer Portal と Field Service は直接同期しません。

## Projects

- `CustomerPortal`: Laravel／Bladeによる、顧客向け問い合わせの登録・確認・編集
- `Crm`: 問い合わせ対応、回答、作業指示の発行・確認
- `FieldService`: Next.js／TypeScriptによる、訪問予定、担当者、作業状況、作業結果の更新
- `../src/SyncCoordinator.AppHost`: 3つのDBコンテナ、3アプリ、Coordinator Web、Workerを1つにまとめるAspire AppHost

デモアプリは`SupportCase`と`WorkOrder`の通常の業務テーブルを直接使用します。CoordinatorのRDB Connectorは管理DBに保存されたテーブル／列マッピングからこれらを読み書きします。

デモDBの物理スキーマは意図的に揃えていません。PortalとCRMの問い合わせ列は別名・別最大長、Field Serviceの`public.work_order`はlower_snake_caseです。CRMだけの`OwnerTeam`／`PriorityCode`、Field Serviceだけの`mobile_sync_note`も同期対象外列として持ちます。作業状態はCRMの`Completed`とField Serviceの`done`のようにコード体系も異なり、初期マッピングの双方向コード変換で吸収します。短い同期先列へ収まらない値は、明示的に切詰めを選ばない限り保留されます。

業務テーブルの読み書き、DBセットアップ、配送、競合、再試行、ループ防止は通常運用と同じ経路です。デモ専用の業務分岐やシステムコード判定はありません。

## Start the demo

デモDBのホスト側ポートは、管理画面へ保存した接続情報が再起動後も変わらないよう固定しています。

| System | Provider | Host | Port | Database | User |
| --- | --- | --- | ---: | --- | --- |
| Customer Portal | MySQL | `localhost` | `13306` | `DemoCustomerPortal` | `root` |
| CRM | SQL Server | `localhost` | `14330` | `DemoCrm` | `sa` |
| Field Service | PostgreSQL | `localhost` | `15432` | `DemoFieldService` | `postgres` |

3つのDBはローカルデモ専用の共通パスワード `SyncDemo123!` を使用します。DBデータ自体はDocker named volumeへ永続化されます。

CodexはDocker Desktop、コンテナ、AppHost、Web、Workerを起動・停止しません。次の操作は実行環境を管理するユーザーが行います。

1. Docker Desktopを起動する。
2. リポジトリルートで次を実行する。

   ```powershell
   aspire run --apphost src/SyncCoordinator.AppHost/SyncCoordinator.AppHost.csproj
   ```

3. Aspire Dashboardから `coordinator-web` を開き、初回管理者設定を行う。
4. シード済みの各ルールで「DBへ反映」→「すべてのDBを確認」→「有効化」を行う。
5. `demo-customer-portal`、`demo-crm`、`demo-field-service` をそれぞれ開く。

Visual StudioではAppHostの起動プロファイルに`Demo`を選びます。デモを動かさずCoordinator WebとWorkerだけを起動する場合は`Core`を選びます。

E2Eは別の構成定義を持たず、このデモと同じAppHost topologyを`RunMode=E2E`で使用します。DBポートとデータだけをテストごとに一時化するため、デモの永続データには触れません。実行方法は[`../docs/e2e-testing.md`](../docs/e2e-testing.md)を参照してください。

初回はCustomer PortalとField ServiceのDockerイメージをビルドするため、2回目以降より起動に時間がかかります。

AppHostは既定でこのデモ一式を起動します。AppHostを終了すると3つのDBコンテナも終了し、次回までデータだけがDocker volumeへ保存されます。

## Demo scenario

1. 2つのデモルートをDB反映・検証して有効化する。Workerが次の7レコードに9件の競合履歴を生成する。

   | レコードキー | シナリオ | 期待状態 |
   |---|---|---|
   | `CASE-UPDATE-1001` | 更新競合 | 更新が「要対応」 |
   | `CASE-DELETE-1001` | 削除競合 | 削除が「要対応」 |
   | `CASE-UPDATE-THEN-DELETE-1001` | 更新競合後に削除競合 | 更新と削除が「要対応」 |
   | `CASE-DELETE-THEN-UPDATE-1001` | 削除競合後に更新競合 | 削除と更新が「要対応」 |
   | `CASE-RESOLVED-1001` | 解決済み更新競合 | 受信値を採用して「解決済み」 |
   | `CASE-DATE-CONFLICT-1001` | 訪問希望日の競合 | 7月24日と7月23日の選択が「要対応」 |
   | `WO-DATETIME-CONFLICT-1001` | 作業予定日時の競合 | 7月22日14:00と11:30の選択が「要対応」 |

2. コンフリクト履歴の状態別件数が「要対応 8件」「解決済み 1件」になっていることを確認する。
3. `CASE-UPDATE-1001`を開き、受信値、同期先の現在値、または手入力値を選んで解決する。
4. `CASE-DELETE-1001`を開き、削除をCRMへ適用するかCRMのレコードを維持するか選んで解決する。
5. 重複シナリオの古い競合には後続、最新の競合には古い未解決競合の案内が表示され、相互に移動できることを確認する。
6. 古い競合から解決した場合は次の競合が最新値で再評価され、最新の競合から解決した場合は古い競合が「対応不要」になることを確認する。
7. CRMで受付状態と顧客への回答を更新し、Customer Portalへ戻ることを確認する。
8. CRMの問い合わせ詳細からWork Orderを発行する。
9. Field ServiceにWork Orderが同期されたことを確認する。
10. Field Serviceで訪問予定、担当者、状態、作業結果を更新する。
11. CRMに作業結果が戻ったことを確認する。
12. CRMでSupport Caseを完了にし、Customer Portalへの反映を確認する。

3分の概要動画として撮影する場合は、競合シナリオを別編に分け、1件の問い合わせが3システムを往復する流れに絞る。秒単位の構成、固定入力値、撮影前のリセット手順は[`video-demo-script.md`](video-demo-script.md)を参照する。

## Database initialization

デモ用AppHostは、空のDocker volumeを初めて作成するときに次を自動構築します。

- Coordinator管理DBとEF Core migration
- SQL Serverの`SupportCase`、`WorkOrder`業務テーブル
- MySQLの`SupportCase`業務テーブルと初期問い合わせ
- PostgreSQLのlower_snake_caseな`work_order`業務テーブル
- `PORTAL`、`CRM`、`FIELD`のシステム定義と接続情報
- `SupportCase`と`WorkOrder`の無効な双方向同期ルールとテーブル／列マッピング

`Customer Portal - CRM`ルートをDB反映・検証して有効化すると、デモWorkerはCRM側の行がまだなくても作成し、更新競合と削除競合を一度ずつ生成します。通常構成や`Core`起動プロファイルでは生成しません。

同期補助テーブルとTriggerは初期化SQLでは作りません。管理画面でDBへ反映し、検証した後にルールを有効化します。初期問い合わせはTrigger配備前に存在するため、配備後にCustomer Portalで更新して最初の同期を開始します。

初期化SQLを変更した後に既存volumeを使い続けるとSQLは再実行されません。デモを完全に初期化し直す場合は、AppHostを停止してからDocker Desktopでデモ用volumeを削除してください。

MySQLの初期データは`utf8mb4`を明示して投入します。修正前に作成したvolumeで日本語が文字化けしている場合も、デモ用volumeを削除してから再起動すると正しい初期データで再作成されます。

## Payload contracts

`SupportCase`は以下の全項目を毎回含みます。

`CaseNumber`, `CustomerName`, `Email`, `Phone`, `ProductName`, `SerialNumber`, `Subject`, `Description`, `PreferredVisitDate`, `Status`, `ResponseMessage`

`WorkOrder`は以下の全項目を毎回含みます。

`WorkOrderNumber`, `CaseId`, `CaseNumber`, `CustomerName`, `Address`, `Phone`, `ProductName`, `ProblemSummary`, `ScheduledAt`, `TechnicianName`, `Status`, `WorkResult`, `CompletedAt`
