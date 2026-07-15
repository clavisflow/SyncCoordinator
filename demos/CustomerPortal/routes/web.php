<?php

use App\Http\Controllers\SupportCaseController;
use Illuminate\Support\Facades\Route;

Route::get('/', [SupportCaseController::class, 'index'])->name('home');
Route::get('/support-cases/create', [SupportCaseController::class, 'create'])->name('support-cases.create');
Route::post('/support-cases', [SupportCaseController::class, 'store'])->name('support-cases.store');
Route::get('/support-cases/{id}', [SupportCaseController::class, 'show'])
    ->name('support-cases.show');
Route::get('/support-cases/{id}/edit', [SupportCaseController::class, 'edit'])
    ->name('support-cases.edit');
Route::put('/support-cases/{id}', [SupportCaseController::class, 'update'])
    ->name('support-cases.update');
