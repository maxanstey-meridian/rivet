<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class OrderItemNestedDto
{
    public string $productId;

    public AddressDto $shippingAddress;
}
