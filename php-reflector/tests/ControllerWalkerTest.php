<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ControllerWalker;
use Rivet\PhpReflector\Tests\Fixtures\AnotherController;
use Rivet\PhpReflector\Tests\Fixtures\EnumParamController;
use Rivet\PhpReflector\Tests\Fixtures\InlineResponseController;
use Rivet\PhpReflector\Tests\Fixtures\NoRouteController;
use Rivet\PhpReflector\Tests\Fixtures\OrderDto;
use Rivet\PhpReflector\Tests\Fixtures\PersonDto;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;

class ControllerWalkerTest extends TestCase
{
    private static array $result;
    private static array $endpoints;

    public static function setUpBeforeClass(): void
    {
        self::$result = ControllerWalker::walk([SampleController::class]);
        self::$endpoints = self::$result['endpoints'];
    }

    public function testEmptyControllerReturnsNoEndpoints(): void
    {
        $result = ControllerWalker::walk([NoRouteController::class]);

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

    public function testMultipleControllersWalked(): void
    {
        $result = ControllerWalker::walk([SampleController::class, AnotherController::class]);

        // Endpoints from both controllers
        $endpointNames = array_column($result['endpoints'], 'name');
        $this->assertContains('show', $endpointNames);  // from SampleController
        $this->assertContains('store', $endpointNames); // from SampleController
        $this->assertCount(6, $result['endpoints']);     // 5 from Sample + 1 from Another

        // Shared DTOs deduplicated (PersonDto referenced by both)
        $typeNames = array_column($result['types'], 'name');
        $personCount = count(array_filter($typeNames, fn($n) => $n === 'PersonDto'));
        $this->assertSame(1, $personCount);

        // Transitive types from both controllers present
        $this->assertContains('AddressDto', $typeNames);  // transitive from PersonDto
        $this->assertContains('OrderDto', $typeNames);     // from SampleController response
    }

    public function testInlineResponseShapeRefsWalked(): void
    {
        $result = ControllerWalker::walk([InlineResponseController::class]);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderItemDto', $typeNames);
    }

    public function testExtraFqcnOnlyTypeAppearsInOutput(): void
    {
        // No endpoints exist on NoRouteController, but OrderDto should appear via extraFqcns
        $result = ControllerWalker::walk([NoRouteController::class], [OrderDto::class]);

        $this->assertSame([], $result['endpoints']);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames);

        $enumNames = array_column($result['enums'], 'name');
        $this->assertContains('Status', $enumNames);
    }

    public function testExtraFqcnDeduplicatedWithEndpointRef(): void
    {
        // PersonDto is already referenced by SampleController's store and update endpoints
        $result = ControllerWalker::walk([SampleController::class], [PersonDto::class]);

        // PersonDto appears exactly once (deduplication works)
        $typeNames = array_column($result['types'], 'name');
        $personCount = count(array_filter($typeNames, fn($n) => $n === 'PersonDto'));
        $this->assertSame(1, $personCount);

        // Transitive walking still works
        $this->assertContains('AddressDto', $typeNames);

        // Endpoint count unchanged
        $this->assertCount(5, $result['endpoints']);
    }

    public function testStringEnumParamClassifiedAsQuery(): void
    {
        $result = ControllerWalker::walk([EnumParamController::class]);
        $ep = $this->findEndpointIn($result['endpoints'], 'listByStatus');

        $this->assertCount(1, $ep['params']);
        $this->assertSame('status', $ep['params'][0]['name']);
        $this->assertSame('query', $ep['params'][0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $ep['params'][0]['type']);
    }

    public function testIntEnumParamClassifiedAsRoute(): void
    {
        $result = ControllerWalker::walk([EnumParamController::class]);
        $ep = $this->findEndpointIn($result['endpoints'], 'listByPriority');

        $this->assertCount(1, $ep['params']);
        $this->assertSame('priority', $ep['params'][0]['name']);
        $this->assertSame('route', $ep['params'][0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $ep['params'][0]['type']);
    }

    public function testNullableEnumParamWrapped(): void
    {
        $result = ControllerWalker::walk([EnumParamController::class]);
        $ep = $this->findEndpointIn($result['endpoints'], 'filtered');

        $this->assertCount(1, $ep['params']);
        $this->assertSame('status', $ep['params'][0]['name']);
        $this->assertSame('query', $ep['params'][0]['source']);
        $this->assertSame([
            'kind' => 'nullable',
            'inner' => ['kind' => 'primitive', 'type' => 'string'],
        ], $ep['params'][0]['type']);
    }

    private function findEndpointIn(array $endpoints, string $name): array
    {
        foreach ($endpoints as $ep) {
            if ($ep['name'] === $name) {
                return $ep;
            }
        }
        $this->fail("Endpoint '$name' not found");
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
