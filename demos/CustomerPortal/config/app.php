<?php

return [
    'name' => env('APP_NAME', '製品おたすけ窓口'),
    'env' => env('APP_ENV', 'production'),
    'debug' => (bool) env('APP_DEBUG', false),
    'url' => env('APP_URL', 'http://localhost'),
    'timezone' => env('APP_TIMEZONE', 'Asia/Tokyo'),
    'locale' => env('APP_LOCALE', 'ja'),
    'fallback_locale' => env('APP_FALLBACK_LOCALE', 'ja'),
    'faker_locale' => 'ja_JP',
    'cipher' => 'AES-256-CBC',
    'key' => env('APP_KEY'),
    'previous_keys' => array_filter(explode(',', (string) env('APP_PREVIOUS_KEYS', ''))),
    'maintenance' => [
        'driver' => env('APP_MAINTENANCE_DRIVER', 'file'),
        'store' => env('APP_MAINTENANCE_STORE', 'database'),
    ],
];
