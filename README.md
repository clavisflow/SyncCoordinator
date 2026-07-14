# SyncCoordinator

A（SQL Server）または B（MySQL）で発生した作業依頼を C（SQL Server）と同期する独立ソリューションです。A⇄CとB⇄Cは別々の同期ルールとして管理し、AとBは直接同期しません。システムと同期ルールは追加可能な構成です。

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
| `SyncCoordinator.Core` | 同期ルール処理、3-way 競合判定、Inbox/Checkpoint 抽象化 |
| `SyncCoordinator.Infrastructure` | 管理DB EF Core、SQL Server/MySQL/PostgreSQL Dapper Connector、DI |
| `SyncCoordinator.Worker` | 差分キューのポーリングと同期ユースケース実行、Windows Service host |
| `SyncCoordinator.Web` | Blazor Interactive Server の管理画面。同期ロジックは持たず、Coreの管理サービスを使用する |
| `SyncCoordinator.AppHost` | SQL Server 管理DB、Worker、Web の Aspire 開発構成 |
| `SyncCoordinator.ServiceDefaults` | OpenTelemetry、service discovery、resilience、Health Check |
| `SyncCoordinator.Tests` | 競合、配送ID、ループ防止の unit test |

## 同期フロー

1. 各業務DBの Trigger が同じDBの `SyncChangeQueue` に `MessageId` と変更IDを追加する。別DBや Coordinator は呼ばない。
2. Worker が `QueueId > Checkpoint` を batch 取得し、同じレコードの通知をまとめる。業務テーブルの全件ポーリングはしない。
3. Source Connector が最新通知と業務行の現在値から最新状態を共通 `EntityPayload` に変換し、管理DBの有効な同期ルールを解決する。
4. Inbox を取得し、送信元・送信先それぞれの前回観測値と現在値を項目単位で比較する。
5. 非競合項目を自動マージし、競合項目には route/field policy を適用する。
6. Destination Connector が業務更新と `SyncAppliedMessage` を単一DBローカルトランザクションで保存する。
7. Coordinator 管理DBへ両側の観測 snapshot、競合前後と採用値、Inbox 状態を保存する。
8. batch内の全同期ルールの処理完了後にだけ Checkpoint をbatch末尾まで進める。途中失敗時は進めない。

管理DBと業務DBの分散トランザクションは使用しません。障害時は同じ決定的 `DeliveryMessageId` で再実行され、宛先 `SyncAppliedMessage` によって二重適用を防ぐ at-least-once 処理です。Coordinator 適用時の MessageId は SQL Server `SESSION_CONTEXT`、MySQL connection user variable、PostgreSQLのtransaction-local設定を経由してTriggerのキューへ渡り、Workerが適用済みメッセージを読み飛ばして同期ループを止めます。

## 起動

既定では、Coordinator管理DBにローカルSQL Server（`localhost:1433`）を使用します。Docker Desktopは不要です。Windows認証で接続できるSQL Serverを先に起動してください。

```powershell
dotnet tool restore
dotnet restore SyncCoordinator.sln
dotnet build SyncCoordinator.sln --no-restore
dotnet test SyncCoordinator.sln --no-build
aspire run --apphost src/SyncCoordinator.AppHost/SyncCoordinator.AppHost.csproj
```

AppHost は `ConnectionStrings:coordinator-db` をWorkerとWebへ注入します。既定値は `localhost:1433` の `SyncCoordinator` データベースです。DevelopmentではWebだけがEF Core migrationとサンプル同期ルールのseedを自動適用し、WebとWorkerによるDB作成競合を避けます。本番の `CoordinatorDatabase:ApplyMigrations` は既定で `false` です。

SQL Server認証を使用する場合は、パスワードを設定ファイルへ書かず、AppHostのUser Secretsへ登録します。

```powershell
dotnet user-secrets set --project src/SyncCoordinator.AppHost `
  "ConnectionStrings:coordinator-db" `
  "Server=localhost,1433;Database=SyncCoordinator;User ID=your-user;Password=your-password;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
```

Docker版SQL Serverを使う場合だけ、`src/SyncCoordinator.AppHost/appsettings.Development.json` の設定を切り替えてDocker Desktopを起動します。このモードでは接続文字列と起動待機をAspireが管理します。

```json
{
  "CoordinatorDatabase": {
    "UseContainer": true
  }
}
```

## 管理画面

Blazor Interactive Serverで次の画面を提供します。

UIコンポーネントはMITライセンスの`Radzen.Blazor` 11.1.0を使用し、無償の`Software`テーマを基礎にしています。管理画面は紙の台帳と配線図をモチーフに独自CSSで調整し、SIL Open Font License 1.1の`Zen Kaku Gothic New`をローカル配信します。Radzen Blazor Studio、Premiumテーマ、有償サポートには依存しません。第三者ライセンスは`THIRD-PARTY-NOTICES.md`およびフォント同梱の`OFL.txt`に記録しています。

| URL | 機能 |
|---|---|
| `/` | 同期概要、件数、最近の同期ルールと競合 |
| `/systems` | システムの登録・編集・有効化、一時停止／再開、暗号化したDB接続情報の保存、接続テスト |
| `/routes` | 同期ルール一覧、新規作成、編集 |
| `/routes/{id}` | 送信元／送信先、片方向／双方向、競合の判定単位、既定の競合ポリシー |
| `/routes/{id}/database-setup` | 業務DBへ作成する同期用テーブル／TriggerのSQL確認、ダウンロード、反映確認、有効化 |
| `/mappings` | ルール選択時に実DBのテーブル／列を取得し、列、キー、列別競合ポリシー、方向別固定値を設定 |
| `/conflicts` | 競合履歴一覧 |
| `/conflicts/{id}` | base、incoming、current、adopted、適用ポリシーの比較 |
| `/operations` | Queue Checkpoint、Inbox、エラー、設定変更監査 |

設定更新は`ICoordinatorAdminService`経由で行い、Blazorコンポーネントには同期判断を実装しません。A⇔Bの同期ルール、同一システム間ルール、重複項目ポリシーはCoreで拒否します。設定保存時は`ConfigurationAudit`へ変更履歴を追加します。

同期ルールは必ず固定の送信元と送信先を持ちます。片方向は送信元から送信先だけ、双方向は送信先で更新された対象データも最初の送信元へ同期します。双方向でも送信先で独自に作成されたデータは対象外です。Cで独自作成したデータを同期したい場合は、Cを送信元にした別の片方向ルールを作成します。条件による送信先の切り替えは実装していません。

画面では内部識別子（コード上の`EntityType`）や発生元追跡値（`OriginSystem`）を入力させません。内部識別子はルール作成時に自動生成し、発生元追跡値はConnectorが保持します。

DB接続文字列はASP.NET Core Data Protectionで暗号化してCoordinator管理DBへ保存します。パスワードは画面へ再表示せず、監査履歴にも保存しません。本番でWebとWorkerを別サービスアカウントにする場合は、両者が読み書きできる保護済みフォルダーを`DataProtection:KeyRingPath`に設定し、鍵のアクセス制御とバックアップを行ってください。暗号鍵を失うと保存済み接続情報は復号できません。

テーブル／列一覧の取得には対象DBの`INFORMATION_SCHEMA`を使用します。同期ルールの選択時にテーブル一覧を、両側のテーブル選択時に列一覧を自動取得します。業務テーブル自体は作成・変更しません。列別競合ポリシーは列マッピングと同時に保存し、未指定列にはルール既定を使用します。双方向ルールでは同じマッピングとポリシーを逆向きにも使用する前提です。

`UpdatedUserId`など同期元の値を使わない列は、通常の列マッピングから外して「書き込み時の固定値」へ設定できます。固定値は片方向ルールでは送信先だけ、双方向ルールでは送信先へ書く値と送信元へ戻す値を別々に保持します。通常マッピングと固定値で同じ書き込み先列を重複指定することはできません。値は文字列として最大4000文字まで保存し、実際のDB型への変換と検証は実業務Connectorが担当します。

削除同期はテーブルマッピングで有効化し、同期元・同期先ごとに`物理削除`または`論理削除`を選びます。物理削除側ではTriggerが削除前payloadとOriginSystemを`SyncDeleteTombstone`へ保存してから`Delete`をキューへ追加します。論理削除側では削除列と削除値（例：`IsDeleted = 1`）を設定し、その値への遷移を`Delete`として検知します。したがって、Aは物理削除、Cは論理削除という組み合わせも可能です。

保存したマッピングは実業務Connectorを実装するときの設定境界であり、現在の`SampleJsonRelationalConnector`は従来どおりサンプルテーブル専用です。

### 業務DBの準備とルール有効化

ルールとマッピングの保存では業務DBへDDLを実行しません。ルールは下書きになり、`/routes/{id}/database-setup`で送信元・送信先ごとの実行SQLと変更対象を確認します。SQLはダウンロードしてDBAが実行できます。作成対象は`SyncChangeQueue`、`SyncAppliedMessage`、`SyncEntityOrigin`、`SyncDeleteTombstone`、反映版を検証する`SyncCoordinatorDeployment`と、必要な変更検知Triggerです。既存の業務テーブルは作成・変更しません。

管理画面からの直接反映は、次の設定を明示した環境だけで使用できます。既定は`false`です。本番では認証に加えて、接続先DBの権限分離と監査手順を整備してから有効にしてください。

```json
{
  "DatabaseDeployment": {
    "AllowDirectApply": true
  }
}
```

直接反映には、SQL確認のチェックと対象DB名の再入力が必要です。DBAがSQLを実行した場合も「すべてのDBを確認」でオブジェクトの存在と定義ハッシュを検証します。両DBの確認完了後だけルールを有効化できます。DDLに影響するキー列変更は下書きへ戻し、マッピング保存時はルールを自動的に無効化します。無効化・削除時にTriggerは自動削除しません。有効なルールで使用中のシステムは無効化できず、先に関連ルールを無効化する必要があります。

システムの一時停止は構成上の無効化とは別です。一時停止中はWorkerがそのシステムを含むルールを処理せず、該当する送信元のCheckpointも進めません。再開後は蓄積した通知を順番に巻き戻すのではなく、各レコードの最新状態へ収束します。現在のCheckpointは送信元システム単位で共有するため、停止対象と同じ送信元を使う別ルールも再開まで待機します。停止操作より前に開始済みの配送は完了する場合があります。

競合詳細と失敗Inboxは現時点では参照専用です。手動再適用は実業務Connector、権限、監査要件が決まってから追加します。

## 業務DB Connector の差し替え

`SampleJsonRelationalConnector` は構造未確定期間だけ使う executable sample です。実テーブル確定後はシステムごとに `ISyncConnector` を実装します。

- `ReadChangesAsync`: `SyncChangeQueue` を Checkpoint の次から読む。
- `WasAppliedMessageAsync`: `SyncAppliedMessage` で Coordinator 自身の更新を判定する。
- `ReadLatestMessageAsync`: 同一レコードの最新通知を確認し、現在の業務行または最新DeleteのTombstoneを共通payloadへ変換する。中間の更新通知は適用しない。
- `ReadCurrentAsync`: 競合比較に使う宛先現在値を同じ共通項目名へ変換する。
- `ApplyAsync`: `SyncAppliedMessage`、業務更新、`SyncEntityOrigin`を同じローカルトランザクションで冪等適用する。Deleteでは`DeletionBehavior`に従い、物理DELETEまたは指定列への論理削除値設定を行う。

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

MySQL のサンプルでは connection string に `GuidFormat=Char36;AllowUserVariables=True` を設定します。管理画面で保存するMySQL接続には自動設定されます。

PostgreSQLは13以降を対象とし、UUID、`jsonb`、PL/pgSQL Triggerを使用します。管理画面で暗号化を有効にした場合、証明書を信頼しない設定では`SSL Mode=VerifyFull`、信頼する設定では`SSL Mode=Require`を使用します。

`EntityPayload.Fields` は Connector が管理する同期対象項目を毎回同じ集合で返す契約です。項目省略を削除として扱うため、部分 payload は渡しません。`MergeAndNotify` で業務固有マージが必要なら `IConflictValueMerger` を差し替えます。既定実装は値を推測せず保留します。

## SQL テンプレート

- SQL Server: `database/sqlserver/001_sync_support_sample.sql`、`database/sqlserver/trigger_template.sql`
- MySQL: `database/mysql/001_sync_support_sample.sql`、`database/mysql/trigger_template.sql`
- PostgreSQL: `database/postgresql/001_sync_support_sample.sql`、`database/postgresql/trigger_template.sql`

Trigger template は置換トークンを含むため、そのまま実行しません。実際のマッピングから生成されたSQLは「業務DB反映準備」で確認・ダウンロードします。削除方式を変更すると定義ハッシュが変わり、再反映と検証が必要になります。

キューと適用済みメッセージには保持期間を定め、すべての稼働 Worker の Checkpoint より古い `SyncChangeQueue`、最大再送期間を過ぎた `SyncAppliedMessage`、対応する処理が完了して再送期間を過ぎた`SyncDeleteTombstone`を別ジョブで削除してください。

## 管理DB migration

初期 migration は `SyncCoordinator.Infrastructure/Persistence/Migrations` に含まれます。

管理DBスキーマの初期マイグレーションは未運用期間中に再作成しています。旧マイグレーションを適用済みの開発用`SyncCoordinator`データベースは互換移行の対象外なので、一度削除してから再作成してください。業務DBは削除対象ではありません。

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
- `docs/decisions/0007-sync-rules-and-direction.md`
- `docs/decisions/0008-business-database-deployment.md`
- `docs/decisions/0009-delete-synchronization.md`
- `docs/decisions/0010-delayed-queue-convergence.md`
- `docs/decisions/0011-system-operational-pause.md`
- `docs/decisions/0012-local-administrator-authentication.md`
- `docs/decisions/0013-community-pro-and-webhooks.md`
- `docs/decisions/0014-community-webhook-delivery.md`

## TODO

現時点の意図的な未実装は、保存したmappingを使用する実業務ConnectorとPro版の追加機能です。Pro候補はTeams／メール通知、失敗データの手動再実行、管理画面からの競合解決、比較・修復、監査エクスポートです。Community版の汎用Webhookは、Outbox、HMAC-SHA256署名、自動再試行、配送履歴を含めて実装しています。

## 管理画面の認証と復旧

管理者は固定ユーザー名`admin`の1アカウントです。ユーザー登録、ロール分け、外部IdP、MFA、リカバリーコードは設けません。ASP.NET CoreのCookie認証と`PasswordHasher`を使用し、管理DBの`AdminAccount`にはパスワードハッシュだけを保存します。ログインは接続元IPごとに1分間5回までです。

初回起動後、サーバー自身のブラウザーで`http://localhost:<Webのポート>/account/setup`を開き、12文字以上の管理者パスワードを登録します。初期設定画面と忘れた場合の`/account/reset`は、接続元とアクセス先Hostの両方がlocalhostまたはループバックIPの場合だけ使用できます。復旧コードや環境変数のパスワードは使用しません。

ログイン後はヘッダーの「パスワード変更」から通常変更できます。再設定・変更時はCookieの世代番号を更新し、以前のログインCookieを無効にします。同期設定、履歴、暗号化したDB接続情報は変更しません。初期設定、再設定、通常変更は`ConfigurationAudit`へ記録しますが、パスワードとハッシュは監査履歴へ書きません。

認証Cookieを安全に送るため、本番ではHTTPSを使用してください。localhost上のリバースプロキシで公開する場合も、復旧URLはLAN側ホスト名では使用できません。プロキシから`Host: localhost`へ書き換えて外部公開しないでください。

## 表示言語

ヘッダーとログイン画面の言語選択から日本語または英語へ変更できます。選択はASP.NET CoreのカルチャーCookieへ1年間保存され、画面を再読み込みして反映されます。共通ナビゲーション、ダッシュボード、認証・アカウント画面、詳細設定画面、Webhook画面は日英リソースを提供します。
