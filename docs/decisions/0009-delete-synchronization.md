# ADR 0009: 物理削除と論理削除の同期

- 状態: Accepted
- 日付: 2026-07-14

## 決定

削除同期はテーブルマッピング単位で有効化し、同期元と同期先それぞれに`Physical`または`Logical`の削除方式を設定する。論理削除では対象列と削除時の値を必須とする。これにより、一方の物理DELETEをもう一方の論理削除へ変換できる。

物理DELETE Triggerは削除前の共通payload、EntityId、OriginSystem、MessageIdを`SyncDeleteTombstone`へ保存し、同じMessageIdで`SyncChangeQueue`へ`Delete`を追加する。論理削除Triggerは指定列が削除値へ遷移したとき、更新前payloadをTombstoneへ保存して`Delete`を追加する。論理削除状態から戻った更新は通常の`Upsert`として扱う。

WorkerはDeleteをレコード単位の操作として扱う。送信先現在値が前回同期スナップショットと一致すれば設定された方式で削除する。送信先が変更されている場合は項目別ポリシーを評価するが、DELETEは部分適用できないため、全競合項目が`ApplyIncomingAndNotify`の場合だけ削除する。`KeepCurrentAndNotify`はレコードを維持し、`HoldAndNotify`と削除時の`MergeAndNotify`は保留する。競合前後と採用値は通常の競合履歴へ保存する。

Coordinatorが適用した物理・論理削除も`SyncAppliedMessage`とMessageIdコンテキストで同期ループを防止する。Tombstoneは再送可能期間中保持し、CheckpointとInboxの完了を考慮した保守ジョブで削除する。

## 理由

IDだけのキューでは物理削除後にpayloadとOriginSystemを復元できない。また、業務システムごとに削除表現が異なるため、同期ルール全体で単一方式を固定せず、テーブルの両端に削除方式を持たせる必要がある。
