<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ResponseResolver;
use Rivet\PhpReflector\Tests\Fixtures\NoRouteController;
use Rivet\PhpReflector\Tests\Fixtures\OrderResponseController;
use Rivet\PhpReflector\Tests\Fixtures\ScalarResponseController;

class ResponseResolverTest extends TestCase
{
    public function testMissingAttributeReturnsNull(): void
    {
        $method = new \ReflectionMethod(NoRouteController::class, 'index');
        $result = ResponseResolver::resolve($method);

        $this->assertNull($result);
    }

    public function testClassModeResolvesToRef(): void
    {
        $method = new \ReflectionMethod(OrderResponseController::class, 'create');
        $result = ResponseResolver::resolve($method);

        $this->assertSame(['kind' => 'ref', 'name' => 'OrderDto'], $result);
    }

    public function testInlineShapeParsesShape(): void
    {
        $method = new \ReflectionMethod(OrderResponseController::class, 'list');
        $result = ResponseResolver::resolve($method);

        $this->assertSame('inlineObject', $result['kind']);
        $this->assertCount(3, $result['properties']);
        $this->assertSame('id', $result['properties'][0]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $result['properties'][0]['type']);
        $this->assertSame('status', $result['properties'][1]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $result['properties'][1]['type']);
        $this->assertSame('items', $result['properties'][2]['name']);
        $this->assertSame(['kind' => 'array', 'element' => ['kind' => 'ref', 'name' => 'AddressDto']], $result['properties'][2]['type']);
    }

    public function testClassLevelAttribute(): void
    {
        $class = new \ReflectionClass(ScalarResponseController::class);
        $result = ResponseResolver::resolve($class);

        $this->assertSame(['kind' => 'ref', 'name' => 'ScalarDto'], $result);
    }

    public function testInvalidClassThrows(): void
    {
        $method = new \ReflectionMethod(OrderResponseController::class, 'broken');

        $this->expectException(\InvalidArgumentException::class);
        $this->expectExceptionMessage('class not found');

        ResponseResolver::resolve($method);
    }

    public function testInlinePrimitiveType(): void
    {
        $method = new \ReflectionMethod(OrderResponseController::class, 'raw');
        $result = ResponseResolver::resolve($method);

        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $result);
    }

    public function testUnderscoredClassNameThrows(): void
    {
        $method = new \ReflectionMethod(OrderResponseController::class, 'underscored');

        $this->expectException(\InvalidArgumentException::class);
        $this->expectExceptionMessage('class not found');

        ResponseResolver::resolve($method);
    }
}
