@php
    $editing = isset($case);
@endphp

@if ($errors->any())
    <div class="message message-error" role="alert">
        <strong>入力内容をご確認ください。</strong>
        <ul>
            @foreach ($errors->all() as $error)
                <li>{{ $error }}</li>
            @endforeach
        </ul>
    </div>
@endif

<div class="form-section">
    <div class="form-section-heading"><span>1</span><div><h2>お客様について</h2><p>担当者から連絡するために使用します。</p></div></div>
    <div class="form-grid two-columns">
        <label class="field"><span>お名前 <em>必須</em></span><input name="customer_name" type="text" maxlength="100" autocomplete="name" required value="{{ old('customer_name', $editing ? $case->value('CustomerName') : '') }}"></label>
        <label class="field"><span>メールアドレス <em>必須</em></span><input name="email" type="email" maxlength="200" autocomplete="email" required value="{{ old('email', $editing ? $case->value('Email') : '') }}"></label>
        <label class="field"><span>電話番号</span><input name="phone" type="tel" maxlength="30" autocomplete="tel" placeholder="例：090-1234-5678" value="{{ old('phone', $editing ? $case->value('Phone') : '') }}"></label>
    </div>
</div>

<div class="form-section">
    <div class="form-section-heading"><span>2</span><div><h2>製品について</h2><p>分かる範囲で入力してください。</p></div></div>
    <div class="form-grid two-columns">
        <label class="field"><span>製品名 <em>必須</em></span><input name="product_name" type="text" maxlength="150" required placeholder="例：エアコン AC-200" value="{{ old('product_name', $editing ? $case->value('ProductName') : '') }}"></label>
        <label class="field"><span>製造番号</span><input name="serial_number" type="text" maxlength="100" placeholder="本体や保証書に記載されています" value="{{ old('serial_number', $editing ? $case->value('SerialNumber') : '') }}"></label>
    </div>
</div>

<div class="form-section">
    <div class="form-section-heading"><span>3</span><div><h2>お困りのこと</h2><p>症状や状況を、そのままお書きください。</p></div></div>
    <div class="form-grid">
        <label class="field"><span>相談のタイトル <em>必須</em></span><input name="subject" type="text" maxlength="150" required placeholder="例：冷たい風が出ない" value="{{ old('subject', $editing ? $case->value('Subject') : request('topic')) }}"></label>
        <label class="field"><span>困っていること・症状 <em>必須</em></span><textarea name="description" rows="6" maxlength="4000" required placeholder="いつから、どのような状態かなどをご記入ください。">{{ old('description', $editing ? $case->value('Description') : '') }}</textarea></label>
        <label class="field field-date"><span>訪問希望日</span><input name="preferred_visit_date" type="date" value="{{ old('preferred_visit_date', $editing ? $case->value('PreferredVisitDate') : '') }}"><small>訪問が必要な場合のご希望です。確定日時は担当者からご連絡します。</small></label>
    </div>
</div>

<div class="form-actions">
    <a class="button button-quiet" href="{{ $editing ? route('support-cases.show', $case->entityId, absolute: false) : route('home', absolute: false) }}">戻る</a>
    <button class="button button-primary" type="submit">{{ $editing ? '変更内容を保存する' : 'この内容で相談する' }}</button>
</div>
