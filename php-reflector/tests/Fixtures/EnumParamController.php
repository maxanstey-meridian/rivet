<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class EnumParamController
{
    #[RivetRoute('GET', '/orders')]
    #[RivetResponse(OrderDto::class)]
    public function listByStatus(AnnotatedEnum $status): void {}

    #[RivetRoute('GET', '/orders/{priority}')]
    #[RivetResponse(OrderDto::class)]
    public function listByPriority(Priority $priority): void {}

    #[RivetRoute('GET', '/orders/filtered')]
    #[RivetResponse(OrderDto::class)]
    public function filtered(?AnnotatedEnum $status): void {}
}
