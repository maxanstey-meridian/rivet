<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_METHOD)]
class RivetRoute
{
    public function __construct(
        public readonly string $method,
        public readonly string $route,
    ) {}
}
