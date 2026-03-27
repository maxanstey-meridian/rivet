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

    public function testNonAutoloadedDirectoryRequiresFileLoading(): void
    {
        $dir = sys_get_temp_dir() . '/rivet_test_' . uniqid();
        mkdir($dir, 0755, true);
        $className = 'TempRivetDto_' . uniqid();
        $fqcn = 'Rivet\\TempTest\\' . $className;
        file_put_contents($dir . '/' . $className . '.php', <<<PHP
        <?php
        namespace Rivet\\TempTest;
        use Rivet\\PhpReflector\\Attribute\\RivetType;
        #[RivetType]
        class {$className} {
            public string \$value;
        }
        PHP);

        try {
            $allFqcns = ClassFinder::find($dir);
            $this->assertContains($fqcn, $allFqcns, 'ClassFinder should tokenize the class');

            // Without require_once, TypeCollector silently skips (class not loaded)
            $withoutLoad = TypeCollector::collect(...$allFqcns);
            $this->assertSame([], $withoutLoad, 'TypeCollector should find nothing without loading');

            // After require_once, TypeCollector should discover it
            require_once $dir . '/' . $className . '.php';
            $withLoad = TypeCollector::collect(...$allFqcns);
            $this->assertContains($fqcn, $withLoad, 'TypeCollector should find class after loading');
        } finally {
            @unlink($dir . '/' . $className . '.php');
            @rmdir($dir);
        }
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
