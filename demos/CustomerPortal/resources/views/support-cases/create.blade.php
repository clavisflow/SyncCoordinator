@extends('layouts.app')

@section('title', '新しく相談する｜製品おたすけ窓口')

@section('content')
<div class="site-width sub-page">
    <nav class="breadcrumbs" aria-label="パンくず"><a href="{{ route('home', absolute: false) }}">トップ</a><span>›</span><span>新しく相談する</span></nav>
    <div class="page-heading">
        <p class="section-label">ご相談フォーム</p>
        <h1>新しく相談する</h1>
        <p>故障かどうか分からない場合も、そのままの状況をお知らせください。<br>入力した内容は、送信前にいつでも見直せます。</p>
    </div>
    <div class="form-intro"><span aria-hidden="true">i</span><p><strong>分かる範囲でご入力ください</strong>「必須」以外の項目は、空欄のままでも送信できます。</p></div>
    <form class="support-form" method="post" action="{{ route('support-cases.store', absolute: false) }}">
        @csrf
        @include('partials.support-case-form')
    </form>
</div>
@endsection
