<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class OrderItemDto
{
    public string $productId;

    public int $quantity;

    public float $unitPrice;

    /** @var list<string> */
    public array $tags;

    /** @var array{width: float, height: float, weight?: float} */
    public array $dimensions;

    /** @var 'small'|'medium'|'large' */
    public string $size;
}
