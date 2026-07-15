<?php

namespace App\Providers;

use Illuminate\Support\ServiceProvider;

final class AppServiceProvider extends ServiceProvider
{
    public function register(): void
    {
        // No demo-only service registration is required.
    }

    public function boot(): void
    {
        // The portal deliberately does not run migrations or DDL at startup.
    }
}
