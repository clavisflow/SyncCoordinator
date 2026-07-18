# Customer Portal Demo - Third-Party Notices

Copyright 2026 ClavisFlow

The Customer Portal demo itself is licensed under the Apache License,
Version 2.0. The complete license is distributed as `LICENSE` in the runtime
image.

PHP runtime dependencies are fixed in `composer.lock`. During the image build,
Composer writes the exact production dependency and license inventory to
`THIRD-PARTY-LICENSES.json`. The original license files distributed by those
packages remain under their respective directories in `vendor`.

The runtime image is based on the Docker Official Image
`php:8.4.23-apache-trixie`. PHP, Apache HTTP Server, Debian, and installed OS
packages remain governed by their respective licenses. Package copyright and
license notices are retained under `/usr/share/doc` in the runtime image.
