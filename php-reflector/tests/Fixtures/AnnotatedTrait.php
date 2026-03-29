<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
trait AnnotatedTrait
{
    public string $traitProp;
}
