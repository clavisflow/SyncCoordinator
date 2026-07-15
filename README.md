# SyncCoordinator

複数システム間の変更配送、競合解決、再試行、ループ防止を管理する同期コーディネーターです。システムと同期ルールは管理画面から追加でき、特定のシステムコードや接続構成を前提にしません。

Customer Portal（MySQL）、CRM（SQL Server）、Field Service（PostgreSQL）を使った実行可能デモは [`demos/README.md`](demos/README.md) を参照してください。

業務テーブル、キー、同期列は管理DBのテーブル／列マッピングで定義します。RDB Connectorは検証済みかつ有効なマッピングを読み、SQL Server、MySQL、PostgreSQLの通常の業務列を共通`EntityPayload`へ変換して読み書きします。

デモと通常運用で、業務テーブルの読み書き、配送、競合解決、再試行、ループ防止、DBセットアップ、管理DBの読み書きに分岐はありません。デモ固有なのはAppHostによるリソース構成と、空の管理DBへのシステム、接続情報、ルール、マッピングの初期投入です。既存構成の置換、特定システムコードだけに適用されるルール、初期Trigger配備はありません。

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
| `SyncCoordinator.AppHost` | 3つの業務DB・業務アプリ、Worker、WebをまとめるAspire構成。設定でCoordinator単体構成にも切替可能 |
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

既定では、Customer Portal、CRM、Field Service、3つの業務DB、Coordinator Web、Workerを1つのAspire Dashboardにまとめたデモ構成を起動します。起動前にDocker Desktopが必要です。

```powershell
dotnet tool restore
dotnet restore SyncCoordinator.sln
dotnet build SyncCoordinator.sln --no-restore
dotnet test SyncCoordinator.sln --no-build
aspire run --apphost src/SyncCoordinator.AppHost/SyncCoordinator.AppHost.csproj
```

AppHostはデモ用接続文字列と同期対象3システムの設定を各リソースへ注入します。構成とデモ手順は[`demos/README.md`](demos/README.md)を参照してください。

Coordinator WebとWorkerだけを起動する場合は、`Demo:Enabled`を`false`にします。この場合は従来どおり`ConnectionStrings:coordinator-db`を使用します。

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
| `/mappings` | 実DBの列型・長さを取得し、列、キー、競合ポリシー、方向別の値変換、固定値、変換プレビューを設定 |
| `/conflicts` | 競合履歴一覧 |
| `/conflicts/{id}` | base、incoming、current、adopted、適用ポリシーの比較 |
| `/operations` | Queue Checkpoint、Inbox、エラー、設定変更監査 |

設定更新は`ICoordinatorAdminService`経由で行い、Blazorコンポーネントには同期判断を実装しません。同一システム間ルールと重複項目ポリシーはCoreで拒否します。設定保存時は`ConfigurationAudit`へ変更履歴を追加します。

同期ルールは必ず固定の送信元と送信先を持ちます。片方向は送信元から送信先だけ、双方向は送信先で更新された対象データも最初の送信元へ同期します。双方向でも送信先で独自に作成されたデータは対象外です。Cで独自作成したデータを同期したい場合は、Cを送信元にした別の片方向ルールを作成します。条件による送信先の切り替えは実装していません。

画面では内部識別子（コード上の`EntityType`）や発生元追跡値（`OriginSystem`）を入力させません。内部識別子はルール作成時に自動生成し、発生元追跡値はConnectorが保持します。

DB接続文字列はASP.NET Core Data Protectionで暗号化してCoordinator管理DBへ保存します。パスワードは画面へ再表示せず、監査履歴にも保存しません。本番でWebとWorkerを別サービスアカウントにする場合は、両者が読み書きできる保護済みフォルダーを`DataProtection:KeyRingPath`に設定し、鍵のアクセス制御とバックアップを行ってください。暗号鍵を失うと保存済み接続情報は復号できません。

接続テストでは通常の接続に加え、`日本語😀`をパラメーターで往復させてUnicodeを保持できるか確認します。不一致や確認エラーは警告として表示しますが、DB設定の自動変更や既存データの文字化け推測・修復は行いません。ダウンロード用SQLはMySQLで`SET NAMES utf8mb4`、PostgreSQLでUTF-8のclient encodingを明示し、SQL Serverの同期補助文字列列には`nvarchar`を使用します。

テーブル／列一覧の取得には対象DBの`INFORMATION_SCHEMA`を使用します。列名に加えてNULL可否、最大長、数値precision／scaleを保存し、書込み直前にも契約を検証します。業務テーブル自体は作成・変更しません。列別競合ポリシーは列マッピングと同時に保存し、未指定列にはルール既定を使用します。

値変換は「同期元の共通値→同期先」と「同期先→共通値」を列ごとに別設定します。コード対応表、NULL時の既定値、UTC正規化、明示的な文字切詰めと小数丸めを使用でき、管理画面で実値をプレビューできます。既定では値を推測、切詰め、丸めせず、桁超過、型不一致、未定義コードなどは非一時エラーとしてInboxを`Held`にします。Checkpointは進めて他データを止めず、原因はInbox、失敗Webhook、運用イベントへ記録します。キー列の変換はEntityIdの一貫性を守るため許可しません。

`UpdatedUserId`など同期元の値を使わない列は、通常の列マッピングから外して「書き込み時の固定値」へ設定できます。固定値は片方向ルールでは送信先だけ、双方向ルールでは送信先へ書く値と送信元へ戻す値を別々に保持します。通常マッピングと固定値で同じ書き込み先列を重複指定することはできません。値は文字列として最大4000文字まで保存し、実際のDB型への変換と検証は実業務Connectorが担当します。

削除同期はテーブルマッピングで有効化し、同期元・同期先ごとに`物理削除`または`論理削除`を選びます。物理削除側ではTriggerが削除前payloadとOriginSystemを`SyncDeleteTombstone`へ保存してから`Delete`をキューへ追加します。論理削除側では削除列と削除値（例：`IsDeleted = 1`）を設定し、その値への遷移を`Delete`として検知します。したがって、Aは物理削除、Cは論理削除という組み合わせも可能です。

保存したマッピングはRDB Connectorが実行時に参照します。同じシステム・EntityTypeに複数の有効なルールがある場合、物理テーブルと列の契約が一致しなければ曖昧な書き込みを避けるため処理を停止します。

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

直接反映には、SQL確認のチェックと対象DB名の再入力が必要です。DBAがSQLを実行した場合も「すべてのDBを確認」でオブジェクトの存在と定義ハッシュを検証します。両DBの確認完了後だけルールを有効化できます。既存マッピングの保存時はルールをマッピング保守状態へ移し、新規Inbox取得を止めて処理中配送の完了を待ってから定義を切り替えます。物理列、キー、削除設定の変更は下書きへ戻し、旧列契約のSnapshotを破棄します。保守中の通知ではCheckpointを進めず、DB反映・検証後にルールを有効化すると保守状態を解除して追いつき処理を再開します。無効化・削除時にTriggerは自動削除しません。有効なルールで使用中のシステムは無効化できず、先に関連ルールを無効化する必要があります。

システムの一時停止は構成上の無効化とは別です。一時停止中はWorkerがそのシステムを含むルールを処理せず、該当する送信元のCheckpointも進めません。再開後は蓄積した通知を順番に巻き戻すのではなく、各レコードの最新状態へ収束します。現在のCheckpointは送信元システム単位で共有するため、停止対象と同じ送信元を使う別ルールも再開まで待機します。停止操作より前に開始済みの配送は完了する場合があります。

競合詳細と失敗・保留Inboxは現時点では参照専用です。手動再適用は権限、監査要件が決まってから追加します。

## 業務DB Connector

標準のRDB Connectorは管理DBの有効なテーブル／列マッピングから業務SQLを組み立てます。画面でDB配備、検証、有効化が完了するまでは対象DBの同期補助テーブルを読みません。

- `ReadChangesAsync`: `SyncChangeQueue` を Checkpoint の次から読む。
- `WasAppliedMessageAsync`: `SyncAppliedMessage` で Coordinator 自身の更新を判定する。
- `ReadLatestMessageAsync`: 同一レコードの最新通知を確認し、現在の業務行または最新DeleteのTombstoneを共通payloadへ変換する。中間の更新通知は適用しない。
- `ReadCurrentAsync`: 競合比較に使う宛先現在値を同じ共通項目名へ変換する。
- `ApplyAsync`: `SyncAppliedMessage`、業務更新、`SyncEntityOrigin`を同じローカルトランザクションで冪等適用する。Deleteでは`DeletionBehavior`に従い、物理DELETEまたは指定列への論理削除値設定を行う。

RDB Connectorを有効にする場合はWorker設定へ次を追加し、対応するconnection stringを指定します。テーブル名と列名はこの設定ではなく、管理DBのマッピングから取得します。

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

MySQLではconnection stringに`GuidFormat=Char36;AllowUserVariables=True`を設定します。管理画面で保存するMySQL接続には自動設定されます。

PostgreSQLは13以降を対象とし、UUID、`jsonb`、PL/pgSQL Triggerを使用します。管理画面で暗号化を有効にした場合、証明書を信頼しない設定では`SSL Mode=VerifyFull`、信頼する設定では`SSL Mode=Require`を使用します。

`EntityPayload.Fields` は Connector が管理する同期対象項目を毎回同じ集合で返す契約です。項目省略を削除として扱うため、部分 payload は渡しません。`MergeAndNotify` で業務固有マージが必要なら `IConflictValueMerger` を差し替えます。既定実装は値を推測せず保留します。

## SQL テンプレート

- SQL Server: `database/sqlserver/trigger_template.sql`
- MySQL: `database/mysql/trigger_template.sql`
- PostgreSQL: `database/postgresql/trigger_template.sql`

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

現時点の意図的な未実装は、失敗・保留データの手動再実行、管理画面からの競合解決、比較・修復、監査エクスポートです。Pro候補にはTeams／メール通知も含みます。Community版のRDB Connector、汎用Webhook、方向別値変換、書込み前検証、運用イベント記録は実装済みです。

## 管理画面の認証と復旧

管理者は固定ユーザー名`admin`の1アカウントです。ユーザー登録、ロール分け、外部IdP、MFA、リカバリーコードは設けません。ASP.NET CoreのCookie認証と`PasswordHasher`を使用し、管理DBの`AdminAccount`にはパスワードハッシュだけを保存します。ログインは接続元IPごとに1分間5回までです。

初回起動後、サーバー自身のブラウザーで`http://localhost:<Webのポート>/account/setup`を開き、12文字以上の管理者パスワードを登録します。初期設定画面と忘れた場合の`/account/reset`は、接続元とアクセス先Hostの両方がlocalhostまたはループバックIPの場合だけ使用できます。復旧コードや環境変数のパスワードは使用しません。

ログイン後はヘッダーの「パスワード変更」から通常変更できます。再設定・変更時はCookieの世代番号を更新し、以前のログインCookieを無効にします。同期設定、履歴、暗号化したDB接続情報は変更しません。初期設定、再設定、通常変更は`ConfigurationAudit`へ記録しますが、パスワードとハッシュは監査履歴へ書きません。

認証Cookieを安全に送るため、本番ではHTTPSを使用してください。localhost上のリバースプロキシで公開する場合も、復旧URLはLAN側ホスト名では使用できません。プロキシから`Host: localhost`へ書き換えて外部公開しないでください。

## 表示言語

ヘッダーとログイン画面の言語選択から日本語または英語へ変更できます。選択はASP.NET CoreのカルチャーCookieへ1年間保存され、画面を再読み込みして反映されます。共通ナビゲーション、ダッシュボード、認証・アカウント画面、詳細設定画面、Webhook画面は日英リソースを提供します。
