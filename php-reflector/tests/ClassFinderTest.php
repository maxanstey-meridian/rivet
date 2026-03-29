<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ClassFinder;
use Rivet\PhpReflector\Tests\Fixtures\OrderDto;
use Rivet\PhpReflector\Tests\Fixtures\ScalarDto;
use Rivet\PhpReflector\Tests\Fixtures\Status;

class ClassFinderTest extends TestCase
{
    public function testFindsClassesInDirectory(): void
    {
        $fqcns = ClassFinder::find(__DIR__ . '/Fixtures');

        $this->assertContains(ScalarDto::class, $fqcns);
        $this->assertContains(OrderDto::class, $fqcns);
        $this->assertContains(Status::class, $fqcns);
    }

    public function testFindsEnumsAndClasses(): void
    {
        $fqcns = ClassFinder::find(__DIR__ . '/Fixtures');
        $this->assertNotEmpty($fqcns);
        $this->assertContains(Status::class, $fqcns);
    }

    public function testThrowsForMissingDirectory(): void
    {
        $this->expectException(\InvalidArgumentException::class);
        ClassFinder::find('/nonexistent');
    }
}
