<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\ReflectCommandFixtures\WithControllers;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class SimpleController
{
    #[RivetRoute('GET', '/items/{id}')]
    #[RivetResponse(ResponseDto::class)]
    public function show(int $id): void {}
}
