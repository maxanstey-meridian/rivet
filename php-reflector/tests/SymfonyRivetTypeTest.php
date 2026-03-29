<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ClassFinder;
use Rivet\PhpReflector\Symfony\RivetReflectCommand;
use Rivet\PhpReflector\SymfonyRouteWalker;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;
use Rivet\PhpReflector\Tests\Fixtures\SharedConfigDto;
use Rivet\PhpReflector\Tests\Fixtures\SymfonyRouteCollectionStub;
use Rivet\PhpReflector\Tests\Fixtures\SymfonyRouteStub;
use Rivet\PhpReflector\TypeCollector;
use Symfony\Component\Console\Tester\CommandTester;
use Symfony\Component\Routing\RouteCollection;
use Symfony\Component\Routing\RouterInterface;

class SymfonyRivetTypeTest extends TestCase
{
    public function testWalkForwardsExtraFqcnsToContract(): void
    {
        $result = SymfonyRouteWalker::walk([], [SharedConfigDto::class]);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('SharedConfigDto', $typeNames);
    }

    public function testWalkMergesEndpointAndStandaloneTypes(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/orders/{id}', 'controller' => SampleController::class, 'action' => 'show'],
        ];

        $result = SymfonyRouteWalker::walk($routes, [SharedConfigDto::class]);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames);
        $this->assertContains('SharedConfigDto', $typeNames);
    }

    public function testNoDuplicateTypesWhenRivetTypeAlsoReferencedByEndpoint(): void
    {
        $routes = [
            ['httpMethod' => 'POST', 'uri' => '/orders', 'controller' => SampleController::class, 'action' => 'store'],
        ];

        $result = SymfonyRouteWalker::walk($routes, [\Rivet\PhpReflector\Tests\Fixtures\PersonDto::class]);

        $typeNames = array_column($result['types'], 'name');
        $personCount = count(array_filter($typeNames, fn($n) => $n === 'PersonDto'));
        $this->assertSame(1, $personCount);
    }

    public function testDiscoveryPipelineFindsRivetTypesFromDirectory(): void
    {
        $dir = __DIR__ . '/Fixtures';
        $allFqcns = ClassFinder::find($dir);
        $rivetTypes = TypeCollector::collect(...$allFqcns);

        $result = SymfonyRouteWalker::walk([], $rivetTypes);

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

        $result = SymfonyRouteWalker::walk($routes, $rivetTypes);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderDto', $typeNames, 'Route-derived type should be present');
        $this->assertContains('SharedConfigDto', $typeNames, 'RivetType-discovered type should be present');
    }

    public function testCommandAcceptsDirOption(): void
    {
        $router = $this->createMock(RouterInterface::class);
        $router->method('getRouteCollection')->willReturn(new RouteCollection());

        $command = new RivetReflectCommand($router);
        $tester = new CommandTester($command);
        $tester->execute(['--dir' => __DIR__ . '/Fixtures']);

        $this->assertSame(0, $tester->getStatusCode());
        $output = $tester->getDisplay();
        $this->assertStringContainsString('SharedConfigDto', $output);
    }

    public function testCommandWithoutDirSkipsDiscovery(): void
    {
        $collection = new RouteCollection();
        $route = new \Symfony\Component\Routing\Route(
            '/orders/{id}',
            ['_controller' => SampleController::class . '::show']
        );
        $route->setMethods(['GET']);
        $collection->add('order_show', $route);

        $router = $this->createMock(RouterInterface::class);
        $router->method('getRouteCollection')->willReturn($collection);

        $command = new RivetReflectCommand($router);
        $tester = new CommandTester($command);
        $tester->execute([]);

        $this->assertSame(0, $tester->getStatusCode());
        $output = $tester->getDisplay();
        $this->assertStringContainsString('OrderDto', $output);
        $this->assertStringNotContainsString('SharedConfigDto', $output);
    }

    public function testCommandWithDirMergesBothSources(): void
    {
        $collection = new RouteCollection();
        $route = new \Symfony\Component\Routing\Route(
            '/orders/{id}',
            ['_controller' => SampleController::class . '::show']
        );
        $route->setMethods(['GET']);
        $collection->add('order_show', $route);

        $router = $this->createMock(RouterInterface::class);
        $router->method('getRouteCollection')->willReturn($collection);

        $command = new RivetReflectCommand($router);
        $tester = new CommandTester($command);
        $tester->execute(['--dir' => __DIR__ . '/Fixtures']);

        $this->assertSame(0, $tester->getStatusCode());
        $output = $tester->getDisplay();
        $this->assertStringContainsString('OrderDto', $output);
        $this->assertStringContainsString('SharedConfigDto', $output);
    }

    public function testCommandWithInvalidDirReturnsFailure(): void
    {
        $router = $this->createMock(RouterInterface::class);
        $router->method('getRouteCollection')->willReturn(new RouteCollection());

        $command = new RivetReflectCommand($router);
        $tester = new CommandTester($command);
        $tester->execute(['--dir' => '/nonexistent/path']);

        $this->assertSame(1, $tester->getStatusCode());
        $this->assertStringContainsString('/nonexistent/path', $tester->getDisplay());
    }
}
