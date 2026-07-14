# ADR 0014: Community Webhook配送

- 状態: Accepted
- 日付: 2026-07-14

## 決定

Community版は複数のWebhook通知先を登録でき、通知先ごとにイベントを選択できる。初期イベントは`sync.upserted`、`sync.deleted`、`conflict.detected`、`sync.failed`、`system.paused`、`system.resumed`、`webhook.test`とする。

payloadは`schemaVersion: 1`と同期メタデータに限定し、業務payload、DB接続情報、例外詳細を含めない。配送はCoordinator管理DBのOutboxを介したat-least-onceとし、HTTP 2xxを成功とする。タイムアウトは10秒、リダイレクトには追従せず、失敗時は1分、5分、30分、2時間、6時間、12時間の間隔で再試行する。Webhook障害によって同期処理を停止しない。受信側は`eventId`で重複排除し、イベント順序に依存しない。

HMAC-SHA256署名は新規通知先で既定有効とするが、社内LANの簡素な受信先向けに無効化を許可する。署名秘密鍵は自動生成して暗号化保存し、タイムスタンプと未加工のrequest bodyを署名する。署名なし、およびHTTP利用時は管理画面に警告を表示する。

成功配送履歴は30日、失敗履歴は90日、未配送は成功または明示的な破棄まで保持する。

## 理由

汎用WebhookをCommunityへ含めながら、受信先障害と同期処理を分離し、小規模な閉域環境でも導入できる安全な既定値を提供するため。
