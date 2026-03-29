<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\ReflectCommandFixtures\BrokenController;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class ErrorController
{
    #[RivetRoute('GET', '/broken')]
    #[RivetResponse(BrokenResponseDto::class)]
    public function show(): void {}
}
