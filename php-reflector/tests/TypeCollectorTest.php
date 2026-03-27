<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\TypeCollector;
use Rivet\PhpReflector\Tests\Fixtures\SharedConfigDto;
use Rivet\PhpReflector\Tests\Fixtures\AnnotatedAbstractDto;
use Rivet\PhpReflector\Tests\Fixtures\AnnotatedEnum;
use Rivet\PhpReflector\Tests\Fixtures\AnnotatedInterface;
use Rivet\PhpReflector\Tests\Fixtures\AnnotatedTrait;
use Rivet\PhpReflector\Tests\Fixtures\ScalarDto;

class TypeCollectorTest extends TestCase
{
    public function testCollectsAnnotatedClasses(): void
    {
        $result = TypeCollector::collect(SharedConfigDto::class, ScalarDto::class);

        $this->assertSame([SharedConfigDto::class], $result);
    }

    public function testReturnsEmptyWhenNoAnnotatedClasses(): void
    {
        $result = TypeCollector::collect(ScalarDto::class);

        $this->assertSame([], $result);
    }

    public function testSkipsEnums(): void
    {
        $result = TypeCollector::collect(AnnotatedEnum::class, SharedConfigDto::class);

        $this->assertSame([SharedConfigDto::class], $result);
    }

    public function testSkipsInterfaces(): void
    {
        $result = TypeCollector::collect(AnnotatedInterface::class, SharedConfigDto::class);

        $this->assertSame([SharedConfigDto::class], $result);
    }

    public function testSkipsNonExistentClasses(): void
    {
        $result = TypeCollector::collect('App\\NonExistent\\Foo', SharedConfigDto::class);

        $this->assertSame([SharedConfigDto::class], $result);
    }

    public function testSkipsAbstractClasses(): void
    {
        $result = TypeCollector::collect(AnnotatedAbstractDto::class, SharedConfigDto::class);

        $this->assertSame([SharedConfigDto::class], $result);
    }

    public function testSkipsTraits(): void
    {
        $result = TypeCollector::collect(AnnotatedTrait::class, SharedConfigDto::class);

        $this->assertSame([SharedConfigDto::class], $result);
    }

    public function testReturnsEmptyForNoInput(): void
    {
        $result = TypeCollector::collect();

        $this->assertSame([], $result);
    }

    public function testDuplicateInputProducesDuplicateOutput(): void
    {
        $result = TypeCollector::collect(SharedConfigDto::class, SharedConfigDto::class);

        $this->assertSame([SharedConfigDto::class, SharedConfigDto::class], $result);
    }
}
