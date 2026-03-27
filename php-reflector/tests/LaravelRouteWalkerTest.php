<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\LaravelRouteWalker;
use Rivet\PhpReflector\Tests\Fixtures\AnotherController;
use Rivet\PhpReflector\Tests\Fixtures\InlineResponseController;
use Rivet\PhpReflector\Tests\Fixtures\LaravelRouteCollectionStub;
use Rivet\PhpReflector\Tests\Fixtures\LaravelRouteStub;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;

class LaravelRouteWalkerTest extends TestCase
{
    public function testEmptyRoutesReturnsEmptyContract(): void
    {
        $result = LaravelRouteWalker::walk([]);

        $this->assertSame(['types' => [], 'enums' => [], 'endpoints' => []], $result);
    }

    public function testSingleGetRouteProducesEndpoint(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes);
        $endpoints = $result['endpoints'];

        $this->assertCount(1, $endpoints);
        $this->assertSame('show', $endpoints[0]['name']);
        $this->assertSame('GET', $endpoints[0]['httpMethod']);
        $this->assertSame('/orders/{id}', $endpoints[0]['routeTemplate']);
        $this->assertSame('sample', $endpoints[0]['controllerName']);
    }

    public function testRouteParamsExtracted(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes);
        $params = $result['endpoints'][0]['params'];

        $this->assertCount(1, $params);
        $this->assertSame('id', $params[0]['name']);
        $this->assertSame('route', $params[0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $params[0]['type']);
    }

    public function testBodyParamDetected(): void
    {
        $routes = [
            ['httpMethod' => 'POST', 'uri' => '/orders', 'controller' => SampleController::class, 'action' => 'store'],
        ];

        $result = LaravelRouteWalker::walk($routes);
        $params = $result['endpoints'][0]['params'];

        $this->assertCount(1, $params);
        $this->assertSame('payload', $params[0]['name']);
        $this->assertSame('body', $params[0]['source']);
        $this->assertSame(['kind' => 'ref', 'name' => 'PersonDto'], $params[0]['type']);
    }

    public function testQueryParamsExtracted(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders', 'controller' => SampleController::class, 'action' => 'index'],
        ];

        $result = LaravelRouteWalker::walk($routes);
        $params = $result['endpoints'][0]['params'];

        $this->assertCount(2, $params);
        $this->assertSame('status', $params[0]['name']);
        $this->assertSame('query', $params[0]['source']);
        $this->assertSame('page', $params[1]['name']);
        $this->assertSame('query', $params[1]['source']);
    }

    public function testResponseTypeResolved(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes);
        $ep = $result['endpoints'][0];

        $this->assertSame(['kind' => 'ref', 'name' => 'OrderDto'], $ep['returnType']);
        $this->assertCount(1, $ep['responses']);
        $this->assertSame(200, $ep['responses'][0]['statusCode']);
        $this->assertSame(['kind' => 'ref', 'name' => 'OrderDto'], $ep['responses'][0]['dataType']);
    }

    public function testMissingResponseIsNull(): void
    {
        $routes = [
            ['httpMethod' => 'DELETE', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'destroy'],
        ];

        $result = LaravelRouteWalker::walk($routes);
        $ep = $result['endpoints'][0];

        $this->assertNull($ep['returnType']);
        $this->assertSame([], $ep['responses']);
    }

    public function testReferencedTypesWalked(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames);

        $enumNames = array_column($result['enums'], 'name');
        $this->assertContains('Status', $enumNames);
    }

    public function testMultipleControllersWalked(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
            ['httpMethod' => 'GET', 'uri' => '/people/{id}', 'controller' => AnotherController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes);

        $this->assertCount(2, $result['endpoints']);

        // PersonDto referenced by both — should appear only once
        $typeNames = array_column($result['types'], 'name');
        $personCount = count(array_filter($typeNames, fn($n) => $n === 'PersonDto'));
        $this->assertSame(1, $personCount);
    }

    public function testInlineResponseRefsWalked(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/items', 'controller' => InlineResponseController::class, 'action' => 'list'],
        ];

        $result = LaravelRouteWalker::walk($routes);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderItemDto', $typeNames);
    }

    public function testFromRouteCollectionExtractsRoutes(): void
    {
        $collection = new LaravelRouteCollectionStub([
            new LaravelRouteStub(['GET', 'HEAD'], '/orders/{id}', SampleController::class . '@show'),
            new LaravelRouteStub(['POST'], '/orders', SampleController::class . '@store'),
        ]);

        $routes = LaravelRouteWalker::fromRouteCollection($collection);

        $this->assertCount(2, $routes);
        $this->assertSame('GET', $routes[0]['httpMethod']);
        $this->assertSame('/orders/{id}', $routes[0]['uri']);
        $this->assertSame(SampleController::class, $routes[0]['controller']);
        $this->assertSame('show', $routes[0]['action']);
        $this->assertSame('POST', $routes[1]['httpMethod']);
        $this->assertSame('store', $routes[1]['action']);
    }

    public function testFromRouteCollectionSkipsClosures(): void
    {
        $collection = new LaravelRouteCollectionStub([
            new LaravelRouteStub(['GET'], '/health', 'Closure'),
            new LaravelRouteStub(['GET'], '/orders/{id}', SampleController::class . '@show'),
        ]);

        $routes = LaravelRouteWalker::fromRouteCollection($collection);

        $this->assertCount(1, $routes);
        $this->assertSame('show', $routes[0]['action']);
    }

    public function testFromRouteCollectionFiltersHead(): void
    {
        $collection = new LaravelRouteCollectionStub([
            new LaravelRouteStub(['GET', 'HEAD'], '/orders/{id}', SampleController::class . '@show'),
        ]);

        $routes = LaravelRouteWalker::fromRouteCollection($collection);

        $this->assertSame('GET', $routes[0]['httpMethod']);
    }

    public function testFromRouteCollectionHandlesInvokable(): void
    {
        $collection = new LaravelRouteCollectionStub([
            new LaravelRouteStub(['GET'], '/dashboard', SampleController::class),
        ]);

        $routes = LaravelRouteWalker::fromRouteCollection($collection);

        $this->assertCount(1, $routes);
        $this->assertSame(SampleController::class, $routes[0]['controller']);
        $this->assertSame('__invoke', $routes[0]['action']);
    }

    public function testCommandPipelineProducesValidJson(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
            ['httpMethod' => 'POST', 'uri' => '/orders', 'controller' => SampleController::class, 'action' => 'store'],
        ];

        $contract = LaravelRouteWalker::walk($routes);
        $json = ContractEmitter::emit($contract);

        $tmp = sys_get_temp_dir() . '/rivet-laravel-test-' . uniqid() . '.json';
        try {
            file_put_contents($tmp, $json);
            $decoded = json_decode(file_get_contents($tmp), true, 512, JSON_THROW_ON_ERROR);

            $this->assertArrayHasKey('types', $decoded);
            $this->assertArrayHasKey('enums', $decoded);
            $this->assertArrayHasKey('endpoints', $decoded);
            $this->assertCount(2, $decoded['endpoints']);
        } finally {
            @unlink($tmp);
        }
    }
}
