<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetResponse;

class OrderResponseController
{
    #[RivetResponse(OrderDto::class)]
    public function create(): void {}

    #[RivetResponse('array{id: int, status: string, items: list<AddressDto>}')]
    public function list(): void {}

    #[RivetResponse('NonExistent\\FakeClass')]
    public function broken(): void {}

    #[RivetResponse('string')]
    public function raw(): void {}
}
