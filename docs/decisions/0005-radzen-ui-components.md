# ADR 0005: 管理画面のUIコンポーネント

- 状態: Accepted
- 日付: 2026-07-13

## 決定

`SyncCoordinator.Web`は`Radzen.Blazor` 11.1.0を使用する。テーマは商用利用可能な無償の`Software`テーマとし、Radzen Blazor Studio、Premiumテーマ、UI Blocks、App Templatesには依存しない。

一覧、検索、ページングには`RadzenDataGrid`を使用する。システム・ルートの編集にはRadzenの入力、選択、チェックボックス、ボタンを使用する。項目別ポリシーのように空状態から可変行を追加する編集フォームはDataGridへ押し込まず、Radzen入力部品による繰り返しフォームとして実装する。

Blazorコンポーネントは画面表示と入力の収集だけを担当し、設定検証と永続化は従来どおりCoreの`ICoordinatorAdminService`へ委譲する。

## 理由

DB接続、テーブル・列マッピング、同期履歴では、データ量の多い一覧、絞り込み、選択、ダイアログが中心になる。RadzenはこれらをBlazorネイティブのコンポーネントとして提供し、MITライセンスの無償範囲で必要な管理画面を構築できる。

UIライブラリを一つに統一し、複数ライブラリ間のテーマ差、CSS競合、配布サイズの増加を避ける。

## ライセンス運用

`Radzen.Blazor`はMITライセンスである。配布物とリポジトリで著作権表示および許諾文を保持するため、`THIRD-PARTY-NOTICES.md`へライセンス全文を収録する。Premiumテーマを導入する場合は、この決定を見直して契約条件を確認する。
