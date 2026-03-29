<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Enums;

enum Priority: int
{
    case Low = 1;
    case Medium = 2;
    case High = 3;
}
