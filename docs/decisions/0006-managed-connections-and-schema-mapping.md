# ADR 0006: 管理DBでの接続情報とスキーママッピング

- 状態: Accepted
- 日付: 2026-07-13

## 決定

システムごとの業務DB接続文字列をASP.NET Core Data Protectionで暗号化し、Coordinator管理DBの`SystemDefinition.ProtectedConnectionString`へ保存する。パスワードは編集画面へ返さず、空欄で保存した場合は既存パスワードを保持する。監査履歴には接続先、DB名、ユーザー名、接続オプションだけを記録し、パスワードと接続文字列全体は記録しない。

SQL ServerとMySQLのテーブル／列／主キーは、対象DBの`INFORMATION_SCHEMA`から読み取る。`SyncChangeQueue`と`SyncAppliedMessage`は業務マッピング候補から除外する。管理画面から対象DBのDDLは実行しない。

テーブルマッピングはルートと実宛先システムの組み合わせごとに保存する。固定宛先ルートはその宛先に限定し、`OriginSystem`ルートはA/Bなど実宛先ごとに別プロファイルを持てる。各プロファイルは同期元／同期先テーブル、列対応、Entity IDを構成するキー列を保持する。

## 暗号鍵の運用

Data ProtectionのApplication Nameは`SyncCoordinator`へ固定する。本番でWebとWorkerが異なるサービスアカウントやホストで動く場合は、`DataProtection:KeyRingPath`へ共有可能かつACLで保護した鍵フォルダーを指定する。鍵は接続情報とセットでバックアップする。

Coordinator管理DBだけを取得しても接続文字列を復号できない構成にする。ただし、管理画面を実行するサービスアカウントは復号できるため、管理画面の認証・認可とサービスアカウント保護は本番導入前の必須事項とする。

## 現時点の境界

保存したマッピングは差し替え可能な実業務Connectorへ渡す設定である。業務テーブルの更新方法、型変換、削除、OriginSystemの保持方法はテーブル構造だけから安全に推測できないため、現在のサンプルConnectorへ動的SQLとして組み込まない。実テーブル確定後に、マッピングと業務固有規則を利用するConnectorをシステム別に実装する。
