<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\ReflectCommandFixtures\TaggedOnly;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
class TaggedDto
{
    public string $name;
}
