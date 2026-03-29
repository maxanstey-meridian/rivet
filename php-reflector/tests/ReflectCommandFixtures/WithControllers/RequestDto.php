<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\ReflectCommandFixtures\WithControllers;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
class RequestDto
{
    public string $label;
}
