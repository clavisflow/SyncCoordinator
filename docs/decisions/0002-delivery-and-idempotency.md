# ADR 0002: at-least-once 配送とループ防止

- 状態: Accepted
- 日付: 2026-07-13

## 決定

各業務DBの Trigger は同一DBの `SyncChangeQueue` に変更IDを追加するだけとする。Worker は `QueueId > Checkpoint` で差分を読み、全件ポーリングをしない。

配送ごとの `DeliveryMessageId` は、発生元 MessageId・RouteId・宛先コードから決定的に生成する。宛先 Connector は、業務更新と `SyncAppliedMessage` 追加を同一ローカルトランザクションで行う。Coordinator による更新では SQL Server の `SESSION_CONTEXT` または MySQL の connection user variable に同じ ID を設定し、Trigger がその ID をキューへ引き継ぐ。Worker は `SyncAppliedMessage` に存在するキューを適用済みとして読み飛ばす。

管理DBと業務DB間では分散トランザクションを使わない。障害時は Inbox の再取得と同じ DeliveryMessageId による宛先の冪等適用で回復する。Checkpoint は対象キューの全ルートが完了または意図的に保留された後だけ進める。
