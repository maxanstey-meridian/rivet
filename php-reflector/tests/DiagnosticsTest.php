<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\Diagnostics;
use Rivet\PhpReflector\PropertyWalker;
use Rivet\PhpReflector\ControllerWalker;
use Rivet\PhpReflector\EndpointBuilder;
use Rivet\PhpReflector\Tests\Fixtures\BrokenRefDto;
use Rivet\PhpReflector\Tests\Fixtures\OrderItemLooseDto;
use Rivet\PhpReflector\Tests\Fixtures\SampleController;

class DiagnosticsTest extends TestCase
{
    public function testWarningCanBeAdded(): void
    {
        $diag = new Diagnostics();
        $diag->warning('Something is off', ['property' => 'foo']);

        $all = $diag->all();
        $this->assertCount(1, $all);
        $this->assertSame('warning', $all[0]['severity']);
        $this->assertSame('Something is off', $all[0]['message']);
        $this->assertSame(['property' => 'foo'], $all[0]['context']);
    }

    public function testErrorCanBeAdded(): void
    {
        $diag = new Diagnostics();
        $diag->error('Something is broken', ['class' => 'Foo']);

        $all = $diag->all();
        $this->assertCount(1, $all);
        $this->assertSame('error', $all[0]['severity']);
        $this->assertSame('Something is broken', $all[0]['message']);
        $this->assertSame(['class' => 'Foo'], $all[0]['context']);
    }

    public function testHasErrorsFalseForWarnings(): void
    {
        $diag = new Diagnostics();
        $diag->warning('just a warning');
        $this->assertFalse($diag->hasErrors());
    }

    public function testHasErrorsTrueWhenError(): void
    {
        $diag = new Diagnostics();
        $diag->warning('a warning');
        $diag->error('an error');
        $this->assertTrue($diag->hasErrors());
    }

    public function testMerge(): void
    {
        $a = new Diagnostics();
        $a->warning('warn from a');

        $b = new Diagnostics();
        $b->error('error from b');

        $a->merge($b);

        $all = $a->all();
        $this->assertCount(2, $all);
        $this->assertSame('warning', $all[0]['severity']);
        $this->assertSame('error', $all[1]['severity']);
        $this->assertTrue($a->hasErrors());
    }

    public function testUntypedArrayDiagnostic(): void
    {
        $result = PropertyWalker::walk(OrderItemLooseDto::class);

        $this->assertArrayHasKey('diagnostics', $result);
        $diag = $result['diagnostics'];
        $this->assertInstanceOf(Diagnostics::class, $diag);

        $all = $diag->all();
        $this->assertCount(1, $all);
        $this->assertSame('warning', $all[0]['severity']);
        $this->assertStringContainsString('extras', $all[0]['message']);
        $this->assertStringContainsString('OrderItemLooseDto', $all[0]['message']);
    }

    public function testUnresolvableRefEmitsWarning(): void
    {
        $result = PropertyWalker::walk(BrokenRefDto::class);

        $diag = $result['diagnostics'];
        $warnings = array_filter($diag->all(), fn ($item) => $item['severity'] === 'warning');
        $this->assertCount(1, $warnings);
        $warning = array_values($warnings)[0];
        $this->assertStringContainsString('NonExistentClass', $warning['message']);
    }

    public function testMissingResponseDiagnostic(): void
    {
        $result = ControllerWalker::walk(SampleController::class);

        $this->assertArrayHasKey('diagnostics', $result);
        $diag = $result['diagnostics'];
        $this->assertInstanceOf(Diagnostics::class, $diag);

        $warnings = array_filter($diag->all(), fn ($item) => $item['severity'] === 'warning');
        $destroyWarnings = array_filter($warnings, fn ($item) => str_contains($item['message'], 'destroy'));
        $this->assertCount(1, $destroyWarnings);

        // The endpoint should still be emitted with empty responses
        $destroyEndpoints = array_filter($result['endpoints'], fn ($ep) => $ep['name'] === 'destroy');
        $this->assertCount(1, $destroyEndpoints);
        $destroy = array_values($destroyEndpoints)[0];
        $this->assertSame([], $destroy['responses']);
    }

    public function testErrorsHaltReflection(): void
    {
        $this->expectException(\RuntimeException::class);
        $this->expectExceptionMessageMatches('/Bad class/');

        $diagnostics = new Diagnostics();
        $diagnostics->error('Bad class ref');
        EndpointBuilder::buildContract([], [], $diagnostics);
    }
}
