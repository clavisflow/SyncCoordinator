# Customer Portal (Laravel 12)

MySQLを利用する、一般家庭向け製品サポート窓口のLaravel 12 + Bladeデモです。
## データ契約

通常列を持つ`SupportCase`業務テーブルを使用します。DDLやmigrationを自動実行する処理はありません。

- キー: `CaseNumber`
- 新規作成時の `OriginSystem`: `PORTAL`
- 文字コード: `utf8mb4`
- Payloadの11項目: `CaseNumber`, `CustomerName`, `Email`, `Phone`, `ProductName`, `SerialNumber`, `Subject`, `Description`, `PreferredVisitDate`, `Status`, `ResponseMessage`

お客様が相談内容を更新するときは、CRM側で設定された`Status`と`ResponseMessage`、保存済みの`OriginSystem`を保持します。

## 設定

`.env.example` を参考に、MySQL接続情報と `APP_KEY` を環境変数で渡します。
Aspireなどから単一の接続文字列を渡す場合は、PDOが解釈できるMySQL URIを `DATABASE_URL` に設定できます。

```text
mysql://user:password@mysql:3306/DemoCustomerPortal?charset=utf8mb4
```

MySQLの業務テーブルはデモAppHostの初期化処理で作成します。同期補助テーブルとTriggerはCoordinator管理画面から配備します。

## Dockerイメージ

ホストにPHPやComposerがない環境でも、`Dockerfile` のComposerビルドステージで依存関係を取得できます。`composer.lock` は現時点では同梱していないため、Docker build時に `composer.json` のLaravel 12互換範囲から依存バージョンが解決されます。

実行時には、少なくとも次の環境変数が必要です。

```text
APP_KEY=base64:...
APP_URL=http://localhost:8080
DATABASE_URL=mysql://user:password@mysql:3306/DemoCustomerPortal?charset=utf8mb4
```

このプロジェクト自身はMySQL、Dockerコンテナ、SyncCoordinator Workerを起動または停止しません。

## 画面

- `/`: 新規相談への導線、よくある困りごと、相談履歴
- `/support-cases/create`: 新規相談
- `/support-cases/{id}`: 相談詳細とCRMからの回答
- `/support-cases/{id}/edit`: お客様入力項目の編集

ホームの人物イラストは、このデモ専用に生成したオリジナル画像です。画像内に文字、ロゴ、UI、透かしは含めていません。
