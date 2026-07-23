# SyncCoordinator

<p align="right">
  <a href="./README.md">English</a> · <strong>日本語</strong>
</p>

SyncCoordinator（SynCo）は、複数の業務データベース間で変更を検知し、値変換、競合判定、再試行、ループ防止を行いながらデータを同期するセルフホスト型コーディネーターです。

製品の考え方や利用イメージは、[SyncCoordinator 製品ドキュメント](https://synco.clavisflow.net/)を参照してください。

- [Overview](https://synco.clavisflow.net/overview): 製品概要、主な機能、ユースケース
- [Architecture](https://synco.clavisflow.net/architecture): システム構成、コンポーネント、信頼性モデル
- [Workflow](https://synco.clavisflow.net/workflow): 変更検知から競合解決、同期完了までの流れ
- [Getting Started](https://synco.clavisflow.net/getting-started): デモ起動、初期設定、実際の業務DBへの接続

## このリポジトリに含まれるもの

- SQL Server、MySQL、PostgreSQLに対応するマッピング駆動のRDB Connector
- Triggerと`SyncChangeQueue`による変更検知。業務テーブルの全件ポーリングは行わない
- 前回値、受信値、同期先の現在値を比較する項目単位の3-way競合判定
- 更新・削除競合の履歴、手動解決、後続競合の再評価
- at-least-once配送、決定的な配送ID、同期先での冪等適用、同期ループ防止
- システム、DB接続、同期ルール、列マッピング、値変換を管理するBlazor Web UI
- Inbox、Checkpoint、運用イベント、監査履歴、Webhook通知、保持期間管理
- Customer Portal、CRM、Field Serviceを使った3システムの実行可能デモ

同期用の補助テーブルとTriggerは、管理画面でSQLを確認し、対象DBごとに反映・検証してからルールを有効化します。既存の業務アプリケーションや業務テーブル定義は変更しません。

## クイックスタート

デモ構成の起動には次が必要です。

- Windows 10で動作確認済み
- .NET SDK 10.0.301以降の互換SDK（`global.json`は`latestFeature`へロールフォワード）
- Aspire CLI 13.4.x
- Docker Desktop

```powershell
dotnet tool restore
dotnet restore SyncCoordinator.sln
dotnet build SyncCoordinator.sln --no-restore
dotnet test SyncCoordinator.sln --no-build
aspire run --apphost src/SyncCoordinator.AppHost/SyncCoordinator.AppHost.csproj
```

既定の`Demo`モードでは、Aspire Dashboardから次のリソースをまとめて起動します。

- SyncCoordinator Web / Worker / 管理DB
- Customer Portal / MySQL
- CRM / SQL Server
- Field Service / PostgreSQL

初回はSyncCoordinator Webの`/account/setup`で管理者パスワードを設定します。デモルートの有効化、競合シナリオ、確認手順は[デモ環境](demos/README.md)を参照してください。

通常の`dotnet test`では、DockerとChromiumを使うE2Eテストをスキップします。明示的な実行方法は[E2Eテスト](docs/e2e-testing.md)にあります。

## 実行モード

`src/SyncCoordinator.AppHost/appsettings.Development.json`の`RunMode`で構成を切り替えます。

| モード | 用途 | 起動内容 |
|---|---|---|
| `Demo` | 製品確認、デモ撮影、開発 | Web、Worker、管理DB、3つの業務アプリと業務DB。デモ設定と競合シナリオを投入する |
| `Core` | 実際の業務DBへ接続 | Web、Worker、外部またはコンテナの管理DB。システムとルールは管理画面から登録する |
| `E2E` | 自動E2Eテスト | 一時DBとデモアプリを動的ポートで起動し、テスト終了後に破棄する |

`Core`モードで外部SQL Serverを管理DBに使う場合、Docker Desktopは不要です。接続文字列、管理DBマイグレーション、本番運用前の確認事項は[Getting Started](https://synco.clavisflow.net/getting-started)と[技術仕様書](docs/technical-specification.md)を参照してください。

## ソリューション構成

| パス | 責務 |
|---|---|
| `src/SyncCoordinator.Contracts` | 共通payload、変更キュー、適用要求の契約 |
| `src/SyncCoordinator.Core` | 同期判断、競合解決、管理サービスのユースケースと抽象化 |
| `src/SyncCoordinator.Infrastructure` | 管理DB、RDB Connector、DB配備、通知、認証関連の実装 |
| `src/SyncCoordinator.Worker` | Queueの読取り、配送、再試行、競合解決要求の処理 |
| `src/SyncCoordinator.Web` | Blazor Interactive Serverによる管理画面 |
| `src/SyncCoordinator.AppHost` | `Demo`、`Core`、`E2E`のAspire構成 |
| `src/SyncCoordinator.ServiceDefaults` | OpenTelemetry、Service Discovery、Resilience、Health Check |
| `tests/SyncCoordinator.Tests` | CoreとInfrastructureを中心としたユニット／統合テスト |
| `tests/SyncCoordinator.E2ETests` | 実DB、Worker、管理画面を含むE2Eテスト |
| `demos` | 3つの業務アプリ、初期データ、撮影・リセット用ツール |

## ドキュメントの役割

| ドキュメント | 内容 |
|---|---|
| [製品ドキュメント](https://synco.clavisflow.net/) | 製品概要、アーキテクチャ、処理フロー、導入手順 |
| [操作マニュアル兼ヘルプ](docs/user-guide.md) | 管理画面の操作手順。アプリ内`/help`の正本 |
| [技術仕様書](docs/technical-specification.md) | 同期処理、状態遷移、永続化、セキュリティ、配備上の制約 |
| [Windows Server導入手順](docs/windows-server-deployment.md) | 管理DB、IISのlocalhost限定Web、共有鍵、Workerを使うテスト環境の構築手順 |
| [関連テーブル同期ガイド](docs/related-entity-sync.md) | JOIN項目、対象条件、親変更の1対多展開、項目方向、SQL Serverでの制約 |
| [Webhook連携ガイド](docs/webhooks.md) | イベント、payload、署名検証、再試行、受信側の契約 |
| [デモ環境](demos/README.md) | 3システム構成、デモシード、確認・リセット手順 |
| [E2Eテスト](docs/e2e-testing.md) | E2Eの前提、実行方法、失敗時の調査方法 |
| [設計判断](docs/decisions) | 主要な設計判断と採用理由を記録したADR |

## 実装上の境界

- Coordinator管理DBと業務DBをまたぐ分散トランザクションは使用しません。
- 同期先では業務更新と`SyncAppliedMessage`を同じDBローカルトランザクションで保存します。
- Workerは同じレコードの通知をまとめ、処理時点の最新状態へ収束させます。
- DB接続情報はASP.NET Core Data Protectionで暗号化し、管理DBへ保存します。
- 管理画面のDB直接反映は`DatabaseDeployment:AllowDirectApply=true`を明示した環境だけで使用できます。
- 本番環境ではHTTPS、共有Key Ring、最小権限のDBアカウント、承認済みマイグレーション手順が必要です。

詳細は[Architecture](https://synco.clavisflow.net/architecture)、[Workflow](https://synco.clavisflow.net/workflow)、[技術仕様書](docs/technical-specification.md)を参照してください。

## ライセンス

SyncCoordinatorは[Apache License 2.0](LICENSE)で提供します。Copyright 2026 ClavisFlow.

配布物に含まれる第三者コンポーネントには、それぞれのライセンスが適用されます。著作権表示とライセンス条件は[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)および[licenses](licenses)を参照してください。特にWindows向けSQL Server接続で同梱されるMicrosoft.Data.SqlClient.SNIは、MITではなくMicrosoft独自の再配布条件で提供されます。バイナリの利用・再配布条件は[BINARY-DISTRIBUTION-NOTICE.md](BINARY-DISTRIBUTION-NOTICE.md)も確認してください。
