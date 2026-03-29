<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
class OrderItemLooseDto
{
    public string $productId;

    public array $extras;
}
