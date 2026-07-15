<?php

namespace App\Http\Controllers;

use App\Repositories\SupportCaseRepository;
use Illuminate\Contracts\View\View;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Log;
use Throwable;

final class SupportCaseController
{
    public function __construct(private readonly SupportCaseRepository $repository)
    {
    }

    public function index(): View
    {
        try {
            $cases = $this->repository->all();
            $errorMessage = null;
        } catch (Throwable $exception) {
            Log::error('Customer Portal failed to load support cases.', ['exception' => $exception]);
            $cases = [];
            $errorMessage = 'ただいま相談履歴を読み込めません。しばらくしてから、もう一度お試しください。';
        }

        return view('home', compact('cases', 'errorMessage'));
    }

    public function create(): View
    {
        return view('support-cases.create');
    }

    public function store(Request $request): RedirectResponse
    {
        $validated = $request->validate($this->rules(), $this->messages(), $this->attributes());

        try {
            $entityId = $this->repository->create($validated);
        } catch (Throwable $exception) {
            Log::error('Customer Portal failed to create a support case.', ['exception' => $exception]);

            return back()
                ->withInput()
                ->with('error', '相談を送信できませんでした。入力内容を残したまま戻りましたので、時間をおいてお試しください。');
        }

        return redirect()
            ->route('support-cases.show', $entityId)
            ->with('success', 'ご相談を受け付けました。担当者からの連絡をお待ちください。');
    }

    public function show(string $id): View
    {
        return view('support-cases.show', ['case' => $this->findOrFail($id)]);
    }

    public function edit(string $id): View
    {
        return view('support-cases.edit', ['case' => $this->findOrFail($id)]);
    }

    public function update(Request $request, string $id): RedirectResponse
    {
        $validated = $request->validate($this->rules(), $this->messages(), $this->attributes());

        try {
            $updated = $this->repository->update($id, $validated);
        } catch (Throwable $exception) {
            Log::error('Customer Portal failed to update a support case.', [
                'entity_id' => $id,
                'exception' => $exception,
            ]);

            return back()
                ->withInput()
                ->with('error', '相談内容を更新できませんでした。時間をおいてお試しください。');
        }

        abort_if(! $updated, 404);

        return redirect()
            ->route('support-cases.show', $id)
            ->with('success', '相談内容を更新しました。');
    }

    private function findOrFail(string $id): \App\Support\SupportCaseRecord
    {
        try {
            $case = $this->repository->find($id);
        } catch (Throwable $exception) {
            Log::error('Customer Portal failed to load a support case.', [
                'entity_id' => $id,
                'exception' => $exception,
            ]);
            abort(503, '相談内容を読み込めませんでした。');
        }

        abort_if($case === null, 404);

        return $case;
    }

    /** @return array<string, list<string>> */
    private function rules(): array
    {
        return [
            'customer_name' => ['required', 'string', 'max:100'],
            'email' => ['required', 'email', 'max:200'],
            'phone' => ['nullable', 'string', 'max:30'],
            'product_name' => ['required', 'string', 'max:150'],
            'serial_number' => ['nullable', 'string', 'max:100'],
            'subject' => ['required', 'string', 'max:150'],
            'description' => ['required', 'string', 'max:4000'],
            'preferred_visit_date' => ['nullable', 'date_format:Y-m-d'],
        ];
    }

    /** @return array<string, string> */
    private function attributes(): array
    {
        return [
            'customer_name' => 'お名前',
            'email' => 'メールアドレス',
            'phone' => '電話番号',
            'product_name' => '製品名',
            'serial_number' => '製造番号',
            'subject' => '相談のタイトル',
            'description' => '困っていること・症状',
            'preferred_visit_date' => '訪問希望日',
        ];
    }

    /** @return array<string, string> */
    private function messages(): array
    {
        return [
            'required' => ':attributeを入力してください。',
            'email' => ':attributeの形式を確認してください。',
            'max' => ':attributeは:max文字以内で入力してください。',
            'date_format' => ':attributeを正しい日付で入力してください。',
        ];
    }
}
