<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class InlineResponseController
{
    #[RivetRoute('GET', '/items')]
    #[RivetResponse('array{items: list<OrderItemDto>}')]
    public function list(): void {}
}
