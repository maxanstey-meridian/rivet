<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\SymfonyRouteWalker;
use Rivet\PhpReflector\Tests\Fixtures\AnotherController;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;
use Rivet\PhpReflector\Tests\Fixtures\SymfonyRouteCollectionStub;
use Rivet\PhpReflector\Tests\Fixtures\SymfonyRouteStub;

class SymfonyRouteWalkerTest extends TestCase
{
    public function testFromRouteCollectionExtractsRoutes(): void
    {
        $collection = new SymfonyRouteCollectionStub([
            'order_show' => new SymfonyRouteStub(['GET'], '/orders/{id}', ['_controller' => SampleController::class . '::show']),
            'order_store' => new SymfonyRouteStub(['POST'], '/orders', ['_controller' => SampleController::class . '::store']),
        ]);

        $routes = SymfonyRouteWalker::fromRouteCollection($collection);

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
        $collection = new SymfonyRouteCollectionStub([
            'health' => new SymfonyRouteStub(['GET'], '/health', []),
            'order_show' => new SymfonyRouteStub(['GET'], '/orders/{id}', ['_controller' => SampleController::class . '::show']),
        ]);

        $routes = SymfonyRouteWalker::fromRouteCollection($collection);

        $this->assertCount(1, $routes);
        $this->assertSame('show', $routes[0]['action']);
    }

    public function testFromRouteCollectionHandlesInvokable(): void
    {
        $collection = new SymfonyRouteCollectionStub([
            'dashboard' => new SymfonyRouteStub(['GET'], '/dashboard', ['_controller' => SampleController::class]),
        ]);

        $routes = SymfonyRouteWalker::fromRouteCollection($collection);

        $this->assertCount(1, $routes);
        $this->assertSame(SampleController::class, $routes[0]['controller']);
        $this->assertSame('__invoke', $routes[0]['action']);
    }

    public function testFromRouteCollectionSkipsEmptyMethods(): void
    {
        $collection = new SymfonyRouteCollectionStub([
            'catchall' => new SymfonyRouteStub([], '/catchall', ['_controller' => SampleController::class . '::show']),
            'order_show' => new SymfonyRouteStub(['GET'], '/orders/{id}', ['_controller' => SampleController::class . '::show']),
        ]);

        $routes = SymfonyRouteWalker::fromRouteCollection($collection);

        $this->assertCount(1, $routes);
        $this->assertSame('GET', $routes[0]['httpMethod']);
    }

    public function testWalkProducesSingleEndpoint(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = SymfonyRouteWalker::walk($routes);
        $ep = $result['endpoints'][0];

        $this->assertCount(1, $result['endpoints']);
        $this->assertSame('show', $ep['name']);
        $this->assertSame('GET', $ep['httpMethod']);
        $this->assertSame('/orders/{id}', $ep['routeTemplate']);
        $this->assertSame('sample', $ep['controllerName']);
        $this->assertCount(1, $ep['params']);
        $this->assertSame('id', $ep['params'][0]['name']);
        $this->assertSame('route', $ep['params'][0]['source']);
        $this->assertSame(['kind' => 'ref', 'name' => 'OrderDto'], $ep['returnType']);
    }

    public function testEmptyRoutesReturnsEmptyContract(): void
    {
        $result = SymfonyRouteWalker::walk([]);

        $this->assertSame([], $result['types']);
        $this->assertSame([], $result['enums']);
        $this->assertSame([], $result['endpoints']);
    }

    public function testReferencedTypesWalked(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = SymfonyRouteWalker::walk($routes);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames);

        $enumNames = array_column($result['enums'], 'name');
        $this->assertContains('Status', $enumNames);
    }

    public function testMultipleControllersDeduplicateTypes(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
            ['httpMethod' => 'GET', 'uri' => '/people/{id}', 'controller' => AnotherController::class, 'action' => 'show'],
        ];

        $result = SymfonyRouteWalker::walk($routes);

        $this->assertCount(2, $result['endpoints']);

        $typeNames = array_column($result['types'], 'name');
        $personCount = count(array_filter($typeNames, fn($n) => $n === 'PersonDto'));
        $this->assertSame(1, $personCount);
    }

    public function testCommandPipelineProducesValidJson(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
            ['httpMethod' => 'POST', 'uri' => '/orders', 'controller' => SampleController::class, 'action' => 'store'],
        ];

        $contract = SymfonyRouteWalker::walk($routes);
        $json = ContractEmitter::emit($contract);

        $tmp = sys_get_temp_dir() . '/rivet-symfony-test-' . uniqid() . '.json';
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
