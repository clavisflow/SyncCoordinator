# ADR 0001: ソリューション境界と依存方向

- 状態: Accepted
- 日付: 2026-07-13

## 決定

依存方向を `Contracts ← Core ← Infrastructure ← Worker/Web` とする。`ServiceDefaults` は Worker/Web の横断的な Aspire、OpenTelemetry、Health Check 設定だけを提供し、AppHost は開発時の構成を起動する。

Blazor は管理DBの読み取りと運用操作の入口に限定する。同期ループ、競合処理、Checkpoint 更新は Core のユースケースと Worker に置く。A/B/C の業務DBには EF Core model を持ち込まず、システム別 `ISyncConnector` が Dapper/ADO.NET で変換する。

## 理由

業務テーブル未確定の段階で共有 Entity model を固定せず、システム追加時の変更範囲を Connector とルート設定に閉じ込めるため。
