<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetType;

#[RivetType]
enum AnnotatedEnum: string
{
    case Alpha = 'alpha';
    case Beta = 'beta';
}
