FROM composer:2.10.2 AS vendor

WORKDIR /app
COPY composer.json composer.lock ./
RUN composer install \
    --no-dev \
    --no-interaction \
    --no-progress \
    --no-scripts \
    --prefer-dist

RUN composer licenses --locked --no-dev --format=json > THIRD-PARTY-LICENSES.json

COPY . .
RUN composer dump-autoload --no-dev --optimize --no-interaction

FROM php:8.4.23-apache-trixie

RUN docker-php-ext-install pdo_mysql \
    && a2enmod rewrite headers

COPY docker/request-limits.conf /etc/apache2/conf-available/request-limits.conf
RUN a2enconf request-limits

ENV APACHE_DOCUMENT_ROOT=/var/www/html/public

RUN sed -ri -e 's!/var/www/html!${APACHE_DOCUMENT_ROOT}!g' \
        /etc/apache2/sites-available/*.conf \
        /etc/apache2/apache2.conf \
        /etc/apache2/conf-available/*.conf \
    && sed -ri -e 's/AllowOverride None/AllowOverride All/g' /etc/apache2/apache2.conf

WORKDIR /var/www/html
COPY --from=vendor /app /var/www/html

RUN php artisan view:cache \
    && cp /usr/share/common-licenses/Apache-2.0 /var/www/html/LICENSE \
    && chown -R www-data:www-data storage bootstrap/cache

EXPOSE 80
