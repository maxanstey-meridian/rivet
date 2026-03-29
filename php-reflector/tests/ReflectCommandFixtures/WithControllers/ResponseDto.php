<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\ReflectCommandFixtures\WithControllers;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
class ResponseDto
{
    public string $title;
    public int $count;
}
