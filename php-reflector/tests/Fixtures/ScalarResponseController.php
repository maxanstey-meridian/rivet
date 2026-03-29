<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetResponse;

#[RivetResponse(ScalarDto::class)]
class ScalarResponseController
{
    public function index(): void {}
}
