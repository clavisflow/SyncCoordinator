# E2Eテスト

E2Eテストは、デモと同じ`SyncCoordinator.AppHost`のリソース構成を使います。Web、Worker、SQL Server、MySQL、PostgreSQL、各デモアプリを別のテスト専用構成ファイルへ複製しません。

実行時の状態だけは分離します。デモは固定ポートとDocker volumeでデータを維持し、E2EはAspireが割り当てる動的ポートと一時コンテナを使います。これにより、デモ環境を起動したままでもE2Eがデモデータを壊しません。

| 項目 | Demo | E2E |
|---|---|---|
| AppHostの構成定義 | 共通 | 共通 |
| DBポート | 固定 | 動的 |
| DBデータ | Docker volumeへ永続化 | テスト終了時に破棄 |
| Webのデモ設定投入 | 有効 | 有効 |
| Workerの競合デモ自動投入 | 有効 | 無効 |
| Data Protection鍵 | 通常の実行設定 | テストごとの一時共有フォルダー |
| 管理画面 | 通常のブラウザーで操作 | Playwright Chromiumで自動操作 |

## 通常のテスト

通常の`dotnet test`ではE2Eはスキップされ、Docker Desktop、AppHost、Chromiumを起動しません。

```powershell
dotnet test SyncCoordinator.sln
```

## 初回のブラウザー準備

NuGetパッケージの復元だけではPlaywright用Chromiumは導入されません。初回と`Microsoft.Playwright`のバージョン更新後に、E2Eプロジェクトをビルドして対応するChromiumを導入します。通常のビルドから暗黙にダウンロードはしません。

```powershell
dotnet build tests/SyncCoordinator.E2ETests/SyncCoordinator.E2ETests.csproj
& tests/SyncCoordinator.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium
```

## E2Eの実行

E2EはDocker Desktopを起動した状態で、明示的にフラグを付けて実行します。別途AppHostやデモを起動する必要はありません。Aspire Testingがテスト専用AppHostとコンテナを起動し、終了時に停止・破棄します。

```powershell
$env:SYNC_COORDINATOR_RUN_E2E = "true"
dotnet test tests/SyncCoordinator.E2ETests/SyncCoordinator.E2ETests.csproj
Remove-Item Env:SYNC_COORDINATOR_RUN_E2E
```

Playwrightの管理画面シナリオが失敗した場合は、調査用traceと全画面スクリーンショットを`%TEMP%\SyncCoordinator.E2E\artifacts\<実行ID>`へ保存します。`SYNC_COORDINATOR_E2E_ARTIFACTS`で保存先のルートを変更できます。traceはPlaywright Trace Viewerで開けます。成功時は診断artifactを残しません。画面状態が含まれるため、共有前に内容を確認してください。

現在のシナリオは実DBと実Workerを使用し、管理画面シナリオは実Web endpointで確認します。

1. 空のCoordinator管理DBへデモ設定を投入する。
2. `Customer Portal - CRM`と`CRM - Field Service`ルートの同期用DDLを3つのDBへ反映・検証し、ルートを有効化する。
3. MySQLの`SupportCase`へ一意な問い合わせを追加する。
4. Workerが変更キューを処理し、SQL Serverの`dbo.SupportCase`へ列マッピング後の値が反映されるまで待つ。
5. SQL Server側で受付状態と回答を更新する。
6. 後続のWorker処理でMySQLへ更新が戻ることを確認する。
7. SQL Serverへ作業指示を追加し、`InProgress`が`in_progress`へ変換されてPostgreSQLへ届き、対応するInboxが初回試行で`Completed`となることを確認する。
8. PostgreSQL側で作業を`done`へ更新し、`Completed`へ逆変換されてSQL Serverへ戻ることを確認する。
9. PostgreSQL側で作業指示を物理削除し、削除tombstoneを経由してSQL Serverからも削除されることを確認する。
10. 管理サービスでField Serviceの接続先だけを一時的に到達不能へ切り替え、Workerが接続障害を検知して対象システムコードとNpgsqlエラーをシステムイベントへ記録し、Inboxを`Failed`にすることを確認する。
11. Field ServiceのQueue読取り障害が継続している間にも、独立した送信元であるPortalの変更がCRMへ届くことを確認する。
12. 正しい暗号化接続設定へ戻し、Workerを再起動せず同じInboxの`AttemptCount`が増え、`LastError`が消去されて`Completed`となり、PostgreSQLへ一度だけ反映されることを確認する。
13. テスト専用Workerを停止し、停止中にPortalへ変更を追加してもInboxとCheckpointが進まないことを確認する。Worker再開後は同じ通知が処理され、Checkpointが前進してCRMへ一度だけ反映されることを確認する。
14. WorkerをInbox取得後の処理中に停止し、対象Inboxが`Processing`、送信元Checkpointが未更新のまま残ることを確認する。5分リースの失効後を再現してWorkerを再開し、同じInboxが`AttemptCount=2`で再取得され、`Completed`となってCRMへ一度だけ反映されることを確認する。
15. PortalとCRMで同じ問い合わせ項目を別値に更新し、Inbox保留と競合履歴が作成されることを確認する。
16. 受信値を採用する手動解決を登録し、Worker処理後にInboxが`Completed`となり、CRMがPortalの値へ収束することを確認する。
17. テスト内の一時HTTP受信口で`sync.upserted`を受け、payload v1、主要メタデータ、HMAC-SHA256署名、配送履歴の`Delivered`を確認する。
18. 別の受信口で初回だけHTTP 500を返し、同一イベントが1分後に新しいtimestamp・署名で再送され、2回目に`Delivered`になることを確認する。
19. Playwright Chromiumで未認証リダイレクト、初期管理者設定フォームの送信、Cookieを分離した再ログイン、認証済みダッシュボードと処理状況画面の描画、JavaScript／Blazorエラーがないことを確認する。

同期結果は固定時間の待機ではなく、最大90秒まで500ミリ秒間隔で確認します。Webの起動待ちは最大120秒、Playwrightの操作待ちは既定30秒です。Webhook再送間隔を実時間で確認するため通常は3～4分かかり、cold環境でのDockerイメージ作成も考慮してテスト全体は10分でタイムアウトします。初回は2回目以降より時間がかかります。

障害回復シナリオではDockerコンテナを停止しません。テスト専用管理DBに保存されたField Serviceの接続ポートだけを閉鎖ポートへ一時変更し、`finally`で元の接続設定へ戻します。デモ環境やユーザーが起動したプロセス・コンテナは操作しません。

Worker再起動シナリオでは、Aspire Testingがこのテスト内で起動した`coordinator-worker`だけを停止・再開します。Visual Studio、デモ環境、ユーザーが起動したWorkerは操作しません。

停止完了後にQueueへ変更を追加するシナリオでは、Inboxリース失効待ちは発生しません。処理途中停止シナリオではSQL Serverのテスト専用行ロックでInbox取得後の停止点を作り、停止後も`Processing`と未来の`LockedUntilUtc`が変わらず残ることを確認します。実時間で5分待つ代わりに、一時管理DBの対象Inboxだけの`LockedUntilUtc`を過去へ更新してリース失効条件を成立させます。これは失効後の再取得動作を検証するための時間短縮であり、実運用の5分リース設定は変更しません。

E2E用Data Protection鍵は`%TEMP%\SyncCoordinator.E2E\<実行ID>`のテスト専用フォルダーへ作成し、テスト用AppHostの破棄後に削除します。Windowsで一時的なファイルハンドルが残る場合は短時間再試行し、最終的に削除できなくてもテスト本体の結果は上書きしません。残ったフォルダーは後から安全に削除できます。

## AppHostの実行モード

- `RunMode=Demo`: 既定。固定ポート、永続volume、外部公開endpointを使用する。
- `RunMode=E2E`: Aspire Testing専用。動的ポート、一時DB、競合デモ投入なしで同じ構成を使用する。
- `RunMode=Core`: Coordinator WebとWorkerだけを使用する。

従来の`Demo:Enabled=false`も`RunMode`未指定時は`Core`として扱います。
