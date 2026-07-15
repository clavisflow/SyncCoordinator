@extends('layouts.app')

@section('title', '製品おたすけ窓口')

@section('content')
<div class="site-width support-home">
    <section class="support-hero" aria-labelledby="hero-title">
        <img src="/images/support-consultation-v1.png" alt="エアコンのリモコンを確認しながら電話で相談するお客様" width="1536" height="1024" fetchpriority="high">
        <div class="hero-copy">
            <span>製品についてのご相談</span>
            <h1 id="hero-title">困ったときは、<br>こちらから。</h1>
            <p>故障かどうか分からなくても大丈夫です。<br>いちばん近い内容を選んでください。</p>
        </div>
    </section>

    <section class="help-start" id="help-topics" aria-labelledby="help-title">
        <div class="help-heading">
            <h2 id="help-title">どのようなことでお困りですか？</h2>
            <p>分からない場合は、いちばん近いものを選んでください。</p>
        </div>

        <div class="choice-grid">
            <a class="help-choice choice-trouble" href="{{ route('support-cases.create', ['topic' => '故障・不具合'], absolute: false) }}">
                <span class="choice-icon icon-trouble" aria-hidden="true"><i></i></span>
                <span class="choice-copy"><strong>製品が動かない・おかしい</strong><small>電源が入らない、音や表示がおかしい</small></span>
                <span class="choice-arrow" aria-hidden="true">›</span>
            </a>
            <a class="help-choice choice-howto" href="{{ route('support-cases.create', ['topic' => '使い方'], absolute: false) }}">
                <span class="choice-icon icon-howto" aria-hidden="true"><i></i></span>
                <span class="choice-copy"><strong>使い方が分からない</strong><small>ボタンや設定について知りたい</small></span>
                <span class="choice-arrow" aria-hidden="true">›</span>
            </a>
            <a class="help-choice choice-visit" href="{{ route('support-cases.create', ['topic' => '訪問修理'], absolute: false) }}">
                <span class="choice-icon icon-visit" aria-hidden="true"><i></i></span>
                <span class="choice-copy"><strong>自宅で見てほしい</strong><small>訪問修理をお願いしたい</small></span>
                <span class="choice-arrow" aria-hidden="true">›</span>
            </a>
            <a class="help-choice choice-unsure" href="{{ route('support-cases.create', ['topic' => 'その他の相談'], absolute: false) }}">
                <span class="choice-icon icon-unsure" aria-hidden="true"><i></i></span>
                <span class="choice-copy"><strong>どれか分からない</strong><small>担当者と相談しながら決めたい</small></span>
                <span class="choice-arrow" aria-hidden="true">›</span>
            </a>
        </div>
    </section>

    <aside class="unsure-panel" aria-label="相談先に迷った場合">
        <div><strong>迷ったときはこちら</strong><span>症状が説明できなくても大丈夫です</span></div>
        <a class="button button-consult" href="{{ route('support-cases.create', ['topic' => '相談しながら決めたい'], absolute: false) }}">担当者に相談する</a>
    </aside>

    <section class="simple-flow" aria-labelledby="flow-title">
        <h2 id="flow-title">ご相談のながれ <small>とてもかんたんです</small></h2>
        <ol>
            <li><b>1</b><span><strong>困っていることを書く</strong><small>分かる範囲で大丈夫です</small></span></li>
            <li><b>2</b><span><strong>担当者から連絡</strong><small>内容を確認してご案内します</small></span></li>
            <li><b>3</b><span><strong>解決までご案内</strong><small>必要に応じて訪問します</small></span></li>
        </ol>
    </section>

    <section class="history-section" id="history" aria-labelledby="history-title">
        <div class="history-heading">
            <h2 id="history-title">{{ count($cases) === 1 ? '前回のご相談' : 'これまでのご相談' }}</h2>
            @if (count($cases) > 0)<span>{{ count($cases) }}件</span>@endif
        </div>

        @if ($errorMessage)
            <div class="message message-error" role="alert">{{ $errorMessage }}</div>
        @elseif (count($cases) === 0)
            <div class="empty-state">
                <h3>まだご相談はありません</h3>
                <p>困ったことがあれば、上の項目から選んでください。</p>
            </div>
        @else
            <div class="case-cards">
                @foreach ($cases as $case)
                    <article class="case-card">
                        <img class="appliance-photo" src="/images/air-conditioner-v1.png" alt="" width="1024" height="1024" loading="lazy">
                        <div class="case-card-copy">
                            <small>{{ $case->value('CaseNumber') ?? '受付番号を確認中' }}</small>
                            <strong>{{ $case->value('ProductName') ?? '製品名未登録' }}</strong>
                            <span>{{ $case->value('Subject') ?? 'ご相談内容' }}</span>
                        </div>
                        <span class="status {{ $case->statusClass() }}">{{ $case->statusLabel() }}</span>
                        <a class="button button-outline" href="{{ route('support-cases.show', $case->entityId, absolute: false) }}">内容を見る</a>
                    </article>
                @endforeach
            </div>
        @endif
    </section>
</div>
@endsection
