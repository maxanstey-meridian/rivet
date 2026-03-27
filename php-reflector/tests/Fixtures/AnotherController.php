<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class AnotherController
{
    #[RivetRoute('GET', '/people/{id}')]
    #[RivetResponse(PersonDto::class)]
    public function show(int $id): void {}
}
