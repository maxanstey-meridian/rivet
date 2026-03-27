<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_METHOD | Attribute::TARGET_CLASS)]
class RivetResponse
{
    public function __construct(public readonly string $type) {}
}
