# ADR 0006: 管理DBでの接続情報とスキーママッピング

- 状態: Accepted
- 日付: 2026-07-13
- 現在仕様: ADR 0008・0009で同期補助テーブルを追加し、保存したマッピングは`ManagedConnectorCatalog`と`MappedRelationalConnector`が動的に使用する

## 決定

システムごとの業務DB接続文字列をASP.NET Core Data Protectionで暗号化し、Coordinator管理DBの`SystemDefinition.ProtectedConnectionString`へ保存する。パスワードは編集画面へ返さず、空欄で保存した場合は既存パスワードを保持する。監査履歴には接続先、DB名、ユーザー名、接続オプションだけを記録し、パスワードと接続文字列全体は記録しない。

SQL Server、MySQL、PostgreSQLのテーブル／列／主キーは、対象DBの`INFORMATION_SCHEMA`から読み取る。`SyncChangeQueue`、`SyncAppliedMessage`、`SyncEntityOrigin`は業務マッピング候補から除外する。設定保存時には対象DBのDDLを実行しない。DDLの確認・反映はADR 0008の独立した承認フローに限定する。

テーブルマッピングは同期ルールごとに保存する。各ルールは固定の送信元と送信先を持ち、同期元／同期先テーブル、列対応、Entity IDを構成するキー列を保持する。列別競合ポリシーも列対応と同じ画面・保存処理で管理し、未指定時はルール既定を使用する。双方向ルールは同じマッピングを逆向きにも使用するため、実Connectorは列対応とキー対応を反転して処理する。

監査列など同期元の値を使用しない書き込みは、通常の列対応と分離した固定値マッピングとして保持する。固定値は書き込み方向と対象列の組み合わせで一意とし、双方向ではForward（送信先へ書く）とReverse（送信元へ戻す）を別設定にする。通常の列対応と固定値で同じ書き込み先列を指定することは許可しない。固定値は文字列として保存し、型変換は実Connectorの責務とする。

## 暗号鍵の運用

Data ProtectionのApplication Nameは`SyncCoordinator`へ固定する。本番でWebとWorkerが異なるサービスアカウントやホストで動く場合は、`DataProtection:KeyRingPath`へ共有可能かつACLで保護した鍵フォルダーを指定する。鍵は接続情報とセットでバックアップする。

Coordinator管理DBだけを取得しても接続文字列を復号できない構成にする。ただし、管理画面を実行するサービスアカウントは復号できるため、管理画面の認証・認可とサービスアカウント保護は本番導入前の必須事項とする。

## 現時点の境界

保存したマッピングは差し替え可能な実業務Connectorへ渡す設定である。業務テーブルの更新方法、型変換、削除、OriginSystemの保持方法はテーブル構造だけから安全に推測できないため、現在のサンプルConnectorへ動的SQLとして組み込まない。実テーブル確定後に、マッピングと業務固有規則を利用するConnectorをシステム別に実装する。
