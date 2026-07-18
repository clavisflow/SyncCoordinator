<!doctype html>
<html lang="ja">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="description" content="製品の使い方や故障、訪問修理について、分かりやすく相談できるお客様向け窓口です。">
    <meta name="theme-color" content="#fffaf2">
    <title>@yield('title', '製品おたすけ窓口')</title>
    <link rel="stylesheet" href="/css/app.css?v={{ filemtime(public_path('css/app.css')) }}">
</head>
<body>
    <header class="site-header">
        <div class="site-width header-inner">
            <a class="brand" href="{{ route('home', absolute: false) }}" aria-label="製品おたすけ窓口 トップページ">
                <span class="brand-symbol" aria-hidden="true"><i></i></span>
                <span class="brand-copy"><strong>製品おたすけ窓口</strong><small>Customer Portal</small></span>
            </a>
            <div class="header-support">
                <span>サポート時間：9:00〜18:00<br class="mobile-only">（土日祝日を除く）</span>
                <a href="{{ route('support-cases.create', ['topic' => '電話で相談したい'], absolute: false) }}">担当者に相談する</a>
            </div>
        </div>
    </header>

    <main>
        @if (session('success'))
            <div class="site-width flash flash-success" role="status">{{ session('success') }}</div>
        @endif
        @if (session('error'))
            <div class="site-width flash flash-error" role="alert">{{ session('error') }}</div>
        @endif
        @yield('content')
    </main>

    <footer class="site-footer">
        <div class="site-width footer-inner">
            <strong>製品おたすけ窓口</strong>
            <span>サポート時間：9:00〜18:00（土日祝日を除く）</span>
            <small>Customer Portal / MySQL</small>
        </div>
    </footer>
</body>
</html>
