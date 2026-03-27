<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ControllerWalker;
use Rivet\PhpReflector\Tests\Fixtures\NoResponseController;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;

class ControllerWalkerTest extends TestCase
{
    private static array $result;
    private static array $endpoints;

    public static function setUpBeforeClass(): void
    {
        self::$result = ControllerWalker::walk(SampleController::class);
        self::$endpoints = self::$result['endpoints'];
    }

    public function testEmptyControllerReturnsNoEndpoints(): void
    {
        $result = ControllerWalker::walk(NoResponseController::class);

        $this->assertSame([], $result['endpoints']);
        $this->assertSame([], $result['types']);
        $this->assertSame([], $result['enums']);
    }

    public function testExtractsEndpointMetadata(): void
    {
        $this->assertCount(5, self::$endpoints);

        $show = $this->findEndpoint('show');
        $this->assertSame('GET', $show['httpMethod']);
        $this->assertSame('/orders/{id}', $show['routeTemplate']);
        $this->assertSame('sample', $show['controllerName']);
    }

    public function testResponseTypeResolved(): void
    {
        $show = $this->findEndpoint('show');
        $this->assertSame(['kind' => 'ref', 'name' => 'OrderDto'], $show['returnType']);
    }

    public function testResponsesArrayPopulated(): void
    {
        $show = $this->findEndpoint('show');
        $this->assertCount(1, $show['responses']);
        $this->assertSame(200, $show['responses'][0]['statusCode']);
        $this->assertSame(['kind' => 'ref', 'name' => 'OrderDto'], $show['responses'][0]['dataType']);
    }

    public function testMissingResponseIsNull(): void
    {
        $destroy = $this->findEndpoint('destroy');
        $this->assertNull($destroy['returnType']);
    }

    public function testMissingResponseHasEmptyResponses(): void
    {
        $destroy = $this->findEndpoint('destroy');
        $this->assertSame([], $destroy['responses']);
    }

    public function testResponseTypesWalked(): void
    {
        $typeNames = array_column(self::$result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames);

        $enumNames = array_column(self::$result['enums'], 'name');
        $this->assertContains('Status', $enumNames);
    }

    public function testBodyParamTypesWalked(): void
    {
        $typeNames = array_column(self::$result['types'], 'name');
        $this->assertContains('PersonDto', $typeNames);
        $this->assertContains('AddressDto', $typeNames);
    }

    public function testMixedRouteAndBodyParams(): void
    {
        $update = $this->findEndpoint('update');

        $this->assertCount(2, $update['params']);
        $this->assertSame('id', $update['params'][0]['name']);
        $this->assertSame('route', $update['params'][0]['source']);
        $this->assertSame('payload', $update['params'][1]['name']);
        $this->assertSame('body', $update['params'][1]['source']);
    }

    public function testBodyParamDetected(): void
    {
        $store = $this->findEndpoint('store');

        $this->assertCount(1, $store['params']);
        $this->assertSame('payload', $store['params'][0]['name']);
        $this->assertSame('body', $store['params'][0]['source']);
        $this->assertSame(['kind' => 'ref', 'name' => 'PersonDto'], $store['params'][0]['type']);
    }

    public function testQueryParamsExtracted(): void
    {
        $index = $this->findEndpoint('index');

        $this->assertCount(2, $index['params']);
        $this->assertSame('status', $index['params'][0]['name']);
        $this->assertSame('query', $index['params'][0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $index['params'][0]['type']);

        $this->assertSame('page', $index['params'][1]['name']);
        $this->assertSame('query', $index['params'][1]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $index['params'][1]['type']);
    }

    public function testRouteParamsExtracted(): void
    {
        $show = $this->findEndpoint('show');

        $this->assertCount(1, $show['params']);
        $this->assertSame('id', $show['params'][0]['name']);
        $this->assertSame('route', $show['params'][0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $show['params'][0]['type']);
    }

    private function findEndpoint(string $name): array
    {
        foreach (self::$endpoints as $ep) {
            if ($ep['name'] === $name) {
                return $ep;
            }
        }
        $this->fail("Endpoint '$name' not found");
    }
}
