# SyncCoordinator working instructions

## Runtime ownership

- Visual Studio、Aspire AppHost、Web、Workerの実行と停止はユーザーが管理する。
- 明示的に依頼されない限り、CodexはAppHost、Web、Workerを起動または停止しない。
- ユーザーが起動中のアプリケーションがある場合、Codexはその環境を画面確認に使用し、別のAppHostを起動しない。
- AppHostの起動を依頼された場合は、起動前に既存のAppHostプロセスとポート`22241`の使用状況を確認する。
- 実行中プロセスによってビルド出力がロックされている場合、Codexはアプリケーションを停止せず、一時的な別出力先へビルドする。
- 明示的に依頼されない限り、CodexはDocker Desktop、Dockerコンテナ、SQL Serverサービスを起動または停止しない。
- プロセスを停止する必要がある場合は、そのプロセスをCodex自身が起動したと確認できる場合を除き、事前にユーザーへ確認する。
