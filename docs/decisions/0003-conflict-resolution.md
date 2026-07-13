# ADR 0003: 3-way 項目比較による競合処理

- 状態: Accepted
- 日付: 2026-07-13

## 決定

前回採用 payload を base、今回の発生元 payload を incoming、現在の宛先 payload を current として項目単位で比較する。片側だけの変更は自動マージし、両側が base から変わり値が異なる場合だけ競合とする。

ルート既定または項目別に `HoldAndNotify`、`ApplyIncomingAndNotify`、`KeepCurrentAndNotify`、`MergeAndNotify` を選べる。Field scope は非競合項目を保存でき、Record scope の hold はレコード全体を保存しない。競合履歴には base/incoming/current/adopted と実施ポリシーを JSON で保持する。

業務意味を推測したマージは行わない。`MergeAndNotify` は `IConflictValueMerger` のシステム固有実装を必要とし、未登録なら保留にフォールバックして通知対象とする。
