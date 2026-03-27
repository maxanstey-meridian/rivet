<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ClassFinder;
use Rivet\PhpReflector\LaravelRouteWalker;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;
use Rivet\PhpReflector\Tests\Fixtures\SharedConfigDto;
use Rivet\PhpReflector\TypeCollector;

class LaravelRivetTypeTest extends TestCase
{
    public function testWalkForwardsExtraFqcnsToContract(): void
    {
        $result = LaravelRouteWalker::walk([], [SharedConfigDto::class]);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('SharedConfigDto', $typeNames);
    }

    public function testWalkMergesEndpointAndStandaloneTypes(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes, [SharedConfigDto::class]);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames);
        $this->assertContains('SharedConfigDto', $typeNames);
    }

    public function testNoDuplicateTypesWhenRivetTypeAlsoReferencedByEndpoint(): void
    {
        $routes = [
            ['httpMethod' => 'POST', 'uri' => '/orders', 'controller' => SampleController::class, 'action' => 'store'],
        ];

        // PersonDto is referenced by the store endpoint AND passed as extra
        $result = LaravelRouteWalker::walk($routes, [\Rivet\PhpReflector\Tests\Fixtures\PersonDto::class]);

        $typeNames = array_column($result['types'], 'name');
        $personCount = count(array_filter($typeNames, fn($n) => $n === 'PersonDto'));
        $this->assertSame(1, $personCount);
    }

    public function testDiscoveryPipelineFindsRivetTypesFromDirectory(): void
    {
        $dir = __DIR__ . '/Fixtures';
        $allFqcns = ClassFinder::find($dir);
        $rivetTypes = TypeCollector::collect(...$allFqcns);

        $result = LaravelRouteWalker::walk([], $rivetTypes);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('SharedConfigDto', $typeNames);
    }

    public function testDiscoveryPipelineMergesWithRouteTypes(): void
    {
        $dir = __DIR__ . '/Fixtures';
        $allFqcns = ClassFinder::find($dir);
        $rivetTypes = TypeCollector::collect(...$allFqcns);

        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = LaravelRouteWalker::walk($routes, $rivetTypes);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames, 'Route-derived type should be present');
        $this->assertContains('SharedConfigDto', $typeNames, 'RivetType-discovered type should be present');
    }
}
