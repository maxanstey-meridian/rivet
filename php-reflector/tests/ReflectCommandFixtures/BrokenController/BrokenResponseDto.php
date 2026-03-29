<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\ReflectCommandFixtures\BrokenController;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
class BrokenResponseDto
{
    /** @var list<NonExistentClass> */
    public array $items;
}
